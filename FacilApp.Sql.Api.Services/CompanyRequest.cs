namespace FacilApp.Sql.Api.Services;

/// <summary>Dados cadastrais enviados pelo console para criar ou atualizar uma empresa.</summary>
public sealed record CompanyRequest(string TipoPessoa, string Cnpj, string Email, string RazaoSocial, string? NomeFantasia, string? InscricaoEstadual, string? InscricaoMunicipal, string? Telefone, string Cep, string CodigoIbge, string Uf, string Cidade, string Endereco, string Numero, string? Complemento, string Bairro)
{
    /// <summary>Imagem da logo em data URL; opcional para preservar a logo existente.</summary>
    public string? LogoData { get; init; }
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
    /// <summary>Modelo comercial da empresa: vitalicio ou mensalidade. Nulo, vazio ou 0 significa mensalidade.</summary>
    public string? TipoPagamento { get; init; }
}
