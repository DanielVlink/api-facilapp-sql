using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

/// <summary>
/// Dados administrativos usados para substituir a senha de um usuário local.
/// </summary>
public sealed class LocalUserPasswordRequest
{
    /// <summary>Nova senha recebida somente para geração do hash persistido.</summary>
    [JsonPropertyName("senha")]
    public string Senha { get; set; } = string.Empty;
}
