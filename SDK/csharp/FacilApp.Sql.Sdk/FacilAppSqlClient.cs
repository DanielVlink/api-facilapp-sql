using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FacilApp.Sql.Sdk;

public sealed class FacilAppSqlClient : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsClient;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    public FacilAppSqlClient(string baseUrl = "https://sql.facilapp.com.br", HttpClient? httpClient = null)
    {
        ownsClient = httpClient is null;
        http = httpClient ?? new HttpClient();
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public string? AccessToken { get; private set; }

    public Task<ApiStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ApiStatus>(HttpMethod.Get, "status", null, false, cancellationToken);

    public Task<JsonDocument> GetOpenApiAsync(CancellationToken cancellationToken = default) =>
        SendAsync<JsonDocument>(HttpMethod.Get, "swagger/v1/swagger.json", null, false, cancellationToken);

    public async Task<LoginResult> LoginWithClientSecretAsync(string clientId, string clientSecret, string? scope = null, CancellationToken cancellationToken = default)
    {
        LoginResult result = await SendAsync<LoginResult>(HttpMethod.Post, "oauth/login-simples",
            new { client_id = clientId, client_secret = clientSecret, scope }, false, cancellationToken);
        AccessToken = result.AccessToken;
        return result;
    }

    public async Task<LoginResult> RequestOAuthTokenAsync(string clientId, string clientSecret, string? scope = null, CancellationToken cancellationToken = default)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId,
            ["client_secret"] = clientSecret, ["scope"] = scope ?? string.Empty
        });
        using HttpRequestMessage request = new(HttpMethod.Post, "oauth/token") { Content = form };
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        LoginResult result = await ReadAsync<LoginResult>(response, cancellationToken);
        AccessToken = result.AccessToken;
        return result;
    }

    public Task<JsonDocument> ExecutarAsync(object pedido, CancellationToken cancellationToken = default) =>
        SendAsync<JsonDocument>(HttpMethod.Post, "executar", pedido, true, cancellationToken);

    public Task<T> GetAsync<T>(string path, bool authenticated = true, CancellationToken cancellationToken = default) => SendAsync<T>(HttpMethod.Get, path, null, authenticated, cancellationToken);
    public Task<T> PostAsync<T>(string path, object? body, bool authenticated = true, CancellationToken cancellationToken = default) => SendAsync<T>(HttpMethod.Post, path, body, authenticated, cancellationToken);
    public Task<T> PutAsync<T>(string path, object? body, bool authenticated = true, CancellationToken cancellationToken = default) => SendAsync<T>(HttpMethod.Put, path, body, authenticated, cancellationToken);
    public Task<T> DeleteAsync<T>(string path, bool authenticated = true, CancellationToken cancellationToken = default) => SendAsync<T>(HttpMethod.Delete, path, null, authenticated, cancellationToken);
    public void SetAccessToken(string accessToken) => AccessToken = accessToken;

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authenticated, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, path.TrimStart('/'));
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, json), Encoding.UTF8, "application/json");
        if (authenticated)
        {
            if (string.IsNullOrWhiteSpace(AccessToken)) throw new InvalidOperationException("Faça o login ou informe um Access Token antes da chamada autenticada.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        }
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new FacilAppApiException((int)response.StatusCode, content);
        return JsonSerializer.Deserialize<T>(content, json) ?? throw new FacilAppApiException((int)response.StatusCode, "A API retornou uma resposta vazia.");
    }

    public void Dispose() { if (ownsClient) http.Dispose(); }
}

public sealed record ApiStatus([property: JsonPropertyName("ok")] bool Ok, [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("instancia")] string? Instancia, [property: JsonPropertyName("porta")] int Porta,
    [property: JsonPropertyName("versao")] string? Versao);

public sealed record LoginResult([property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType, [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope, [property: JsonPropertyName("credencial")] string? Credencial,
    [property: JsonPropertyName("cnpj")] string? Cnpj, [property: JsonPropertyName("idFilial")] string? IdFilial,
    [property: JsonPropertyName("idClient")] string? IdClient);

public sealed class FacilAppApiException(int statusCode, string responseBody) : Exception($"FacilApp SQL API retornou HTTP {statusCode}: {responseBody}")
{
    public int StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}
