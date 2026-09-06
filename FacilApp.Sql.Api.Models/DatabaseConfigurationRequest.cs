namespace FacilApp.Sql.Api.Models;

public sealed class DatabaseConfigurationRequest
{
    public string Tipo { get; set; } = string.Empty;

    public bool Ativo { get; set; }

    public string Servidor { get; set; } = string.Empty;

    public int Porta { get; set; }

    public string Banco { get; set; } = string.Empty;

    public string Usuario { get; set; } = string.Empty;

    public string Senha { get; set; } = string.Empty;

    public string Dsn { get; set; } = string.Empty;

    /// <summary>
    /// Diretório-base autorizado para arquivos SQLite. Caminhos informados nas chamadas
    /// permanecem obrigatoriamente dentro desta pasta.
    /// </summary>
    public string Diretorio { get; set; } = string.Empty;
}
