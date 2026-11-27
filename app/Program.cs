using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Unbreakable.Auth;
using Unbreakable.Authorization;
using Unbreakable.Data;
using Unbreakable.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<UnbreakableContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Unbreakable")));

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// Authenticatie: Keycloak access tokens (OIDC, JWT). Zie Auth/KeycloakAuth.cs.
builder.Services.AddKeycloakJwt(builder.Configuration);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<UserDirectory>();

// Autorisatie: OpenFGA als PDP. De API (PEP) vraagt per bericht of het mag.
var fgaOptions = builder.Configuration.GetSection("OpenFga").Get<OpenFgaOptions>() ?? new OpenFgaOptions();
builder.Services.AddSingleton(fgaOptions);
builder.Services.AddHttpClient(OpenFgaClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(fgaOptions.ApiUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(fgaOptions.TimeoutSeconds, 1, 30));
});
builder.Services.AddSingleton<FgaStoreState>();
builder.Services.AddSingleton<OpenFgaClient>();
builder.Services.AddSingleton<FgaBootstrapper>();
builder.Services.AddScoped<MessageAuthorization>();
// Maakt bij het opstarten de store en het model aan en schrijft ontbrekende tuples weg.
builder.Services.AddHostedService<TupleSyncService>();

// Kan de PDP niet beslissen, dan 503 en nooit toegang (fail closed).
builder.Services.AddExceptionHandler<FgaExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Databank aanmaken, schema bijwerken en vullen met voorbeeldberichten.
// De databank kan iets later opstarten dan de app, dus we proberen een paar keer.
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<UnbreakableContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    for (var attempt = 1; attempt <= 30; attempt++)
    {
        try
        {
            context.Database.EnsureCreated();
            SchemaUpgrade.Apply(context);
            SeedData.Initialize(context);
            SchemaUpgrade.LinkLegacyMessages(context);
            break;
        }
        catch (Exception ex) when (attempt < 30)
        {
            logger.LogWarning("Databank nog niet bereikbaar ({Error}), poging {Attempt}", ex.Message, attempt);
            context.ChangeTracker.Clear();
            Thread.Sleep(2000);
        }
    }
}

// Geen stacktraces naar de gebruiker; fouten van de PDP worden 503 (FgaExceptionHandler).
app.UseExceptionHandler();
app.UseCors();

// De SPA: index.html, JavaScript en CSS uit wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// Elke gebruiker met een geldig token komt in het gebruikersregister (sub en naam),
// zodat je hem een bericht kan sturen.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var directory = context.RequestServices.GetRequiredService<UserDirectory>();
        await directory.TouchAsync(context.User.ToCurrentUser(), context.RequestAborted);
    }

    await next(context);
});

app.MapGet("/health", () => "ok");

// De API. Wie je bent, komt uit het access token (Authorization: Bearer ...).
// Zonder geldig token: 401. De header X-User bestaat niet meer.
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/me", (ClaimsPrincipal principal) =>
{
    var user = principal.ToCurrentUser();
    return Results.Ok(new { username = user.Username, sub = user.Sub });
});

// Gebruikersregister voor het veld "Aan". Enkel namen.
api.MapGet("/users", async (UnbreakableContext db) =>
    Results.Ok(await db.Users.AsNoTracking().OrderBy(u => u.Username).Select(u => u.Username).ToListAsync()));

// Lijsten krijgen hun toegestane objecten van dezelfde PDP als individuele berichten.
api.MapGet("/messages", async (HttpRequest request, ClaimsPrincipal principal,
    MessageAuthorization authz, UnbreakableContext db, CancellationToken ct) =>
{
    var ids = await authz.ReadableMessageIdsAsync(principal.ToCurrentUser(), ct);
    var inbox = db.Messages.AsNoTracking().Where(m => ids.Contains(m.Id));

    // Functionele filter, uitsluitend binnen de door OpenFGA toegestane berichten.
    var from = request.Query["from"].ToString();
    if (!string.IsNullOrEmpty(from))
    {
        inbox = inbox.Where(m => m.Sender == from);
    }

    return Results.Ok(await inbox.OrderByDescending(m => m.SentAt).ToListAsync(ct));
});

api.MapGet("/messages/search", async (HttpRequest request, ClaimsPrincipal principal,
    MessageAuthorization authz, UnbreakableContext db, CancellationToken ct) =>
{
    var ids = await authz.ReadableMessageIdsAsync(principal.ToCurrentUser(), ct);
    var q = request.Query["q"].ToString();
    var pattern = "%" + q + "%";

    // EF parameteriseert het zoekpatroon; invoer wordt niet in SQL geplakt.
    var messages = await db.Messages.AsNoTracking()
        .Where(m => ids.Contains(m.Id))
        .Where(m => EF.Functions.ILike(m.Subject, pattern) || EF.Functions.ILike(m.Body, pattern))
        .OrderByDescending(m => m.SentAt)
        .ToListAsync(ct);

    return Results.Ok(messages);
});

api.MapGet("/messages/export", async (ClaimsPrincipal principal, MessageAuthorization authz,
    UnbreakableContext db, CancellationToken ct) =>
{
    var ids = await authz.ReadableMessageIdsAsync(principal.ToCurrentUser(), ct);
    var messages = await db.Messages.AsNoTracking()
        .Where(m => ids.Contains(m.Id))
        .OrderBy(m => m.SentAt)
        .ToListAsync(ct);

    return Results.Text(JsonConvert.SerializeObject(messages, Formatting.Indented), "application/json");
});

// Eén bericht: eerst de PDP, dan pas de databank. Bestaat een id niet, dan zijn
// er ook geen tuples en weigert de PDP: zo lekt de API niet welke id's bestaan.
api.MapGet("/messages/{id:int}", async (int id, ClaimsPrincipal principal, MessageAuthorization authz,
    UnbreakableContext db, CancellationToken ct) =>
{
    if (!await authz.CanReadAsync(principal.ToCurrentUser(), id, ct))
    {
        return Forbidden("Enkel de ontvanger mag dit bericht lezen.");
    }

    var message = await db.Messages.FindAsync(id);
    return message == null ? Results.NotFound() : Results.Ok(message);
});

api.MapPost("/messages", async (Message message, ClaimsPrincipal principal, UserDirectory directory,
    MessageAuthorization authz, UnbreakableContext db, ILogger<Program> logger, CancellationToken ct) =>
{
    // Zonder geldige HMAC (HMAC-SHA256, 64 hexadecimale tekens) kan de ontvanger de
    // integriteit niet controleren. Zo'n bericht weigeren we, ook via een directe API-call.
    if (message.Hmac == null || !System.Text.RegularExpressions.Regex.IsMatch(message.Hmac, "^[0-9a-f]{64}$"))
    {
        return Results.Problem(title: "Ongeldige HMAC",
            detail: "Een bericht moet een HMAC-SHA256 van 64 hexadecimale tekens bevatten.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    // De afzender komt altijd uit het token, nooit uit de request body.
    var user = principal.ToCurrentUser();
    message.Sender = user.Username;
    message.SenderId = user.Sub;

    // De ontvanger kies je op naam; we bewaren ook zijn sub.
    var recipient = await directory.FindByUsernameAsync(message.Recipient, CancellationToken.None);
    if (recipient == null)
    {
        return Results.Problem(title: "Onbekende ontvanger",
            detail: "Deze gebruiker heeft nog nooit aangemeld.", statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    message.Recipient = recipient.Username;
    message.RecipientId = recipient.Sub;

    message.Id = 0;
    message.SentAt = DateTime.UtcNow;
    message.FgaSynced = false;

    db.Messages.Add(message);
    await db.SaveChangesAsync();

    // Feiten naar de PDP: wie afzender en wie ontvanger is. Lukt het niet, dan
    // blijft het bericht bewaard en probeert TupleSyncService het opnieuw; tot
    // dan kan niemand het lezen of verwijderen (fail closed).
    try
    {
        await authz.EnsureMessageTuplesAsync(message, ct);
        message.FgaSynced = true;
        await db.SaveChangesAsync();
    }
    catch (Exception ex) when (ex is FgaUnavailableException or FgaRequestException)
    {
        logger.LogWarning("Tuples voor bericht {Id} nog niet geschreven: {Error}. Wordt opnieuw geprobeerd.",
            message.Id, ex.Message);
    }

    return Results.Created($"/api/messages/{message.Id}", message);
});

api.MapDelete("/messages/{id:int}", async (int id, ClaimsPrincipal principal, MessageAuthorization authz,
    UnbreakableContext db, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!await authz.CanDeleteAsync(principal.ToCurrentUser(), id, ct))
    {
        return Forbidden("Enkel de afzender mag dit bericht verwijderen.");
    }

    var message = await db.Messages.FindAsync(id);
    if (message == null)
    {
        return Results.NotFound();
    }

    db.Messages.Remove(message);
    await db.SaveChangesAsync();

    // Tuples opruimen. Mislukt dit, dan blijven tuples naar een id dat niet meer
    // bestaat; id's worden niet hergebruikt binnen dezelfde store.
    try
    {
        await authz.RemoveMessageTuplesAsync(id, ct);
    }
    catch (Exception ex) when (ex is FgaUnavailableException or FgaRequestException)
    {
        logger.LogWarning("Tuples van verwijderd bericht {Id} niet opgeruimd: {Error}", id, ex.Message);
    }

    return Results.NoContent();
});

app.Run();

static IResult Forbidden(string detail) =>
    Results.Problem(title: "Geen toegang", detail: detail, statusCode: StatusCodes.Status403Forbidden);
