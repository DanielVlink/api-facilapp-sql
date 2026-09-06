using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;

namespace FacilApp.Sql.Api.Services;

/// <summary>Consulta dados de pessoa física na FonteData.</summary>
public sealed record PublicCpfLookupResponse(PublicCompanyView Pessoa, JsonElement Retorno);

public sealed class CpfLookupService
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IniConfiguration ini;

    public CpfLookupService(IHttpClientFactory httpClientFactory, IniConfiguration ini)
    {
        this.httpClientFactory = httpClientFactory;
        this.ini = ini;
    }

    public async Task<PublicCpfLookupResponse> LookupAsync(string cpf, CancellationToken cancellationToken)
    {
        string normalizedCpf = OnlyDigits(cpf);
        if (!IsValidCpf(normalizedCpf))
        {
            throw new InvalidDataException("CPF inválido.");
        }
        if (!ini.GetBoolean("FonteDataCPF", "Ativo", false))
        {
            throw new InvalidOperationException("A consulta FonteDataCPF está desativada no FacilAppSQL.ini.");
        }
        if (ini.Get("FonteDataCPF", "API_KEY").Trim().Length == 0)
        {
            throw new InvalidOperationException("Configure FonteDataCPF.API_KEY no FacilAppSQL.ini.");
        }
        return await LookupFonteDataAsync(normalizedCpf, cancellationToken);
    }

    private async Task<PublicCpfLookupResponse> LookupFonteDataAsync(string normalizedCpf, CancellationToken cancellationToken)
    {
        string apiKey = ini.Get("FonteDataCPF", "API_KEY").Trim();
        string baseUrl = ini.Get("FonteDataCPF", "URL_BASE",
            "https://app.fontedata.com/api/v1/consulta/cadastro-pf-basica").Trim().TrimEnd('/');
        using HttpRequestMessage request = new(HttpMethod.Get,
            baseUrl + "?cpf=" + Uri.EscapeDataString(normalizedCpf));
        request.Headers.Add("X-API-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");

        HttpClient client = httpClientFactory.CreateClient("external");
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(content, default, cancellationToken);
        JsonElement root = document.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            string error = FindText(root, "message", "mensagem", "error");
            throw new InvalidDataException(error.Length > 0 ? error : $"FonteData respondeu HTTP {(int)response.StatusCode}.");
        }

        string name = FindText(root, "nome", "nomeCompleto");
        if (name.Length == 0)
        {
            throw new InvalidDataException("A FonteData não retornou o nome da pessoa.");
        }
        PublicCompanyView person = new(
            normalizedCpf,
            name,
            name,
            string.Empty,
            FindText(root, "enderecoEmail", "email"),
            FindText(root, "telefoneComDDD", "telefone"),
            FindText(root, "cep"),
            string.Empty,
            FindText(root, "uf").ToUpperInvariant(),
            FindText(root, "cidade"),
            FindText(root, "logradouro", "endereco"),
            FindText(root, "numero"),
            FindText(root, "complemento"),
            FindText(root, "bairro"));
        return new PublicCpfLookupResponse(person, root.Clone());
    }

    private static string FindText(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (names.Any(name => Normalize(name) == Normalize(property.Name))
                    && property.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array and not JsonValueKind.Null)
                {
                    return property.Value.ToString().Trim();
                }
            }
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string found = FindText(property.Value, names);
                if (found.Length > 0) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string found = FindText(item, names);
                if (found.Length > 0) return found;
            }
        }
        return string.Empty;
    }

    private static string Normalize(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character)).ToArray()).ToLowerInvariant();
    }

    private static string OnlyDigits(string value) => new(value.Where(char.IsDigit).ToArray());

    private static bool IsValidCpf(string cpf)
    {
        if (cpf.Length != 11 || cpf.All(character => character == cpf[0])) return false;
        int Digit(int length)
        {
            int sum = 0;
            for (int i = 0; i < length; i++) sum += (cpf[i] - '0') * (length + 1 - i);
            int remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }
        return Digit(9) == cpf[9] - '0' && Digit(10) == cpf[10] - '0';
    }
}
