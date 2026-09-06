using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

/// <summary>
/// Dados administrativos usados para cadastrar um usuário local da API.
/// </summary>
public sealed class LocalUserRequest
{
    /// <summary>Sistema ao qual o usuário pertence.</summary>
    [JsonPropertyName("sistema_id")]
    public string SistemaId { get; set; } = "curso";

    /// <summary>Identificador interno da empresa proprietária do usuário.</summary>
    [JsonPropertyName("empresa_id")]
    public string EmpresaId { get; set; } = string.Empty;

    /// <summary>Nome exibido no administrador.</summary>
    [JsonPropertyName("nome")]
    public string Nome { get; set; } = string.Empty;

    /// <summary>Identificador informado no login.</summary>
    [JsonPropertyName("usuario")]
    public string Usuario { get; set; } = string.Empty;

    /// <summary>Senha recebida somente na criação e armazenada por hash.</summary>
    [JsonPropertyName("senha")]
    public string Senha { get; set; } = string.Empty;

    /// <summary>Client ID da credencial OAuth ativa vinculada ao usuário.</summary>
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Itens JSON do menu lateral liberados para o usuário.</summary>
    [JsonPropertyName("menu_data")]
    public string MenuData { get; set; } = "[]";

    /// <summary>Itens JSON do menu superior liberados para o usuário.</summary>
    [JsonPropertyName("menu_superior_data")]
    public string MenuSuperiorData { get; set; } = "[]";

    /// <summary>Itens JSON dos dashboards liberados para o usuário.</summary>
    [JsonPropertyName("dashboard_data")]
    public string DashBoardData { get; set; } = "[]";
}
