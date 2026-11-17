using Unbreakable.Models;

namespace Unbreakable.Data;

public static class SeedData
{
    // Vaste 'sub' waarden. Dezelfde id's staan in keycloak/unbreakable-realm.json,
    // zodat de testgebruikers in Keycloak meteen bij deze berichten horen.
    public const string AnnaSub = "0b8a6a3e-5f2c-4c1e-9a51-3d6f1e2a7c01";
    public const string BramSub = "1c9b7b4f-6a3d-4d2f-8b62-4e7a2f3b8d02";
    public const string ChrisSub = "2dac8c5a-7b4e-4e3a-9c73-5f8b3a4c9e03";

    public static void Initialize(UnbreakableContext context)
    {
        var now = DateTime.UtcNow;

        // Gebruikers enkel toevoegen als ze nog niet bestaan (ook in een bestaande databank).
        AddUserIfMissing(context, AnnaSub, "anna", now);
        AddUserIfMissing(context, BramSub, "bram", now);
        AddUserIfMissing(context, ChrisSub, "chris", now);
        context.SaveChanges();

        if (context.Messages.Any())
        {
            return;
        }

        context.Messages.AddRange(
            NewMessage("bram", BramSub, "anna", AnnaSub, "Welkom bij Unbreakable",
                "Hoi Anna, vanaf nu sturen we alles via Unbreakable. Niemand anders kan meelezen.", now.AddDays(-3)),
            NewMessage("anna", AnnaSub, "bram", BramSub, "Re: Welkom bij Unbreakable",
                "Top! Is dat echt zo? Wie heeft dat gecontroleerd?", now.AddDays(-3).AddHours(2)),
            NewMessage("chris", ChrisSub, "anna", AnnaSub, "Afspraak vrijdag",
                "Zien we elkaar vrijdag om 14 uur aan het station?", now.AddDays(-1)),
            NewMessage("bram", BramSub, "chris", ChrisSub, "Wachtwoord van de wifi",
                "Het wachtwoord van de wifi op kantoor is ZomerZon2026. Niet doorgeven!", now.AddHours(-5)),
            NewMessage("anna", AnnaSub, "chris", ChrisSub, "Vrijdag",
                "Prima, ik neem de documenten mee.", now.AddHours(-2)));

        context.SaveChanges();
    }

    private static void AddUserIfMissing(UnbreakableContext context, string sub, string username, DateTime now)
    {
        if (context.Users.Any(u => u.Sub == sub || u.Username == username))
        {
            return;
        }

        context.Users.Add(new AppUser { Sub = sub, Username = username, LastSeen = now });
    }

    private static Message NewMessage(string sender, string senderId, string recipient, string recipientId,
        string subject, string body, DateTime sentAt) => new()
    {
        Sender = sender,
        SenderId = senderId,
        Recipient = recipient,
        RecipientId = recipientId,
        Subject = subject,
        Body = body,
        SentAt = sentAt
    };
}
