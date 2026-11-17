using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Unbreakable.Auth;

public sealed class KeycloakOptions
{
    // Moet exact overeenkomen met de 'iss' claim: de publieke URL van de realm.
    public string Issuer { get; set; } = "";

    // De API aanvaardt enkel tokens die voor haar bedoeld zijn ('aud').
    public string Audience { get; set; } = "unbreakable-api";

    // De SPA is de enige client die tokens voor deze API mag aanvragen ('azp').
    public string ClientId { get; set; } = "unbreakable-spa";

    // Optioneel: interne URL van de JWKS (publieke sleutels). Zo haalt de API de
    // sleutels rechtstreeks bij de Keycloak container, zonder via internet en de
    // WAF terug binnen te komen. Leeg = OIDC discovery via de Issuer.
    public string JwksUrl { get; set; } = "";

    // Toegelaten klokverschil tussen Keycloak en de API.
    public int ClockSkewSeconds { get; set; } = 30;
}

// De gebruiker zoals de API die kent: enkel uit een gevalideerd token.
public sealed record CurrentUser(string Sub, string Username);

public static class KeycloakAuth
{
    // sub is een vaste id (UUID bij Keycloak); geen ':' '#', quotes of spaties.
    private static readonly Regex SubPattern = new("^[A-Za-z0-9._-]{1,128}$", RegexOptions.Compiled);

    // Zelfde grenzen als Message.Sender (2 tot 40 tekens).
    private static readonly Regex UsernamePattern = new("^[a-z0-9._@-]{2,40}$", RegexOptions.Compiled);

    public static IServiceCollection AddKeycloakJwt(this IServiceCollection services, IConfiguration configuration)
    {
        var kc = configuration.GetSection("Keycloak").Get<KeycloakOptions>() ?? new KeycloakOptions();
        if (string.IsNullOrWhiteSpace(kc.Issuer))
        {
            throw new InvalidOperationException("Configuratie Keycloak:Issuer ontbreekt.");
        }

        services.AddSingleton(kc);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Claimnamen laten zoals in het token (sub, preferred_username, azp, typ).
                options.MapInboundClaims = false;
                // Geen foutdetails in de WWW-Authenticate header naar buiten.
                options.IncludeErrorDetails = false;
                options.SaveToken = false;

                if (string.IsNullOrWhiteSpace(kc.JwksUrl))
                {
                    options.Authority = kc.Issuer;
                    options.RequireHttpsMetadata = kc.Issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // Enkel de publieke sleutels ophalen van de interne URL.
                    // RequireHttps volgt de URL: http enkel binnen het Docker netwerk.
                    var retriever = new HttpDocumentRetriever
                    {
                        RequireHttps = kc.JwksUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    };
                    options.ConfigurationManager =
                        new Microsoft.IdentityModel.Protocols.ConfigurationManager<OpenIdConnectConfiguration>(
                            kc.JwksUrl, new JwksRetriever(kc.Issuer), retriever)
                        {
                            // Sleutels periodiek verversen (sleutelrotatie in Keycloak).
                            AutomaticRefreshInterval = TimeSpan.FromHours(1),
                            // Bij een onbekende 'kid' of een fout: na 30 s opnieuw proberen.
                            RefreshInterval = TimeSpan.FromSeconds(30)
                        };
                }

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // 1. Handtekening: verplicht, enkel RS256, sleutel uit de JWKS van Keycloak.
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },

                    // 2. Uitgever: exact onze realm.
                    ValidateIssuer = true,
                    ValidIssuer = kc.Issuer,

                    // 3. Doelgroep: dit token is voor deze API bedoeld.
                    ValidateAudience = true,
                    ValidAudience = kc.Audience,

                    // 4. Geldigheid: exp verplicht, nbf/exp gecontroleerd met kleine marge.
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(Math.Clamp(kc.ClockSkewSeconds, 0, 120)),

                    NameClaimType = "preferred_username"
                };

                options.Events = new JwtBearerEvents
                {
                    // Extra controles die de standaardvalidatie niet doet.
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal;
                        var typ = principal?.FindFirst("typ")?.Value;
                        var azp = principal?.FindFirst("azp")?.Value;
                        var sub = principal?.FindFirst("sub")?.Value;
                        var username = principal?.FindFirst("preferred_username")?.Value;

                        // 5. Enkel access tokens (Keycloak zet typ=Bearer), geen ID- of refresh tokens.
                        if (!string.Equals(typ, "Bearer", StringComparison.Ordinal))
                        {
                            context.Fail("Geen access token.");
                            return Task.CompletedTask;
                        }

                        // 6. Uitgegeven aan onze SPA (authorized party).
                        if (!string.Equals(azp, kc.ClientId, StringComparison.Ordinal))
                        {
                            context.Fail("Token is niet uitgegeven aan de verwachte client.");
                            return Task.CompletedTask;
                        }

                        // 7. Identiteit aanwezig en bruikbaar.
                        if (string.IsNullOrEmpty(sub) || !SubPattern.IsMatch(sub))
                        {
                            context.Fail("Ongeldige of ontbrekende sub claim.");
                            return Task.CompletedTask;
                        }

                        if (string.IsNullOrEmpty(username) || !UsernamePattern.IsMatch(username))
                        {
                            context.Fail("Ongeldige of ontbrekende preferred_username claim.");
                        }

                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Unbreakable.Auth");
                        logger.LogInformation("Token geweigerd: {Reason}", context.Exception.GetType().Name);
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();
        return services;
    }

    public static CurrentUser ToCurrentUser(this ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst("sub")?.Value;
        var username = principal.FindFirst("preferred_username")?.Value;
        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(username))
        {
            // Kan niet gebeuren na OnTokenValidated; toch nooit verder zonder identiteit.
            throw new InvalidOperationException("Geen geauthenticeerde gebruiker.");
        }

        return new CurrentUser(sub, username.ToLowerInvariant());
    }
}

// Haalt enkel de JWKS op (geen volledige discovery) en vult daarmee een
// OpenIdConnectConfiguration, zodat JwtBearer de sleutels kan gebruiken en
// zelf ververst bij sleutelrotatie.
public sealed class JwksRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    private readonly string _issuer;

    public JwksRetriever(string issuer)
    {
        _issuer = issuer;
    }

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
        var jwks = new JsonWebKeySet(json);

        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = _issuer,
            JsonWebKeySet = jwks
        };

        // GetSigningKeys slaat sleutels met use=enc over (Keycloak publiceert ook een RSA-OAEP sleutel).
        foreach (var key in jwks.GetSigningKeys())
        {
            configuration.SigningKeys.Add(key);
        }

        if (configuration.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException("JWKS bevat geen bruikbare handtekeningsleutels.");
        }

        return configuration;
    }
}
