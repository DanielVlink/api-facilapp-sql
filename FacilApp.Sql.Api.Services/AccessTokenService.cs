using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FacilApp.Sql.Api.Configuration;

namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Emite e valida tokens Bearer temporários para o fluxo OAuth 2.0
/// <c>client_credentials</c> da API FacilApp SQL.
/// </summary>
/// <remarks>
/// A chave de assinatura pertence à instalação e fica no diretório protegido
/// <c>Segredos</c>. Tokens de acesso e Client Secrets nunca são gravados no INI.
/// </remarks>
public sealed class AccessTokenService
{
    /// <summary>
    /// Tempo de validade de um token de acesso, em segundos: 30 dias.
    /// </summary>
    public const int LifetimeSeconds = 30 * 24 * 60 * 60;

    private const string Issuer = "FacilApp.Sql.Api";
    private readonly byte[] signingKey;

    /// <summary>
    /// Inicializa o serviço e carrega a chave privada de assinatura da instalação.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Gerada quando a chave existente é menor que o mínimo de segurança exigido.
    /// </exception>
    /// <param name="configuration">Configuração que identifica o diretório gravável da instalação.</param>
    public AccessTokenService(IniConfiguration configuration)
    {
        string secretsDirectory = Path.Combine(configuration.DataDirectory, "Segredos");
        Directory.CreateDirectory(secretsDirectory);

        string keyPath = Path.Combine(secretsDirectory, "facilapp_sql.jwtkey");
        if (!File.Exists(keyPath))
        {
            // CreateNew impede que duas inicializações sobrescrevam a mesma chave.
            using FileStream keyFile = new(
                keyPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);

            keyFile.Write(RandomNumberGenerator.GetBytes(32));
        }

        signingKey = File.ReadAllBytes(keyPath);
        if (signingKey.Length < 32)
        {
            throw new InvalidDataException("A chave JWT da instalação é inválida.");
        }
    }

    /// <summary>
    /// Cria um token limitado às permissões da credencial e aos escopos solicitados.
    /// </summary>
    /// <param name="credential">Credencial ativa previamente validada.</param>
    /// <param name="requestedScope">
    /// Escopos separados por espaço. Quando omitido, preserva a compatibilidade e
    /// concede todas as permissões cadastradas para a credencial.
    /// </param>
    /// <returns>Token Bearer, validade e escopos efetivamente concedidos.</returns>
    /// <exception cref="ArgumentException">
    /// Gerada quando um escopo solicitado não pertence à credencial.
    /// </exception>
    public AccessTokenResult Create(ApiCredentialView credential, string requestedScope = null)
    {
        string[] allowedScopes = NormalizeScopes(credential.Permissoes);
        string[] grantedScopes = string.IsNullOrWhiteSpace(requestedScope)
            ? allowedScopes
            : NormalizeScopes(requestedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        string[] invalidScopes = grantedScopes
            .Except(allowedScopes, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (invalidScopes.Length > 0)
        {
            throw new ArgumentException(
                $"Escopo não autorizado: {string.Join(' ', invalidScopes)}.",
                nameof(requestedScope));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string scope = string.Join(' ', grantedScopes);
        var header = new
        {
            alg = "HS256",
            typ = "JWT"
        };
        var payload = new
        {
            iss = Issuer,
            sub = credential.ClientId,
            client_id = credential.ClientId,
            scope,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddSeconds(LifetimeSeconds).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N")
        };

        string unsignedToken = $"{EncodeJson(header)}.{EncodeJson(payload)}";
        using HMACSHA256 hmac = new(signingKey);
        string signature = Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(unsignedToken)));

        return new AccessTokenResult(
            $"{unsignedToken}.{signature}",
            LifetimeSeconds,
            scope);
    }

    /// <summary>
    /// Valida assinatura, emissor, expiração, identidade e escopos de um Bearer.
    /// </summary>
    /// <param name="token">Token recebido no cabeçalho Authorization.</param>
    /// <returns>Resultado seguro da validação, sem expor o conteúdo bruto do token.</returns>
    public AccessTokenValidation Validate(string token)
    {
        try
        {
            string[] segments = token.Split('.');
            if (segments.Length != 3)
            {
                return AccessTokenValidation.Invalid;
            }

            using JsonDocument headerDocument = JsonDocument.Parse(DecodeBase64Url(segments[0]));
            JsonElement header = headerDocument.RootElement;
            if (!header.TryGetProperty("alg", out JsonElement algorithm)
                || algorithm.GetString() != "HS256")
            {
                return AccessTokenValidation.Invalid;
            }

            string signedContent = $"{segments[0]}.{segments[1]}";
            using HMACSHA256 hmac = new(signingKey);
            byte[] expectedSignature = hmac.ComputeHash(Encoding.ASCII.GetBytes(signedContent));
            byte[] receivedSignature = DecodeBase64Url(segments[2]);

            if (expectedSignature.Length != receivedSignature.Length
                || !CryptographicOperations.FixedTimeEquals(expectedSignature, receivedSignature))
            {
                return AccessTokenValidation.Invalid;
            }

            using JsonDocument payloadDocument = JsonDocument.Parse(DecodeBase64Url(segments[1]));
            JsonElement payload = payloadDocument.RootElement;
            if (!payload.TryGetProperty("iss", out JsonElement issuer)
                || issuer.GetString() != Issuer
                || !payload.TryGetProperty("exp", out JsonElement expiration)
                || expiration.GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                || !payload.TryGetProperty("client_id", out JsonElement clientIdentifier))
            {
                return AccessTokenValidation.Invalid;
            }

            string clientId = clientIdentifier.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return AccessTokenValidation.Invalid;
            }

            string scope = payload.TryGetProperty("scope", out JsonElement scopeElement)
                ? scopeElement.GetString() ?? string.Empty
                : string.Empty;

            return new AccessTokenValidation(
                IsValid: true,
                clientId,
                NormalizeScopes(scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }
        catch (Exception exception) when (
            exception is FormatException
            or JsonException
            or KeyNotFoundException
            or InvalidOperationException)
        {
            return AccessTokenValidation.Invalid;
        }
    }

    private static string[] NormalizeScopes(IEnumerable<string> scopes)
    {
        return scopes
            .Select(scope => scope.Trim().ToLowerInvariant())
            .Where(scope => scope.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string EncodeJson<T>(T value)
    {
        return Base64Url(JsonSerializer.SerializeToUtf8Bytes(value));
    }

    private static string Base64Url(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        string normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + (4 - normalized.Length % 4) % 4,
            '=');

        return Convert.FromBase64String(normalized);
    }
}
