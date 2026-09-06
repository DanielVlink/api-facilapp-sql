namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Credencial recuperada internamente após um login válido em banco externo.
/// </summary>
/// <remarks>
/// Este tipo nunca deve ser serializado em respostas nem registrado em logs.
/// </remarks>
/// <param name="ClientId">Identificador público da credencial OAuth.</param>
/// <param name="ClientSecret">Segredo usado somente na validação interna.</param>
public sealed record ExternalCredentialResult(string ClientId, string ClientSecret);
