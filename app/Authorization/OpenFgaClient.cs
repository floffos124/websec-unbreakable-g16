using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Unbreakable.Authorization;

public sealed class OpenFgaOptions
{
    // Interne URL van OpenFGA (HTTP API). Niet bereikbaar van buitenaf.
    public string ApiUrl { get; set; } = "http://openfga:8080";

    // Preshared key van OpenFGA. Komt uit een omgevingsvariabele, nooit uit de repo.
    public string ApiToken { get; set; } = "";

    public string StoreName { get; set; } = "unbreakable";

    // Het autorisatiemodel (JSON). Staat buiten de API-code in openfga/model.json
    // en wordt read-only in de container gemount.
    public string ModelPath { get; set; } = "/config/openfga/model.json";

    public int TimeoutSeconds { get; set; } = 5;

    // ListObjects geeft maximaal zoveel resultaten (OPENFGA_LIST_OBJECTS_MAX_RESULTS).
    public int ListObjectsMaxResults { get; set; } = 5000;
}

public sealed record FgaTuple(string User, string Relation, string Object);

// De PDP is niet bereikbaar of antwoordt niet correct. De API antwoordt dan 503:
// zonder beslissing van de PDP geen toegang (fail closed).
public sealed class FgaUnavailableException : Exception
{
    public FgaUnavailableException(string message, Exception inner = null) : base(message, inner)
    {
    }
}

// Houdt bij welke store en welk model de app gebruikt. Pas na een geslaagde
// bootstrap is IsReady true; daarvoor weigert de client elke vraag.
public sealed class FgaStoreState
{
    private volatile Snapshot _current;

    public bool IsReady => _current != null;

    public void Set(string storeId, string modelId) => _current = new Snapshot(storeId, modelId);

    public void Reset() => _current = null;

    public (string StoreId, string ModelId) Get()
    {
        var snapshot = _current ?? throw new FgaUnavailableException("OpenFGA is nog niet klaar (bootstrap loopt).");
        return (snapshot.StoreId, snapshot.ModelId);
    }

    private sealed record Snapshot(string StoreId, string ModelId);
}

// Dunne client op de HTTP API van OpenFGA. Bewust zonder SDK: we gebruiken
// maar zes calls en willen zelf bepalen wat er bij fouten gebeurt.
public sealed class OpenFgaClient
{
    public const string HttpClientName = "openfga";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FgaStoreState _state;
    private readonly OpenFgaOptions _options;
    private readonly ILogger<OpenFgaClient> _logger;

    public OpenFgaClient(IHttpClientFactory httpClientFactory, FgaStoreState state, OpenFgaOptions options,
        ILogger<OpenFgaClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _state = state;
        _options = options;
        _logger = logger;
    }

    // ------------------------------------------------------------ beslissingen

    public async Task<bool> CheckAsync(string user, string relation, string obj, CancellationToken ct)
    {
        var (storeId, modelId) = _state.Get();
        var body = new JsonObject
        {
            ["authorization_model_id"] = modelId,
            ["tuple_key"] = TupleJson(user, relation, obj)
        };

        using var doc = await SendWithRetryAsync(HttpMethod.Post, $"stores/{Esc(storeId)}/check", body, ct);
        // Ontbreekt 'allowed' of is het geen true: weigeren.
        return doc.RootElement.TryGetProperty("allowed", out var allowed)
               && allowed.ValueKind == JsonValueKind.True;
    }

    public async Task<IReadOnlyList<string>> ListObjectsAsync(string user, string relation, string type,
        CancellationToken ct)
    {
        var (storeId, modelId) = _state.Get();
        var body = new JsonObject
        {
            ["authorization_model_id"] = modelId,
            ["type"] = type,
            ["relation"] = relation,
            ["user"] = user
        };

        using var doc = await SendWithRetryAsync(HttpMethod.Post, $"stores/{Esc(storeId)}/list-objects", body, ct);
        var result = new List<string>();
        if (doc.RootElement.TryGetProperty("objects", out var objects) && objects.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in objects.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    result.Add(item.GetString());
                }
            }
        }

        if (result.Count >= _options.ListObjectsMaxResults)
        {
            _logger.LogWarning("ListObjects gaf {Count} resultaten: mogelijk afgekapt op de limiet.", result.Count);
        }

        return result;
    }

    // ------------------------------------------------------------ tuples

    public async Task<IReadOnlyList<FgaTuple>> ReadAsync(string obj, CancellationToken ct)
    {
        var (storeId, _) = _state.Get();
        var tuples = new List<FgaTuple>();
        string continuation = null;

        // Pagineren; begrensd zodat een fout in de server ons niet eindeloos laat lussen.
        for (var page = 0; page < 100; page++)
        {
            var body = new JsonObject
            {
                ["tuple_key"] = new JsonObject { ["object"] = obj },
                ["page_size"] = 100
            };
            if (!string.IsNullOrEmpty(continuation))
            {
                body["continuation_token"] = continuation;
            }

            using var doc = await SendWithRetryAsync(HttpMethod.Post, $"stores/{Esc(storeId)}/read", body, ct);
            if (doc.RootElement.TryGetProperty("tuples", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("key", out var key))
                    {
                        continue;
                    }

                    var user = key.TryGetProperty("user", out var u) ? u.GetString() : null;
                    var relation = key.TryGetProperty("relation", out var r) ? r.GetString() : null;
                    var o = key.TryGetProperty("object", out var ob) ? ob.GetString() : null;
                    if (user != null && relation != null && o != null)
                    {
                        tuples.Add(new FgaTuple(user, relation, o));
                    }
                }
            }

            continuation = doc.RootElement.TryGetProperty("continuation_token", out var tokenElement)
                ? tokenElement.GetString()
                : null;
            if (string.IsNullOrEmpty(continuation))
            {
                return tuples;
            }
        }

        throw new FgaUnavailableException("Te veel pagina's bij het lezen van tuples.");
    }

    public async Task WriteAsync(IReadOnlyCollection<FgaTuple> writes, IReadOnlyCollection<FgaTuple> deletes,
        CancellationToken ct)
    {
        if (writes.Count == 0 && deletes.Count == 0)
        {
            return;
        }

        var (storeId, modelId) = _state.Get();
        var body = new JsonObject { ["authorization_model_id"] = modelId };

        // OpenFGA weigert een lege lijst, dus enkel meesturen wat er is.
        if (writes.Count > 0)
        {
            var keys = new JsonArray();
            foreach (var t in writes)
            {
                keys.Add((JsonNode)TupleJson(t.User, t.Relation, t.Object));
            }

            body["writes"] = new JsonObject { ["tuple_keys"] = keys };
        }

        if (deletes.Count > 0)
        {
            var keys = new JsonArray();
            foreach (var t in deletes)
            {
                keys.Add((JsonNode)TupleJson(t.User, t.Relation, t.Object));
            }

            body["deletes"] = new JsonObject { ["tuple_keys"] = keys };
        }

        // Schrijven niet automatisch herhalen: de aanroeper leest eerst wat er al
        // staat en schrijft enkel het verschil (idempotent op een hoger niveau).
        using var _ = await SendAsync(HttpMethod.Post, $"stores/{Esc(storeId)}/write", body.ToJsonString(), ct);
    }

    // ------------------------------------------------------------ store en model (bootstrap)

    public async Task<bool> StoreExistsAsync(string storeId, CancellationToken ct)
    {
        try
        {
            using var _ = await SendAsync(HttpMethod.Get, $"stores/{Esc(storeId)}", null, ct);
            return true;
        }
        catch (FgaRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound
                                             || (ex.StatusCode == HttpStatusCode.BadRequest
                                                 && ex.Body.Contains("not_found", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
    }

    public async Task<string> CreateStoreAsync(string name, CancellationToken ct)
    {
        var body = new JsonObject { ["name"] = name };
        using var doc = await SendAsync(HttpMethod.Post, "stores", body.ToJsonString(), ct);
        return RequireString(doc.RootElement, "id");
    }

    public async Task<string> WriteAuthorizationModelAsync(string storeId, string modelJson, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"stores/{Esc(storeId)}/authorization-models", modelJson, ct);
        return RequireString(doc.RootElement, "authorization_model_id");
    }

    // ------------------------------------------------------------ HTTP

    private async Task<JsonDocument> SendWithRetryAsync(HttpMethod method, string path, JsonObject body,
        CancellationToken ct)
    {
        var json = body.ToJsonString();
        try
        {
            return await SendAsync(method, path, json, ct);
        }
        catch (FgaUnavailableException ex) when (!ct.IsCancellationRequested)
        {
            // Eén nieuwe poging voor leesoperaties (check, list, read): die zijn veilig te herhalen.
            _logger.LogWarning("OpenFGA {Path} faalde ({Error}), nieuwe poging.", path, ex.Message);
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            return await SendAsync(method, path, json, ct);
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string jsonBody, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(_options.ApiToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }

        if (jsonBody != null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Netwerkfout of time-out.
            throw new FgaUnavailableException($"OpenFGA niet bereikbaar: {ex.GetType().Name}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new FgaUnavailableException($"OpenFGA antwoordde {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                // 4xx: verkeerde vraag of verkeerde sleutel. Nooit als 'toegestaan' behandelen.
                throw new FgaRequestException(response.StatusCode, text);
            }

            try
            {
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }
            catch (JsonException ex)
            {
                throw new FgaUnavailableException("OpenFGA gaf geen geldige JSON.", ex);
            }
        }
    }

    private static JsonObject TupleJson(string user, string relation, string obj) => new()
    {
        ["user"] = user,
        ["relation"] = relation,
        ["object"] = obj
    };

    private static string RequireString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                                                             && !string.IsNullOrEmpty(value.GetString()))
        {
            return value.GetString();
        }

        throw new FgaUnavailableException($"OpenFGA antwoord mist '{property}'.");
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);
}

// OpenFGA weigerde de vraag (4xx). Voor de gebruiker: geen toegang of 503,
// nooit toegang.
public sealed class FgaRequestException : Exception
{
    public FgaRequestException(HttpStatusCode statusCode, string body)
        : base($"OpenFGA weigerde de vraag ({(int)statusCode}).")
    {
        StatusCode = statusCode;
        Body = body ?? "";
    }

    public HttpStatusCode StatusCode { get; }

    public string Body { get; }
}
