using System;

namespace FacilApp.Sql.Api.Services;

/// <summary>Pagamento mensal recebido de uma empresa.</summary>
public sealed record CompanyPaymentRequest(DateTimeOffset DataPagamento, decimal Valor)
{
    /// <summary>Identificação opcional do recebimento: PIX, boleto, recibo etc.</summary>
    public string? Referencia { get; init; }
}

/// <summary>Registro seguro de pagamento, associado somente a uma empresa interna.</summary>
public sealed record CompanyPaymentView(string Id, string EmpresaId, string DataPagamento, decimal Valor, string? Referencia, string CriadoEm);

/// <summary>Estado comercial que a API aplica antes de emitir o token de um usuário local.</summary>
public sealed record CompanyPaymentStatus(
    string TipoPagamento,
    bool AcessoLiberado,
    string Situacao,
    string? UltimoPagamento,
    string? LimiteAcesso,
    int DiasRestantes);

/// <summary>Linha do browse de mensalidades, incluindo a situação calculada da empresa.</summary>
public sealed record CompanyPaymentOverview(
    string EmpresaId,
    string Cnpj,
    string RazaoSocial,
    string? NomeFantasia,
    CompanyPaymentStatus Pagamento);
