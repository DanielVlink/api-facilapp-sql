using System.Text.Json;
using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

/// <summary>
/// Define o banco SQLite e os três catálogos de menu que serão aplicados
/// aos usuários ativos pela console administrativa.
/// </summary>
public sealed class MenuProfileUpdateRequest
{
    /// <summary>
    /// Nome do arquivo SQLite dentro do diretório autorizado pelo INI.
    /// Caminhos absolutos e travessia de diretórios não são aceitos.
    /// </summary>
    [JsonPropertyName("banco")]
    public string Banco { get; set; } = "CURSO.db";

    /// <summary>Itens do menu lateral, enviados como uma lista JSON.</summary>
    [JsonPropertyName("menu_data")]
    public JsonElement MenuData { get; set; }

    /// <summary>Itens do menu superior, enviados como uma lista JSON.</summary>
    [JsonPropertyName("menu_superior_data")]
    public JsonElement MenuSuperiorData { get; set; }

    /// <summary>Itens das dashboards, enviados como uma lista JSON.</summary>
    [JsonPropertyName("dashboard_data")]
    public JsonElement DashBoardData { get; set; }
}
