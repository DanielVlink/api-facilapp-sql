namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Representa a resposta interna da emissão de um token OAuth 2.0.
/// </summary>
/// <param name="AccessToken">Bearer temporário assinado pela instalação.</param>
/// <param name="ExpiresIn">Validade do token em segundos.</param>
/// <param name="Scope">Escopos concedidos, separados por espaço.</param>
public sealed record AccessTokenResult(string AccessToken, int ExpiresIn, string Scope);
