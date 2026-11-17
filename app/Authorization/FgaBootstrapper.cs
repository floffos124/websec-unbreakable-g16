using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Unbreakable.Data;
using Unbreakable.Models;

namespace Unbreakable.Authorization;

// Zorgt dat er een OpenFGA store is met het juiste autorisatiemodel.
//
// - Geen FgaState rij, of de store bestaat niet meer: nieuwe store, model
//   schrijven en alle berichten opnieuw laten synchroniseren.
// - Model gewijzigd (andere hash): nieuwe modelversie in dezelfde store.
// - Anders: bestaande store en model gebruiken.
public sealed class FgaBootstrapper
{
    private readonly OpenFgaClient _fga;
    private readonly FgaStoreState _state;
    private readonly OpenFgaOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FgaBootstrapper> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FgaBootstrapper(OpenFgaClient fga, FgaStoreState state, OpenFgaOptions options,
        IServiceScopeFactory scopeFactory, ILogger<FgaBootstrapper> logger)
    {
        _fga = fga;
        _state = state;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.IsReady)
            {
                return;
            }

            var (modelJson, modelHash) = LoadModel();

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<UnbreakableContext>();
            var row = await db.FgaStates.SingleOrDefaultAsync(s => s.Id == 1, ct);

            if (row != null && await _fga.StoreExistsAsync(row.StoreId, ct))
            {
                if (!string.Equals(row.ModelHash, modelHash, StringComparison.Ordinal))
                {
                    var newModelId = await _fga.WriteAuthorizationModelAsync(row.StoreId, modelJson, ct);
                    _logger.LogInformation("Nieuw autorisatiemodel {ModelId} in store {StoreId}", newModelId, row.StoreId);
                    row.ModelId = newModelId;
                    row.ModelHash = modelHash;
                    await db.SaveChangesAsync(ct);
                }

                _state.Set(row.StoreId, row.ModelId);
                _logger.LogInformation("OpenFGA klaar: store {StoreId}, model {ModelId}", row.StoreId, row.ModelId);
                return;
            }

            // Nieuwe store. Nooit een bestaande store op naam hergebruiken: die kan
            // tuples bevatten voor bericht-id's die in deze databank iets anders zijn.
            var storeName = $"{_options.StoreName}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var storeId = await _fga.CreateStoreAsync(storeName, ct);
            var modelId = await _fga.WriteAuthorizationModelAsync(storeId, modelJson, ct);

            if (row == null)
            {
                row = new FgaState { Id = 1 };
                db.FgaStates.Add(row);
            }
            else
            {
                _logger.LogWarning("Store {OldStoreId} bestaat niet meer in OpenFGA; nieuwe store wordt opgebouwd.",
                    row.StoreId);
            }

            row.StoreId = storeId;
            row.ModelId = modelId;
            row.ModelHash = modelHash;
            await db.SaveChangesAsync(ct);

            // De app-databank is de bron van waarheid voor afzender en ontvanger.
            // Alle tuples opnieuw opbouwen in de nieuwe store.
            var marked = await db.Messages.ExecuteUpdateAsync(s => s.SetProperty(m => m.FgaSynced, false), ct);

            _state.Set(storeId, modelId);
            _logger.LogInformation("Nieuwe OpenFGA store {StoreId} ({Name}) met model {ModelId}; {Count} berichten te synchroniseren",
                storeId, storeName, modelId, marked);
        }
        finally
        {
            _lock.Release();
        }
    }

    private (string Json, string Hash) LoadModel()
    {
        var path = _options.ModelPath;
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, path);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Autorisatiemodel niet gevonden: {path}");
        }

        // Parsen en opnieuw serialiseren: controleert dat het geldige JSON is en
        // maakt de hash onafhankelijk van witruimte.
        var node = JsonNode.Parse(File.ReadAllText(path))
                   ?? throw new InvalidOperationException("Autorisatiemodel is leeg.");
        if (node["schema_version"] == null || node["type_definitions"] is not JsonArray)
        {
            throw new InvalidOperationException("Autorisatiemodel mist schema_version of type_definitions.");
        }

        var json = node.ToJsonString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return (json, hash);
    }
}
