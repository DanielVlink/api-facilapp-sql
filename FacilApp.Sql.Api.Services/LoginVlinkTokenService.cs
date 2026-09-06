using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;

namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Solicita os Bearers SQL e NFe depois da autenticação válida de um usuário VLINK.
/// </summary>
/// <remarks>
/// A credencial SQL é escolhida no cadastro SQLite e identificada pelo INI. O segredo dela
/// nunca é recuperado: o token SQL é emitido internamente após validar a credencial ativa.
/// A integração NFe, quando configurada, permanece no INI e seu segredo nunca entra na resposta.
/// </remarks>
public sealed class LoginVlinkTokenService
{
    private const string DefaultNfeTokenUrl = "https://api.facilapp.com.br/realms/ACBrAPI/protocol/openid-connect/token";
    private readonly IniConfiguration configuration;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ConsoleStore consoleStore;
    private readonly AccessTokenService accessTokenService;

    /// <summary>Inicializa o emissor de tokens com a configuração privada e o cliente HTTP gerenciado.</summary>
    /// <param name="configuration">Configuração INI da instalação.</param>
    /// <param name="httpClientFactory">Fábrica de clientes HTTP com timeout institucional.</param>
    /// <param name="consoleStore">Cadastro SQLite de credenciais ativas.</param>
    /// <param name="accessTokenService">Emissor interno do Bearer SQL.</param>
    public LoginVlinkTokenService(
        IniConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ConsoleStore consoleStore,
        AccessTokenService accessTokenService)
    {
        this.configuration = configuration;
        this.httpClientFactory = httpClientFactory;
        this.consoleStore = consoleStore;
        this.accessTokenService = accessTokenService;
    }

    /// <summary>
    /// Obtém o token SQL da credencial selecionada e, opcionalmente, o token NFe do INI.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <returns>Tokens e respectivas validades devolvidos pelos dois serviços.</returns>
    /// <exception cref="InvalidOperationException">Gerada quando uma credencial obrigatória não está configurada.</exception>
    public async Task<LoginVlinkTokens> RequestTokensAsync(CancellationToken cancellationToken)
    {
        string nfeUrl = configuration.Get("LoginVlink", "NfeTokenUrl", DefaultNfeTokenUrl);
        string sqlClientId = configuration.Get("LoginVlink", "SqlClientId");
        ApiCredentialView? sqlCredential = await consoleStore.GetActiveCredentialAsync(sqlClientId, cancellationToken);
        if (sqlCredential is null)
        {
            throw new InvalidOperationException("Selecione uma credencial SQL ativa para o LoginVlink.");
        }

        AccessTokenResult sql = accessTokenService.Create(sqlCredential);
        string nfeClientId = configuration.Get("LoginVlink", "NfeClientId");
        string nfeClientSecret = configuration.Get("LoginVlink", "NfeClientSecret");
        TokenResponse? nfe = string.IsNullOrWhiteSpace(nfeClientId) || string.IsNullOrWhiteSpace(nfeClientSecret)
            ? null
            : await RequestTokenAsync(nfeUrl, nfeClientId, nfeClientSecret, "NFe", cancellationToken);

        return new LoginVlinkTokens(sql.AccessToken, sql.ExpiresIn, nfe?.AccessToken, nfe?.ExpiresIn);
    }

    private async Task<TokenResponse> RequestTokenAsync(
        string endpoint,
        string clientId,
        string clientSecret,
        string serviceName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException($"Credencial {serviceName} do LoginVlink não foi configurada no INI.");
        }

        HttpClient client = httpClientFactory.CreateClient("external");
        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        });
        using HttpResponseMessage response = await client.PostAsync(endpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"A API {serviceName} recusou a credencial configurada para LoginVlink.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument json = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("access_token", out JsonElement tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new InvalidOperationException($"A API {serviceName} não retornou um access token válido.");
        }

        int expiresIn = json.RootElement.TryGetProperty("expires_in", out JsonElement expiresElement)
            && expiresElement.TryGetInt32(out int parsedExpiresIn)
            ? parsedExpiresIn
            : 0;
        return new TokenResponse(tokenElement.GetString()!, expiresIn);
    }

    private sealed record TokenResponse(string AccessToken, int ExpiresIn);
}

/// <summary>Tokens temporários devolvidos após LoginVlink válido.</summary>
/// <param name="AccessTokenSql">Bearer emitido pela API SQL.</param>
/// <param name="ExpiresInSql">Validade, em segundos, do Bearer SQL.</param>
/// <param name="AccessTokenNfe">Bearer emitido pela API NFe.</param>
/// <param name="ExpiresInNfe">Validade, em segundos, do Bearer NFe.</param>
public sealed record LoginVlinkTokens(string AccessTokenSql, int ExpiresInSql, string? AccessTokenNfe, int? ExpiresInNfe);
