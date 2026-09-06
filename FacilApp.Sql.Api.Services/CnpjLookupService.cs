using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FacilApp.Sql.Api.Services;

public sealed class CnpjLookupService
{
    private sealed record CachedCompany(PublicCompanyView Company, DateTimeOffset ExpiresAt);

    private sealed record CachedRawCompany(PublicCnpjRawResponse Response, DateTimeOffset ExpiresAt);

    private readonly IHttpClientFactory httpClientFactory;

    private readonly ConcurrentDictionary<string, CachedCompany> cache = new ConcurrentDictionary<string, CachedCompany>();

    private readonly ConcurrentDictionary<string, CachedRawCompany> rawCache = new ConcurrentDictionary<string, CachedRawCompany>();

    public CnpjLookupService(IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
    }

    public async Task<PublicCompanyView> LookupAsync(string cnpj, CancellationToken cancellationToken)
    {
        string normalizedCnpj = OnlyDigits(cnpj);
        if (!IsValidCnpj(normalizedCnpj))
        {
            throw new InvalidDataException("CNPJ inválido.");
        }
        if (cache.TryGetValue(normalizedCnpj, out CachedCompany value) && (object)value != null && value.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return value.Company;
        }
        HttpClient client = httpClientFactory.CreateClient("external");
        using HttpResponseMessage response = await client.GetAsync("https://brasilapi.com.br/api/cnpj/v1/" + normalizedCnpj, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            PublicCompanyView result;
            await using (Stream content = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                using JsonDocument document = await JsonDocument.ParseAsync(content, default(JsonDocumentOptions), cancellationToken);
                PublicCompanyView primary = ParseBrasilApi(normalizedCnpj, document.RootElement);
                if (primary.Email.Length > 0 && primary.InscricaoEstadual.Length > 0)
                {
                    result = Cache(primary);
                }
                else
                {
                    try
                    {
                        result = Cache(Merge(primary, await LookupCnpjWsAsync(client, normalizedCnpj, cancellationToken)));
                    }
                    catch (HttpRequestException)
                    {
                        result = Cache(primary);
                    }
                }
            }
            return result;
        }
        return Cache(await LookupCnpjWsAsync(client, normalizedCnpj, cancellationToken));
    }

    /// <summary>
    /// Consulta um CNPJ sem reduzir os campos: devolve o JSON completo da fonte pública que respondeu.
    /// </summary>
    /// <param name="cnpj">CNPJ com ou sem máscara.</param>
    /// <param name="cancellationToken">Token para cancelar a chamada HTTP.</param>
    /// <returns>Fonte consultada e seu JSON original.</returns>
    /// <exception cref="InvalidDataException">Lançada quando o CNPJ é inválido ou não é encontrado.</exception>
    public async Task<PublicCnpjRawResponse> LookupRawAsync(string cnpj, CancellationToken cancellationToken)
    {
        string normalizedCnpj = OnlyDigits(cnpj);
        if (!IsValidCnpj(normalizedCnpj))
        {
            throw new InvalidDataException("CNPJ inválido.");
        }

        if (rawCache.TryGetValue(normalizedCnpj, out CachedRawCompany cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Response;
        }

        HttpClient client = httpClientFactory.CreateClient("external");
        HttpResponseMessage? brasilApiResponse = null;
        try
        {
            brasilApiResponse = await client.GetAsync("https://brasilapi.com.br/api/cnpj/v1/" + normalizedCnpj, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (brasilApiResponse.IsSuccessStatusCode)
            {
                return CacheRaw(new PublicCnpjRawResponse(normalizedCnpj, "BrasilAPI", await ReadJsonAsync(brasilApiResponse, cancellationToken)));
            }
        }
        finally
        {
            brasilApiResponse?.Dispose();
        }

        using HttpResponseMessage cnpjWsResponse = await client.GetAsync("https://publica.cnpj.ws/cnpj/" + normalizedCnpj, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (cnpjWsResponse.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidDataException("CNPJ não encontrado no cadastro público.");
        }

        cnpjWsResponse.EnsureSuccessStatusCode();
        return CacheRaw(new PublicCnpjRawResponse(normalizedCnpj, "CNPJ.ws", await ReadJsonAsync(cnpjWsResponse, cancellationToken)));
    }

    private PublicCompanyView Cache(PublicCompanyView company)
    {
        cache[company.Cnpj] = new CachedCompany(company, DateTimeOffset.UtcNow.AddHours(12.0));
        return company;
    }

    private PublicCnpjRawResponse CacheRaw(PublicCnpjRawResponse response)
    {
        rawCache[response.Cnpj] = new CachedRawCompany(response, DateTimeOffset.UtcNow.AddHours(12.0));
        return response;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(content, default, cancellationToken);
        return document.RootElement.Clone();
    }

    private static PublicCompanyView ParseBrasilApi(string normalizedCnpj, JsonElement root)
    {
        return new PublicCompanyView(normalizedCnpj, Text(root, "razao_social"), Text(root, "nome_fantasia"), Text(root, "inscricao_estadual"), Text(root, "email"), Text(root, "ddd_telefone_1"), Text(root, "cep"), Text(root, "codigo_municipio_ibge"), Text(root, "uf").ToUpperInvariant(), Text(root, "municipio"), Text(root, "logradouro"), Text(root, "numero"), Text(root, "complemento"), Text(root, "bairro"));
    }

    private static PublicCompanyView Merge(PublicCompanyView primary, PublicCompanyView fallback)
    {
        return primary with
        {
            RazaoSocial = Prefer(primary.RazaoSocial, fallback.RazaoSocial),
            NomeFantasia = Prefer(primary.NomeFantasia, fallback.NomeFantasia),
            InscricaoEstadual = Prefer(primary.InscricaoEstadual, fallback.InscricaoEstadual),
            Email = Prefer(primary.Email, fallback.Email),
            Telefone = Prefer(primary.Telefone, fallback.Telefone),
            Cep = Prefer(primary.Cep, fallback.Cep),
            CodigoIbge = Prefer(primary.CodigoIbge, fallback.CodigoIbge),
            Uf = Prefer(primary.Uf, fallback.Uf),
            Cidade = Prefer(primary.Cidade, fallback.Cidade),
            Endereco = Prefer(primary.Endereco, fallback.Endereco),
            Numero = Prefer(primary.Numero, fallback.Numero),
            Complemento = Prefer(primary.Complemento, fallback.Complemento),
            Bairro = Prefer(primary.Bairro, fallback.Bairro)
        };
        static string Prefer(string value, string alternative)
        {
            if (value.Length <= 0)
            {
                return alternative;
            }
            return value;
        }
    }

    private static async Task<PublicCompanyView> LookupCnpjWsAsync(HttpClient client, string normalizedCnpj, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync("https://publica.cnpj.ws/cnpj/" + normalizedCnpj, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidDataException("CNPJ não encontrado no cadastro público.");
        }
        response.EnsureSuccessStatusCode();
        PublicCompanyView result;
        await using (Stream content = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            using JsonDocument jsonDocument = await JsonDocument.ParseAsync(content, default(JsonDocumentOptions), cancellationToken);
            JsonElement rootElement = jsonDocument.RootElement;
            JsonElement jsonElement = Object(rootElement, "estabelecimento");
            JsonElement root = Object(jsonElement, "estado");
            JsonElement root2 = Object(jsonElement, "cidade");
            string inscricaoEstadual = FirstActiveStateRegistration(jsonElement);
            string telefone = Text(jsonElement, "ddd1") + Text(jsonElement, "telefone1");
            result = new PublicCompanyView(normalizedCnpj, Text(rootElement, "razao_social"), Text(jsonElement, "nome_fantasia"), inscricaoEstadual, Text(jsonElement, "email"), telefone, Text(jsonElement, "cep"), Text(root2, "ibge_id"), Text(root, "sigla").ToUpperInvariant(), Text(root2, "nome"), string.Join(' ', new string[2]
            {
                Text(jsonElement, "tipo_logradouro"),
                Text(jsonElement, "logradouro")
            }.Where((string value) => value.Length > 0)), Text(jsonElement, "numero"), Text(jsonElement, "complemento"), Text(jsonElement, "bairro"));
        }
        return result;
    }

    private static JsonElement Object(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return default(JsonElement);
        }
        return value;
    }

    private static string FirstActiveStateRegistration(JsonElement establishment)
    {
        if (!establishment.TryGetProperty("inscricoes_estaduais", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.TryGetProperty("ativo", out var value2) && value2.ValueKind == JsonValueKind.True)
            {
                return Text(item, "inscricao_estadual");
            }
        }
        if (value.GetArrayLength() <= 0)
        {
            return string.Empty;
        }
        return Text(value[0], "inscricao_estadual");
    }

    private static string Text(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }
        return value.ToString().Trim();
    }

    private static string OnlyDigits(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
    }

    private static bool IsValidCnpj(string cnpj)
    {
        if (cnpj.Length != 14 || cnpj.All((char character) => character == cnpj[0]))
        {
            return false;
        }
        if (CalculateDigit(cnpj, 12) == cnpj[12] - 48)
        {
            return CalculateDigit(cnpj, 13) == cnpj[13] - 48;
        }
        return false;
        static int CalculateDigit(string value, int length)
        {
            int num = length - 7;
            int num2 = 0;
            for (int j = 0; j < length; j++)
            {
                num2 += (value[j] - 48) * num;
                num = ((num < 3) ? 9 : (num - 1));
            }
            int num3 = num2 % 11;
            if (num3 >= 2)
            {
                return 11 - num3;
            }
            return 0;
        }
    }
}
