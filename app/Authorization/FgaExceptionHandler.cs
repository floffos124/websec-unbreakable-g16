using Microsoft.AspNetCore.Diagnostics;

namespace Unbreakable.Authorization;

// Kan de PDP geen beslissing geven, dan is het antwoord 503 en nooit toegang.
public sealed class FgaExceptionHandler : IExceptionHandler
{
    private readonly ILogger<FgaExceptionHandler> _logger;

    public FgaExceptionHandler(ILogger<FgaExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not (FgaUnavailableException or FgaRequestException))
        {
            return false;
        }

        _logger.LogError("AUTHZ ERROR {Path}: {Error} (fail closed, 503)", httpContext.Request.Path, exception.Message);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "10";
        await httpContext.Response.WriteAsJsonAsync(new
        {
            title = "Autorisatie tijdelijk niet beschikbaar",
            status = 503,
            detail = "De PDP (OpenFGA) gaf geen beslissing. Zonder beslissing geen toegang."
        }, cancellationToken);
        return true;
    }
}
