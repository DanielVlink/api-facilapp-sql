using System.Collections.Generic;

namespace FacilApp.Sql.Api.Configuration;

/// <summary>
/// Define uma chave reconhecida pelo arquivo FacilAppSQL.ini.
/// </summary>
/// <param name="Section">Seção INI.</param>
/// <param name="Key">Nome da chave.</param>
/// <param name="DefaultValue">Valor aplicado quando a chave não estiver gravada.</param>
/// <param name="IsSecret">Indica que o valor nunca deve ser devolvido ao navegador ou aos logs.</param>
/// <param name="RequiresRestart">Indica que a mudança altera a infraestrutura iniciada pelo processo.</param>
/// <param name="Description">Finalidade operacional da chave.</param>
public sealed record IniFieldDefinition(
    string Section,
    string Key,
    string DefaultValue,
    bool IsSecret,
    bool RequiresRestart,
    string Description);

/// <summary>
/// Catálogo central e documentado de todos os campos INI reconhecidos pela API.
/// Ele evita que instalador, console e código C# evoluam com nomes divergentes.
/// </summary>
public static class IniFieldCatalog
{
    /// <summary>Obtém a lista imutável das chaves suportadas.</summary>
    public static IReadOnlyList<IniFieldDefinition> Fields { get; } = new IniFieldDefinition[]
    {
        Field("Instancia", "Nome", "FacilApp SQL", false, false, "Nome legível da instância."),
        Field("Servico", "NomeWindows", "FacilAppSQL", false, true, "Nome do serviço Windows."),
        Field("Servico", "IpValido", "", false, false, "IP administrativo legado autorizado."),

        Field("ServidorHTTP", "Ativo", "1", false, true, "Habilita o servidor HTTP da API."),
        Field("ServidorHTTP", "Porta", "5050", false, true, "Porta principal da API."),
        Field("ServidorHTTP", "Portas", "5050", false, true, "Lista de portas principais separadas por vírgula."),
        Field("ServidorHTTP", "LimiteCorpoKB", "1024", false, true, "Compatibilidade com o limite histórico do corpo HTTP."),
        Field("ServidorHTTP", "TimeoutSegundos", "30", false, false, "Timeout das operações e integrações."),
        Field("ServidorHTTP", "MaxConexoes", "500", false, true, "Máximo de conexões simultâneas do Kestrel."),
        Field("ServidorHTTP", "IPPublico", "1", false, true, "Compatibilidade com publicação em rede."),
        Field("ServidorHTTP", "ModoTeste", "1", false, false, "Habilita comportamento de ambiente de teste."),
        Field("ServidorHTTP", "IpValido", "", false, false, "IP HTTP legado autorizado."),
        Field("ServidorHTTP", "PonteHtmlAtiva", "0", false, true, "Chave legada mantida para compatibilidade."),

        Field("PonteClaude", "Ativo", "1", false, true, "Habilita a Bridge incorporada ao serviço C#."),
        Field("PonteClaude", "Porta", "3000", false, true, "Porta local exclusiva da Bridge."),
        Field("PonteClaude", "Token", "", true, false, "Token relido a cada chamada da Bridge."),

        Field("Seguranca", "ExigirToken", "1", false, false, "Exige Bearer nas operações protegidas."),
        Field("Seguranca", "ConsultaLivre", "1", false, false, "Permite as funções SQL livres autorizadas pelo escopo."),
        Field("Seguranca", "Token", "", true, false, "Token legado da API."),
        Field("Seguranca", "ExigirHTTPSPublico", "1", false, false, "Exige HTTPS em acessos públicos."),

        Field("Log", "Ativo", "1", false, false, "Habilita logs operacionais."),
        Field("Log", "Nivel", "INFO", false, false, "Nível mínimo do log."),
        Field("Log", "RetencaoDias", "30", false, false, "Retenção dos arquivos de log."),

        Field("SQL", "Ativo", "1", false, false, "Habilita o provedor SQL Server."),
        Field("SQL", "Servidor", "127.0.0.1", false, false, "Servidor SQL Server."),
        Field("SQL", "Porta", "1433", false, false, "Porta do SQL Server."),
        Field("SQL", "Banco", "FACILAPP_API", false, false, "Banco SQL Server padrão."),
        Field("SQL", "Usuario", "", false, false, "Usuário técnico do SQL Server."),
        Field("SQL", "Senha", "", true, false, "Senha técnica do SQL Server."),
        Field("SQL", "ServidorPadrao", "127.0.0.1", false, false, "Servidor legado usado quando a chamada não informa host."),

        Field("PostgreSQL", "Ativo", "1", false, false, "Habilita o provedor PostgreSQL."),
        Field("PostgreSQL", "Servidor", "127.0.0.1", false, false, "Servidor PostgreSQL."),
        Field("PostgreSQL", "Porta", "5432", false, false, "Porta do PostgreSQL."),
        Field("PostgreSQL", "Banco", "FACILAPP_API", false, false, "Banco PostgreSQL padrão."),
        Field("PostgreSQL", "Usuario", "", false, false, "Usuário técnico do PostgreSQL."),
        Field("PostgreSQL", "Senha", "", true, false, "Senha técnica do PostgreSQL."),

        Field("MariaDB", "Ativo", "1", false, false, "Habilita o provedor MariaDB."),
        Field("MariaDB", "Servidor", "127.0.0.1", false, false, "Servidor MariaDB."),
        Field("MariaDB", "Porta", "3306", false, false, "Porta do MariaDB."),
        Field("MariaDB", "Banco", "FACILAPP_API", false, false, "Banco MariaDB padrão."),
        Field("MariaDB", "Usuario", "", false, false, "Usuário técnico do MariaDB."),
        Field("MariaDB", "Senha", "", true, false, "Senha técnica do MariaDB."),

        Field("MySQL", "Ativo", "1", false, false, "Habilita o provedor MySQL."),
        Field("MySQL", "Servidor", "127.0.0.1", false, false, "Servidor MySQL."),
        Field("MySQL", "Porta", "3307", false, false, "Porta do MySQL."),
        Field("MySQL", "Banco", "FACILAPP_API", false, false, "Banco MySQL padrão."),
        Field("MySQL", "Usuario", "", false, false, "Usuário técnico do MySQL."),
        Field("MySQL", "Senha", "", true, false, "Senha técnica do MySQL."),

        Field("Oracle", "Ativo", "1", false, false, "Habilita o provedor Oracle."),
        Field("Oracle", "Servidor", "127.0.0.1", false, false, "Servidor Oracle."),
        Field("Oracle", "Porta", "1521", false, false, "Porta do Oracle."),
        Field("Oracle", "Banco", "FREEPDB1", false, false, "Service Name ou banco Oracle."),
        Field("Oracle", "Usuario", "", false, false, "Usuário técnico do Oracle."),
        Field("Oracle", "Senha", "", true, false, "Senha técnica do Oracle."),
        Field("Oracle", "DSN", "FacilAppOracle", false, false, "DSN opcional do Oracle."),

        Field("HFSQL", "Ativo", "1", false, false, "Habilita o provedor HFSQL."),
        Field("HFSQL", "Servidor", "127.0.0.1", false, false, "Servidor HFSQL."),
        Field("HFSQL", "Porta", "4900", false, false, "Porta do HFSQL."),
        Field("HFSQL", "Banco", "FACILAPP_API", false, false, "Banco HFSQL padrão."),
        Field("HFSQL", "Usuario", "", false, false, "Usuário técnico do HFSQL."),
        Field("HFSQL", "Senha", "", true, false, "Senha técnica do HFSQL."),
        Field("HFSQL", "DSN", "", false, false, "DSN opcional do HFSQL."),

        Field("SQLite", "Ativo", "1", false, false, "Habilita o provedor SQLite."),
        Field("SQLite", "Diretorio", "BasesSQLite", false, false, "Diretório-base autorizado para bancos SQLite."),
        Field("SQLite", "Banco", "facilapp.db", false, false, "Arquivo SQLite padrão."),

        Field("Sincronizacao", "Ativo", "0", false, false, "Habilita a sincronização SQLite para a nuvem."),
        Field("Sincronizacao", "TipoBancoNuvem", "PostgreSQL", false, false, "Banco de destino da sincronização."),
        Field("Sincronizacao", "BancoLocal", "gourmet.db", false, false, "Arquivo SQLite usado como origem."),
        Field("Sincronizacao", "BancoNuvem", "FACILAPP_GOURMET", false, false, "Banco PostgreSQL usado como destino."),
        Field("Sincronizacao", "DocumentoEmpresa", "", false, false, "CPF/CNPJ; opcional quando inicia o nome do BancoNuvem."),
        Field("Sincronizacao", "IntervaloSegundos", "10", false, false, "Intervalo entre sincronizações automáticas."),
        Field("Sincronizacao", "QuantidadePorLote", "100", false, false, "Quantidade máxima processada por lote."),

        Field("Arquivos", "Ativo", "1", false, false, "Habilita o repositório de arquivos."),
        Field("Arquivos", "DiretorioBase", "C:\\FacilApp\\Repositorio", false, false, "Diretório-base do repositório."),
        Field("Arquivos", "TamanhoMaximoMB", "15", false, false, "Tamanho máximo de cada arquivo."),
        Field("Arquivos", "ExtensoesPermitidas", "pdf,jpg,jpeg,png,webp,xml,ini,js,json,txt,csv", false, false, "Extensões aceitas pelo repositório."),

        Field("Scripts", "Ativo", "1", false, false, "Habilita a execução autenticada de scripts PS1 existentes."),
        Field("Scripts", "Diretorio", "C:\\FacilApp\\API_FACILAPP_SQL\\Scripts", false, false, "Pasta exclusiva dos scripts PS1 executáveis."),
        Field("Scripts", "DiretoriosPermitidos", "C:\\FacilApp\\API_FACILAPP_SQL\\Scripts", false, false, "Pastas locais ou UNC separadas por ponto e vírgula."),
        Field("Scripts", "TimeoutSegundos", "300", false, false, "Tempo máximo de execução de cada script."),

        Field("IA", "OPENAI_API_KEY", "", true, false, "Chave da integração OpenAI."),
        Field("IA", "OPENAI_MODEL", "gpt-4.1-mini", false, false, "Modelo OpenAI selecionado."),
        Field("IA", "ARQUIVO_INTENCOES", "EXECUTAR_VIA_IA.md", false, false, "Arquivo local de instruções para IA."),

        Field("OPENSTREETMAP", "USER_AGENT", "FacilAppSQL/1.0", false, false, "Identificação enviada ao OpenStreetMap."),
        Field("OPENSTREETMAP", "CLIENT_ID", "", false, false, "Client ID opcional do provedor de mapa."),
        Field("OPENSTREETMAP", "CLIENT_SECRET", "", true, false, "Client Secret opcional do provedor de mapa."),
        Field("OPENSTREETMAP", "REDIRECT_URI", "http://127.0.0.1:8080/", false, false, "URI de retorno do provedor de mapa."),

        Field("FonteDataCPF", "Ativo", "1", false, false, "Usa a FonteData como consulta principal de CPF."),
        Field("FonteDataCPF", "API_KEY", "", true, false, "Chave privada enviada no cabeçalho X-API-Key."),
        Field("FonteDataCPF", "URL_BASE", "https://app.fontedata.com/api/v1/consulta/cadastro-pf-basica", false, false, "Endpoint da consulta Cadastro PF Básica."),

        Field("WHATSAPP_ZAPI", "Ativo", "0", false, false, "Habilita a integração Z-API."),
        Field("WHATSAPP_ZAPI", "BASE_URL", "https://api.z-api.io", false, false, "URL-base da Z-API."),
        Field("WHATSAPP_ZAPI", "INSTANCE_ID", "", false, false, "Identificador da instância Z-API."),
        Field("WHATSAPP_ZAPI", "INSTANCE_TOKEN", "", true, false, "Token privado da instância Z-API."),
        Field("WHATSAPP_ZAPI", "CLIENT_TOKEN", "", true, false, "Token privado do cliente Z-API."),
        Field("WHATSAPP_ZAPI", "GRUPO_PADRAO", "", false, false, "Grupo padrão para mensagens."),
        Field("WHATSAPP_ZAPI", "WEBHOOK_SECRET", "", true, false, "Segredo de validação do webhook."),
        Field("WHATSAPP_ZAPI", "NUMERO_FONE", "", false, false, "Número padrão da integração."),

        Field("Rede", "IPFixo", "", false, false, "IP público ou fixo informado pelo administrador."),
        Field("Rede", "SiteNuvem", "", false, false, "Endereço web auxiliar."),
        Field("Rede", "PermitirLocalhost", "1", false, false, "Permite chamadas locais."),
        Field("Rede", "PermitirRedeLocal", "1", false, false, "Permite chamadas da rede privada."),
        Field("Rede", "PermitirTailscale", "1", false, false, "Permite endereços da rede Tailscale."),
        Field("WEB", "URL_BASE", "", false, false, "URL-base web auxiliar."),

        Field("ALERTAS", "Ativo", "0", false, false, "Habilita alertas automáticos."),
        Field("ALERTAS", "NumeroFacilApp", "", false, false, "Número que recebe alertas."),
        Field("ALERTAS", "IntervaloMinutos", "5", false, false, "Intervalo mínimo entre alertas."),

        Field("LoginVlink", "SqlClientId", "", false, false, "Credencial SQL selecionada no SQLite administrativo."),
        Field("LoginVlink", "SqlClientSecret", "", true, false, "Client Secret SQL legado, preservado por compatibilidade."),
        Field("LoginVlink", "SqlTokenUrl", "", false, false, "Endpoint OAuth da API SQL."),
        Field("LoginVlink", "NfeClientId", "", false, false, "Client ID da API NFe."),
        Field("LoginVlink", "NfeClientSecret", "", true, false, "Client Secret da API NFe."),
        Field("LoginVlink", "NfeTokenUrl", "", false, false, "Endpoint OAuth da API NFe.")
    };

    private static IniFieldDefinition Field(
        string section,
        string key,
        string defaultValue,
        bool isSecret,
        bool requiresRestart,
        string description)
    {
        return new IniFieldDefinition(section, key, defaultValue, isSecret, requiresRestart, description);
    }

}
