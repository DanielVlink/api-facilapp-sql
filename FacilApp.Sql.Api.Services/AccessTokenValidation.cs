using System;
using System.Collections.Generic;
using System.Linq;

namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Resultado da validação criptográfica e semântica de um Bearer.
/// </summary>
/// <param name="IsValid">Indica se o token pode ser aceito.</param>
/// <param name="ClientId">Identificador da credencial que originou o token.</param>
/// <param name="Scopes">Escopos concedidos ao token.</param>
public sealed record AccessTokenValidation(
    bool IsValid,
    string ClientId,
    IReadOnlyList<string> Scopes)
{
    /// <summary>
    /// Resultado compartilhado para tokens inválidos, sem dados de credencial.
    /// </summary>
    public static AccessTokenValidation Invalid { get; } = new(
        IsValid: false,
        string.Empty,
        Array.Empty<string>());

    /// <summary>
    /// Verifica se o token contém um escopo específico.
    /// </summary>
    /// <param name="scope">Escopo necessário para a operação.</param>
    /// <returns><see langword="true"/> quando o escopo foi concedido.</returns>
    public bool HasScope(string scope)
    {
        return Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
    }
}
