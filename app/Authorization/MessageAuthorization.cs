using System.Globalization;
using Unbreakable.Auth;
using Unbreakable.Models;

namespace Unbreakable.Authorization;

// Policy Enforcement Point (PEP) voor berichten.
//
// Hier staan GEEN regels. Deze klasse stelt enkel vragen aan de PDP (OpenFGA)
// en geeft de PDP de feiten door (wie afzender en ontvanger is). Wie wat mag,
// staat uitsluitend in openfga/model.fga:
//   can_read   = recipient
//   can_delete = sender
public sealed class MessageAuthorization
{
    // Namen uit het model. Een rename in het model betekent hier een rename, geen nieuwe regel.
    public const string ObjectType = "message";
    public const string RelationSender = "sender";
    public const string RelationRecipient = "recipient";
    public const string PermissionRead = "can_read";
    public const string PermissionDelete = "can_delete";

    private readonly OpenFgaClient _fga;
    private readonly ILogger<MessageAuthorization> _logger;

    public MessageAuthorization(OpenFgaClient fga, ILogger<MessageAuthorization> logger)
    {
        _fga = fga;
        _logger = logger;
    }

    public static string ObjectId(int messageId) =>
        ObjectType + ":" + messageId.ToString(CultureInfo.InvariantCulture);

    public static string UserId(string sub) => "user:" + sub;

    // ------------------------------------------------------------ beslissingen

    public Task<bool> CanReadAsync(CurrentUser user, int messageId, CancellationToken ct) =>
        CheckAsync(user, PermissionRead, messageId, ct);

    public Task<bool> CanDeleteAsync(CurrentUser user, int messageId, CancellationToken ct) =>
        CheckAsync(user, PermissionDelete, messageId, ct);

    private async Task<bool> CheckAsync(CurrentUser user, string permission, int messageId, CancellationToken ct)
    {
        var obj = ObjectId(messageId);
        var allowed = await _fga.CheckAsync(UserId(user.Sub), permission, obj, ct);

        // Audit log van elke beslissing (wie, wat, welk object, resultaat).
        _logger.LogInformation("AUTHZ {Decision} user={User} sub={Sub} permission={Permission} object={Object}",
            allowed ? "ALLOW" : "DENY", user.Username, user.Sub, permission, obj);
        return allowed;
    }

    // ------------------------------------------------------------ feiten (tuples)

    // Zorgt dat in OpenFGA exact staat wie afzender en ontvanger is. Idempotent:
    // eerst lezen, dan enkel het verschil schrijven. Veilig om te herhalen na een
    // fout of een time-out.
    public async Task EnsureMessageTuplesAsync(Message message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.SenderId) || string.IsNullOrEmpty(message.RecipientId))
        {
            throw new InvalidOperationException($"Bericht {message.Id} heeft geen afzender- of ontvanger-id.");
        }

        var obj = ObjectId(message.Id);
        var desired = new HashSet<FgaTuple>
        {
            new(UserId(message.SenderId), RelationSender, obj),
            new(UserId(message.RecipientId), RelationRecipient, obj)
        };

        var existing = await _fga.ReadAsync(obj, ct);
        var existingSet = new HashSet<FgaTuple>(existing);

        var writes = desired.Where(t => !existingSet.Contains(t)).ToList();
        // Alles wat er voor dit object staat maar niet hoort, gaat weg (bv. na een correctie).
        var deletes = existingSet.Where(t => !desired.Contains(t)).ToList();

        await _fga.WriteAsync(writes, deletes, ct);

        if (writes.Count > 0 || deletes.Count > 0)
        {
            _logger.LogInformation("FGA tuples voor {Object}: {Writes} geschreven, {Deletes} verwijderd",
                obj, writes.Count, deletes.Count);
        }
    }

    public async Task RemoveMessageTuplesAsync(int messageId, CancellationToken ct)
    {
        var obj = ObjectId(messageId);
        var existing = await _fga.ReadAsync(obj, ct);
        await _fga.WriteAsync(Array.Empty<FgaTuple>(), existing.ToList(), ct);
    }
}
