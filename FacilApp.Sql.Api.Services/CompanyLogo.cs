namespace FacilApp.Sql.Api.Services;

/// <summary>Imagem persistida da logo de uma empresa.</summary>
public sealed record CompanyLogo(string MimeType, byte[] Data);
