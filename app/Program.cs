using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Unbreakable.Auth;
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

app.UseDeveloperExceptionPage();
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

api.MapGet("/messages", async (HttpRequest request, ClaimsPrincipal principal, UnbreakableContext db) =>
{
    // We vergelijken op sub (vaste id), niet op de naam.
    var user = principal.ToCurrentUser().Sub;

    var inbox = db.Messages.Where(m => m.RecipientId == user);

    var from = request.Query["from"].ToString();
    if (!string.IsNullOrEmpty(from))
    {
        inbox = inbox.Where(m => m.Sender == from);
    }

    return Results.Ok(await inbox.OrderByDescending(m => m.SentAt).ToListAsync());
});

api.MapGet("/messages/search", async (HttpRequest request, ClaimsPrincipal principal, UnbreakableContext db) =>
{
    var user = principal.ToCurrentUser().Sub;

    var q = request.Query["q"].ToString();
    var sql = "SELECT * FROM \"Messages\" WHERE \"RecipientId\" = '" + user + "' " +
              "AND (\"Subject\" ILIKE '%" + q + "%' OR \"Body\" ILIKE '%" + q + "%')";

    var messages = await db.Messages.FromSqlRaw(sql).OrderByDescending(m => m.SentAt).ToListAsync();
    return Results.Ok(messages);
});

api.MapGet("/messages/export", async (ClaimsPrincipal principal, UnbreakableContext db) =>
{
    var user = principal.ToCurrentUser().Sub;

    var messages = await db.Messages
        .Where(m => m.RecipientId == user)
        .OrderBy(m => m.SentAt)
        .ToListAsync();

    return Results.Text(JsonConvert.SerializeObject(messages, Formatting.Indented), "application/json");
});

api.MapGet("/messages/{id:int}", async (int id, UnbreakableContext db) =>
{
    var message = await db.Messages.FindAsync(id);
    return message == null ? Results.NotFound() : Results.Ok(message);
});

api.MapPost("/messages", async (Message message, ClaimsPrincipal principal, UserDirectory directory, UnbreakableContext db) =>
{
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

    db.Messages.Add(message);
    await db.SaveChangesAsync();

    return Results.Created($"/api/messages/{message.Id}", message);
});

api.MapDelete("/messages/{id:int}", async (int id, UnbreakableContext db) =>
{
    var message = await db.Messages.FindAsync(id);
    if (message == null)
    {
        return Results.NotFound();
    }

    db.Messages.Remove(message);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();
