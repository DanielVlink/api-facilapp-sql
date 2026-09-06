namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Visão segura de um usuário local, sem senha, hash ou salt.
/// </summary>
/// <param name="Id">Identificador interno.</param>
/// <param name="EmpresaId">Identificador da empresa proprietária.</param>
/// <param name="EmpresaNome">Razão social da empresa proprietária.</param>
/// <param name="Cnpj">CNPJ da empresa proprietária.</param>
/// <param name="Nome">Nome exibido no administrador.</param>
/// <param name="Usuario">Identificador usado no login.</param>
/// <param name="ClientId">Credencial OAuth vinculada.</param>
/// <param name="CredencialNome">Nome público da credencial vinculada.</param>
/// <param name="Ativo">Indica se o usuário pode autenticar.</param>
/// <param name="CriadoEm">Data de criação em formato ISO 8601.</param>
/// <param name="MenuData">Itens JSON do menu lateral liberados.</param>
/// <param name="MenuSuperiorData">Itens JSON do menu superior liberados.</param>
/// <param name="DashBoardData">Itens JSON dos dashboards liberados.</param>
public sealed record LocalUserView(
    string Id,
    string EmpresaId,
    string SistemaId,
    string EmpresaNome,
    string Cnpj,
    string Nome,
    string Usuario,
    string ClientId,
    string CredencialNome,
    bool Ativo,
    string CriadoEm,
    string MenuData,
    string MenuSuperiorData,
    string DashBoardData);

/// <summary>
/// Resultado interno de uma autenticação local válida.
/// </summary>
/// <param name="Usuario">Usuário autenticado.</param>
/// <param name="Credencial">Credencial OAuth ativa vinculada.</param>
public sealed record LocalUserAuthentication(
    LocalUserView Usuario,
    ApiCredentialView Credencial,
    CompanyPaymentStatus Pagamento);
