namespace FacilApp.Sql.Api.Services;

/// <summary>Empresa vinculada ao primeiro usuário ativo que utiliza uma credencial.</summary>
public sealed record ClientCredentialContext(string IdFilial, string Cnpj);
