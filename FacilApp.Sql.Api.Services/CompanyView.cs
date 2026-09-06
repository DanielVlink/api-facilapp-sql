namespace FacilApp.Sql.Api.Services;

public sealed record CompanyView(string Id, string TipoPessoa, string Cnpj, string Email, string RazaoSocial, string? NomeFantasia, string? InscricaoEstadual, string? InscricaoMunicipal, string? Telefone, string Cep, string CodigoIbge, string Uf, string Cidade, string Endereco, string Numero, string? Complemento, string Bairro, string CriadoEm, string AtualizadoEm)
{
    public string? Genero { get; init; }
    public string? DataNascimento { get; init; }
    public string? NomeMae { get; init; }
    public int? Idade { get; init; }
    public string? Zodiaco { get; init; }
    public string? TelefonesJson { get; init; }
    public string? EnderecosJson { get; init; }
    public string? EmailsJson { get; init; }
    public string? SalarioEstimado { get; init; }
    public string? StatusCadastral { get; init; }
    public string? DataStatusCadastral { get; init; }
    public string? UltimaAtualizacaoFonte { get; init; }
    public string? ConsultaJson { get; init; }
    public string TipoPagamento { get; init; } = "mensalidade";
}
