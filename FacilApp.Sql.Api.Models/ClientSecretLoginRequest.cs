using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

/// <summary>Credencial de aplicação para emissão de Bearer sem login de usuário.</summary>
public sealed class ClientSecretLoginRequest
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;
}
