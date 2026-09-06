using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

/// <summary>
/// Pedido de autenticação de um usuário armazenado no SQLite interno.
/// </summary>
public sealed class LocalUserLoginRequest
{
    /// <summary>Sistema do contexto ativo. Com CNPJ, devolve permissões deste sistema; vazio ativa o modo legado de usuário e senha.</summary>
    [JsonPropertyName("sistema_id")]
    public string SistemaId { get; set; } = string.Empty;

    /// <summary>Identificador do usuário.</summary>
    [JsonPropertyName("usuario")]
    public string Usuario { get; set; } = string.Empty;

    /// <summary>Senha do usuário. O valor nunca é persistido nem registrado em log.</summary>
    [JsonPropertyName("senha")]
    public string Senha { get; set; } = string.Empty;

    /// <summary>Id da empresa. Quando informado, o login é resolvido somente dentro dela.</summary>
    [JsonPropertyName("empresa_id")]
    public string EmpresaId { get; set; } = string.Empty;

    /// <summary>CNPJ da empresa (somente números ou formatado). Alternativa a empresa_id.</summary>
    [JsonPropertyName("cnpj")]
    public string Cnpj { get; set; } = string.Empty;

}
