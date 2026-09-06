using System.Text.Json;

namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Representa os campos empresariais normalizados para o cadastro da API.
/// </summary>
public sealed record PublicCompanyView(string Cnpj, string RazaoSocial, string NomeFantasia, string InscricaoEstadual, string Email, string Telefone, string Cep, string CodigoIbge, string Uf, string Cidade, string Endereco, string Numero, string Complemento, string Bairro);

/// <summary>
/// Representa o JSON integral devolvido por uma fonte pública de consulta de CNPJ.
/// </summary>
/// <param name="Cnpj">CNPJ consultado, somente com dígitos.</param>
/// <param name="Fonte">Nome da fonte pública que respondeu a consulta.</param>
/// <param name="Retorno">JSON original recebido, sem redução de campos.</param>
public sealed record PublicCnpjRawResponse(string Cnpj, string Fonte, JsonElement Retorno);
