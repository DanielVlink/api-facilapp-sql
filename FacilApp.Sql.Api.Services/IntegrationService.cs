using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;

namespace FacilApp.Sql.Api.Services;

public sealed class IntegrationService
{
    private readonly IHttpClientFactory httpClientFactory;

    private readonly IniConfiguration configuration;

    public IntegrationService(IHttpClientFactory httpClientFactory, IniConfiguration configuration)
    {
        this.httpClientFactory = httpClientFactory;
        this.configuration = configuration;
    }

    public async Task<JsonElement> SendZApiTextAsync(string phone, string message, CancellationToken cancellationToken)
    {
        if (!configuration.GetBoolean("WHATSAPP_ZAPI", "Ativo"))
        {
            throw new InvalidOperationException("Z-API desativada.");
        }
        string value = configuration.Get("WHATSAPP_ZAPI", "BASE_URL", "https://api.z-api.io").TrimEnd('/');
        string value2 = Required("WHATSAPP_ZAPI", "INSTANCE_ID");
        string value3 = Required("WHATSAPP_ZAPI", "INSTANCE_TOKEN");
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{value}/instances/{value2}/token/{value3}/send-text");
        string text = configuration.Get("WHATSAPP_ZAPI", "CLIENT_TOKEN");
        if (text.Length > 0)
        {
            request.Headers.Add("Client-Token", text);
        }
        request.Content = JsonContent.Create(new { phone, message });
        using HttpResponseMessage response = await httpClientFactory.CreateClient("external").SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Z-API recusou o envio ({(int)response.StatusCode}).");
        }
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public async Task<JsonElement> AskOpenAiAsync(string prompt, bool webSearch, bool structured, CancellationToken cancellationToken)
    {
        string parameter = Required("IA", "OPENAI_API_KEY");
        string model = configuration.Get("IA", "OPENAI_MODEL", "gpt-4.1-mini");
        object value = new
        {
            model = model,
            input = prompt,
            tools = ((!webSearch) ? Array.Empty<object>() : new object[1]
            {
                new
                {
                    type = "web_search_preview"
                }
            }),
            text = (structured ? new
            {
                format = new
                {
                    type = "json_object"
                }
            } : null)
        };
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", parameter);
        request.Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await httpClientFactory.CreateClient("external").SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI recusou a consulta ({(int)response.StatusCode}).");
        }
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public static string ExtractOpenAiText(JsonElement response)
    {
        if (response.TryGetProperty("output_text", out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }
        if (!response.TryGetProperty("output", out var value2) || value2.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }
        foreach (JsonElement item in value2.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var value3) || value3.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (JsonElement item2 in value3.EnumerateArray())
            {
                if (item2.TryGetProperty("text", out var value4) && value4.ValueKind == JsonValueKind.String)
                {
                    return value4.GetString() ?? string.Empty;
                }
            }
        }
        return string.Empty;
    }

    public async Task<JsonElement> GeocodeAsync(string query, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=5&q=" + Uri.EscapeDataString(query));
        request.Headers.UserAgent.ParseAdd(configuration.Get("OPENSTREETMAP", "USER_AGENT", "FacilAppSQL/1.0"));
        using HttpResponseMessage response = await httpClientFactory.CreateClient("external").SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
    }

    private string Required(string section, string key)
    {
        string text = configuration.Get(section, key);
        if (text.Length <= 0)
        {
            throw new InvalidOperationException(section + "." + key + " não configurado.");
        }
        return text;
    }
}
