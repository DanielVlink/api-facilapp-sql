namespace FacilApp.Sql.Api.Models;

/// <summary>
/// Credenciais privadas usadas pelo LoginVlink para solicitar Bearers SQL e NFe.
/// </summary>
/// <remarks>
/// Esta classe é recebida somente pelo console MASTER em loopback. Os valores não são
/// retornados por nenhum endpoint e não devem ser registrados em logs.
/// </remarks>
public sealed class VlinkConfigurationRequest
{
    /// <summary>Client ID da API SQL.</summary>
    public string SqlClientId { get; set; } = string.Empty;

    /// <summary>Client ID da API NFe.</summary>
    public string NfeClientId { get; set; } = string.Empty;

    /// <summary>Client Secret da API NFe.</summary>
    public string NfeClientSecret { get; set; } = string.Empty;
}
