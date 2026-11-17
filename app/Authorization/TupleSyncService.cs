using Microsoft.EntityFrameworkCore;
using Unbreakable.Data;

namespace Unbreakable.Authorization;

// Een bericht staat in PostgreSQL, de tuples staan in OpenFGA. Die twee kunnen
// niet in één transactie geschreven worden. Daarom:
//   1. de API bewaart het bericht met FgaSynced = false,
//   2. schrijft meteen de tuples en zet FgaSynced = true,
//   3. lukt stap 2 niet (OpenFGA plat, time-out), dan probeert deze service het
//      opnieuw tot het lukt.
// Tot dan kan niemand het bericht lezen of verwijderen (fail closed).
//
// Deze service doet ook de bootstrap van OpenFGA bij het opstarten.
public sealed class TupleSyncService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private const int BatchSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FgaBootstrapper _bootstrapper;
    private readonly FgaStoreState _state;
    private readonly OpenFgaClient _fga;
    private readonly ILogger<TupleSyncService> _logger;

    public TupleSyncService(IServiceScopeFactory scopeFactory, FgaBootstrapper bootstrapper, FgaStoreState state,
        OpenFgaClient fga, ILogger<TupleSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _bootstrapper = bootstrapper;
        _state = state;
        _fga = fga;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_state.IsReady && !await _fga.StoreExistsAsync(_state.Get().StoreId, stoppingToken))
                {
                    // OpenFGA werd leeggemaakt: opnieuw bootstrappen en alle tuples herbouwen.
                    _logger.LogWarning("OpenFGA store bestaat niet meer; bootstrap opnieuw.");
                    _state.Reset();
                }

                if (!_state.IsReady)
                {
                    await _bootstrapper.EnsureReadyAsync(stoppingToken);
                }

                await SyncPendingAsync(stoppingToken);
                delay = Interval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("OpenFGA bootstrap/sync mislukt: {Error}. Nieuwe poging over {Delay} s.",
                    ex.Message, delay.TotalSeconds);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxBackoff.TotalSeconds));
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncPendingAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UnbreakableContext>();
        var authz = scope.ServiceProvider.GetRequiredService<MessageAuthorization>();

        var pending = await db.Messages
            .Where(m => !m.FgaSynced && m.SenderId != null && m.RecipientId != null)
            .OrderBy(m => m.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var message in pending)
        {
            // Fout bij één bericht: loggen en verder met de rest. Volgende ronde opnieuw.
            try
            {
                await authz.EnsureMessageTuplesAsync(message, ct);
                message.FgaSynced = true;
                await db.SaveChangesAsync(ct);
            }
            catch (FgaUnavailableException)
            {
                throw; // PDP plat: stoppen, backoff in ExecuteAsync.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Tuples voor bericht {Id} mislukt: {Error}", message.Id, ex.Message);
            }
        }

        if (pending.Count > 0)
        {
            _logger.LogInformation("{Count} bericht(en) gesynchroniseerd naar OpenFGA", pending.Count);
        }
    }
}
