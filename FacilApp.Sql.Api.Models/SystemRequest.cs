using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

/// <summary>Dados alteráveis de um sistema cadastrado na API.</summary>
public sealed class SystemRequest
{
    [JsonPropertyName("codigo")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("nome")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ativo")]
    public bool Active { get; set; } = true;
}
