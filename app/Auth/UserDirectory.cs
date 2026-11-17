using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Unbreakable.Data;
using Unbreakable.Models;

namespace Unbreakable.Auth;

// Houdt het gebruikersregister (sub <-> naam) bij. Enkel gevoed met gegevens
// uit een gevalideerd token, dus niemand kan zich zo als iemand anders laten
// registreren.
public sealed class UserDirectory
{
    private readonly UnbreakableContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UserDirectory> _logger;

    public UserDirectory(UnbreakableContext db, IMemoryCache cache, ILogger<UserDirectory> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    // Wordt bij elk geauthenticeerd verzoek aangeroepen; schrijft hoogstens
    // eens per 5 minuten per gebruiker naar de databank.
    public async Task TouchAsync(CurrentUser user, CancellationToken ct)
    {
        var cacheKey = "user-touch:" + user.Sub + ":" + user.Username;
        if (_cache.TryGetValue(cacheKey, out _))
        {
            return;
        }

        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Werd deze naam ooit door een andere (verwijderde) account gebruikt?
            // Dan hoort de naam nu bij deze sub. Oude berichten blijven bij de oude
            // sub: de autorisatie hangt aan sub, niet aan de naam.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""DELETE FROM "Users" WHERE "Username" = {user.Username} AND "Sub" <> {user.Sub}""", ct);

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "Users" ("Sub", "Username", "LastSeen")
                 VALUES ({user.Sub}, {user.Username}, {DateTime.UtcNow})
                 ON CONFLICT ("Sub") DO UPDATE SET "Username" = EXCLUDED."Username", "LastSeen" = EXCLUDED."LastSeen"
                 """, ct);

            await tx.CommitAsync(ct);
            _cache.Set(cacheKey, true, TimeSpan.FromMinutes(5));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Het register is geen beveiligingsbeslissing; een fout hier mag het verzoek niet breken.
            _logger.LogWarning("Gebruikersregister bijwerken mislukt voor {Sub}: {Error}", user.Sub, ex.Message);
        }
    }

    public Task<AppUser> FindByUsernameAsync(string username, CancellationToken ct)
    {
        var normalized = (username ?? "").Trim().ToLowerInvariant();
        return _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == normalized, ct);
    }
}
