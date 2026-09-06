using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

/// <summary>
/// Conjunto de menus e dashboards autorizado para um usuário local.
/// </summary>
public sealed class LocalUserMenuRequest
{
    /// <summary>Lista JSON dos itens do menu lateral.</summary>
    [JsonPropertyName("menu_data")]
    public string MenuData { get; set; } = "[]";

    /// <summary>Lista JSON dos itens do menu superior.</summary>
    [JsonPropertyName("menu_superior_data")]
    public string MenuSuperiorData { get; set; } = "[]";

    /// <summary>Lista JSON dos dashboards habilitados.</summary>
    [JsonPropertyName("dashboard_data")]
    public string DashBoardData { get; set; } = "[]";
}
