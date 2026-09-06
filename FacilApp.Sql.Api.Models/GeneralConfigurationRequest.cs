namespace FacilApp.Sql.Api.Models;

public sealed class GeneralConfigurationRequest
{
    public string NomeInstancia { get; set; } = string.Empty;

    public string NomeServicoWindows { get; set; } = string.Empty;

    public bool ServidorAtivo { get; set; }

    public int Porta { get; set; }

    public int TimeoutSegundos { get; set; }

    public int LimiteCorpoKb { get; set; }

    public int MaximoConexoes { get; set; }

    /// <summary>Habilita a Ponte Claude incorporada ao serviço C#.</summary>
    public bool PonteClaudeAtiva { get; set; }

    /// <summary>Porta exclusiva usada pela Ponte Claude.</summary>
    public int PortaPonteClaude { get; set; }

    /// <summary>Novo token da Ponte; vazio preserva o valor atual.</summary>
    public string TokenPonteClaude { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool ExigirToken { get; set; }

    public bool ConsultaLivre { get; set; }

    public bool PermitirLocalhost { get; set; }

    public bool PermitirRedeLocal { get; set; }

    public bool PermitirTailscale { get; set; }

    public bool LogAtivo { get; set; }

    public string NivelLog { get; set; } = "INFO";

    public int RetencaoDias { get; set; }

    public bool ArquivosAtivos { get; set; }

    public int TamanhoMaximoArquivoMb { get; set; }

    public string DiretorioBaseArquivos { get; set; } = string.Empty;

    public string ExtensoesPermitidas { get; set; } = string.Empty;
}
