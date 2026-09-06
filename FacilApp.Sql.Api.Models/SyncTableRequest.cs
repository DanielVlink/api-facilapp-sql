using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

/// <summary>Solicita a sincronizacao generica de uma tabela SQLite com o PostgreSQL.</summary>
public sealed class SyncTableRequest
{
    [JsonPropertyName("tabela")]
    public string Tabela { get; set; } = string.Empty;

    [JsonPropertyName("banco")]
    public string Banco { get; set; } = string.Empty;
}
