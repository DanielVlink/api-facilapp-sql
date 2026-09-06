using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;
using FacilApp.Sql.Api.Models;

namespace FacilApp.Sql.Api.Services;

public sealed class LegacyDispatcher
{
    private readonly DatabaseService databases;

    private readonly RepositoryService repository;

    private readonly IntegrationService integrations;

    private readonly IniConfiguration configuration;

    public LegacyDispatcher(DatabaseService databases, RepositoryService repository, IntegrationService integrations, IniConfiguration configuration)
    {
        this.databases = databases;
        this.repository = repository;
        this.integrations = integrations;
        this.configuration = configuration;
    }

    public async Task<object> ExecuteAsync(ExecuteRequest request, bool isLocal, CancellationToken cancellationToken)
    {
        object result;
        switch (request.EffectiveFunction.Trim().ToLowerInvariant())
        {
            case "status":
                result = Status();
                break;
            case "consultar":
                result = await QueryTableAsync(request, request.GetString("tipo_banco"), cancellationToken);
                break;
            case "consultar_sqlserver":
                result = await QueryTableAsync(request, "sqlserver", cancellationToken);
                break;
            case "consultar_postgresql":
                result = await QueryTableAsync(request, "postgresql", cancellationToken);
                break;
            case "consultar_mariadb":
                result = await QueryTableAsync(request, "mariadb", cancellationToken);
                break;
            case "consultar_mysql":
                result = await QueryTableAsync(request, "mysql", cancellationToken);
                break;
            case "consultar_oracle":
                result = await QueryTableAsync(request, "oracle", cancellationToken);
                break;
            case "consultar_hfsql":
                result = await QueryTableAsync(request, "hfsql", cancellationToken);
                break;
            case "consultar_sqlite":
                // Mantém compatibilidade com clientes antigos, mas permite que a
                // chamada selecione outro SGBD apenas trocando tipo_banco.
                result = await QueryTableAsync(request, RequestedTypeOrDefault(request, "sqlite"), cancellationToken);
                break;
            case "criar_banco_sqlite":
                result = await databases.CreateSqliteDatabaseAsync(
                    RequestedDatabase(request),
                    cancellationToken);
                break;
            case "consulta_livre":
            case "alterar":
            case "inserir":
            case "excluir":
                result = await FreeSqlAsync(request, request.GetString("tipo_banco"), cancellationToken);
                break;
            case "consultar_multibanco":
                result = await StructuredCrudAsync(request, "select", cancellationToken);
                break;
            case "inserir_multibanco":
                result = await StructuredCrudAsync(request, "insert", cancellationToken);
                break;
            case "alterar_multibanco":
                result = await StructuredCrudAsync(request, "update", cancellationToken);
                break;
            case "excluir_multibanco":
                result = await StructuredCrudAsync(request, "delete", cancellationToken);
                break;
            case "consultar_sqlserver_livre":
                result = await FreeSqlAsync(request, "sqlserver", cancellationToken);
                break;
            case "consultar_postgresql_livre":
                result = await FreeSqlAsync(request, "postgresql", cancellationToken);
                break;
            case "consultar_mariadb_livre":
                result = await FreeSqlAsync(request, "mariadb", cancellationToken);
                break;
            case "consultar_mysql_livre":
                result = await FreeSqlAsync(request, "mysql", cancellationToken);
                break;
            case "consultar_oracle_livre":
                result = await FreeSqlAsync(request, "oracle", cancellationToken);
                break;
            case "consultar_sqlite_livre":
                // O sufixo histórico não limita mais o banco: tipo_banco tem prioridade.
                result = await FreeSqlAsync(request, RequestedTypeOrDefault(request, "sqlite"), cancellationToken);
                break;
            case "listar_tabelas":
                result = await ListTablesAsync(request, cancellationToken);
                break;
            case "vincular_credencial_externa":
                result = await databases.LinkExternalCredentialAsync(request, cancellationToken);
                break;
            case "listar_campos":
                result = await ListColumnsAsync(request, cancellationToken);
                break;
            case "arquivo_salvar":
                result = Success("resultado", await repository.SaveAsync(request.GetString("categoria"), request.GetString("desenvolvedor"), request.GetString("usuario"), request.GetString("nome_arquivo"), request.GetString("conteudo_base64"), cancellationToken));
                break;
            case "arquivo_ler":
                result = Success("resultado", await repository.ReadAsync(request.GetString("categoria"), request.GetString("desenvolvedor"), request.GetString("usuario"), request.GetString("nome_arquivo"), cancellationToken));
                break;
            case "arquivo_listar":
                result = Success("arquivos", repository.List(request.GetString("categoria"), request.GetString("desenvolvedor"), request.GetString("usuario")));
                break;
            case "arquivo_excluir":
                result = Success("resultado", repository.Delete(request.GetString("categoria"), request.GetString("desenvolvedor"), request.GetString("usuario"), request.GetString("nome_arquivo")));
                break;
            case "mapa_geocodificar":
                result = Success("dados", await integrations.GeocodeAsync(request.GetString("consulta"), cancellationToken));
                break;
            case "whatsapp_zapi_enviar":
                result = Success("dados", await integrations.SendZApiTextAsync(ZApiDestination(request), request.GetString("mensagem"), cancellationToken));
                break;
            case "whatsapp_enviar":
                if (request.GetString("provedor").Equals("zapi", StringComparison.OrdinalIgnoreCase) || request.GetString("provedor").Equals("z-api", StringComparison.OrdinalIgnoreCase))
                {
                    result = Success("dados", await integrations.SendZApiTextAsync(ZApiDestination(request), request.GetString("mensagem"), cancellationToken));
                    break;
                }
                goto default;
            case "consulta_via_ia":
            case "executar_via_ia":
                result = Success("dados", await integrations.AskOpenAiAsync(request.GetString("prompt"), request.GetBoolean("pesquisa_web"), request.GetBoolean("retorno_estruturado"), cancellationToken));
                break;
            case "cfg_ler":
                if (isLocal)
                {
                    result = new
                    {
                        ok = true,
                        ini = configuration.ReadRedacted()
                    };
                    break;
                }
                goto IL_123e;
            case "cfg_gravar":
                if (isLocal)
                {
                    result = await SaveConfigurationAsync(request.GetString("ini"), cancellationToken);
                    break;
                }
                goto IL_123e;
            case "whatsapp_meta_enviar":
                throw new NotSupportedException("Integração WhatsApp Meta removida; use Z-API.");
            case "ia_chat":
                throw new NotSupportedException("Integração Ollama removida.");
            case "inserir_hfsql":
                result = Success("dados", await databases.MaintainHfSqlAsync("inserir", RequestedDatabase(request), request.GetString("tabela"), request.GetString("campo_id"), request.GetString("campo_nome"), 0, request.GetString("nome"), cancellationToken));
                break;
            case "alterar_hfsql":
                result = Success("dados", await databases.MaintainHfSqlAsync("alterar", RequestedDatabase(request), request.GetString("tabela"), request.GetString("campo_id"), request.GetString("campo_nome"), request.GetInt("id"), request.GetString("nome"), cancellationToken));
                break;
            case "excluir_hfsql":
                result = Success("dados", await databases.MaintainHfSqlAsync("excluir", RequestedDatabase(request), request.GetString("tabela"), request.GetString("campo_id"), request.GetString("campo_nome"), request.GetInt("id"), string.Empty, cancellationToken));
                break;
            default:
                {
                    throw new KeyNotFoundException("funcao nao encontrada");
                }
            IL_123e:
                throw new UnauthorizedAccessException("configuração permitida somente no servidor local.");
        }
        return result;
    }

    private static string RequestedTypeOrDefault(ExecuteRequest request, string fallback)
    {
        string requested = request.GetString("tipo_banco").Trim();
        return requested.Length == 0 ? fallback : requested;
    }

    public object Status()
    {
        return new
        {
            ok = true,
            status = "online",
            instancia = configuration.Get("Instancia", "Nome", "FacilApp SQL"),
            porta = configuration.GetInt("ServidorHTTP", "Porta", 5050),
            portas = ParsePorts(configuration),
            aplicacao = "FacilApp SQL",
            versao = (typeof(LegacyDispatcher).Assembly.GetName().Version?.ToString() ?? "1.0.0")
        };
    }

    public static int[] ParsePorts(IniConfiguration configuration)
    {
        string text = configuration.Get("ServidorHTTP", "Portas", configuration.Get("ServidorHTTP", "Porta", "5050"));
        int[] array = (from value in text.Split(',', 59, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       select int.TryParse(value, out var result) ? result : 0 into port
                       where port >= 1 && port <= 65535
                       select port).Distinct().ToArray();
        if (array.Length == 0)
        {
            return new int[1] { 5050 };
        }
        return array;
    }

    private async Task<object> QueryTableAsync(ExecuteRequest request, string type, CancellationToken cancellationToken)
    {
        string item = DatabaseService.NormalizeDatabaseType(type).Type;
        return Success("dados", await databases.QueryTableAsync(item, request.GetString("servidor"), RequestedDatabase(request), request.GetString("tabela"), request.GetString("campos"), cancellationToken));
    }

    private async Task<object> FreeSqlAsync(ExecuteRequest request, string type, CancellationToken cancellationToken)
    {
        string item = DatabaseService.NormalizeDatabaseType(type).Type;
        return Success("dados", await databases.ExecuteFreeSqlAsync(item, request.GetString("servidor"), RequestedDatabase(request), request.GetString("sql"), cancellationToken));
    }

    private async Task<object> StructuredCrudAsync(ExecuteRequest request, string operation, CancellationToken cancellationToken)
    {
        string type = RequestedTypeOrDefault(request, "sqlite");
        IReadOnlyDictionary<string, object?> data = ReadObject(request, "dados");
        IReadOnlyDictionary<string, object?> where = ReadObject(request, "where");
        IReadOnlyList<string> fields = request.GetString("campos")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        object value = await databases.ExecuteStructuredCrudAsync(
            operation, type, request.GetString("servidor"), RequestedDatabase(request),
            request.GetString("tabela"), data, where, fields, cancellationToken);
        return Success("dados", value);
    }

    private static IReadOnlyDictionary<string, object?> ReadObject(ExecuteRequest request, string name)
    {
        if (!request.Arguments.TryGetValue(name, out JsonElement element) || element.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        return element.EnumerateObject().ToDictionary(item => item.Name, item => JsonValue(item.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static object? JsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetInt64(out long integer) => integer,
        JsonValueKind.Number when element.TryGetDecimal(out decimal number) => number,
        JsonValueKind.String => element.GetString(),
        _ => element.GetRawText()
    };

    private async Task<object> ListTablesAsync(ExecuteRequest request, CancellationToken cancellationToken)
    {
        string item = DatabaseService.NormalizeDatabaseType(request.GetString("tipo_banco")).Type;
        return Success("dados", await databases.ListTablesAsync(item, request.GetString("servidor"), RequestedDatabase(request), cancellationToken));
    }

    private async Task<object> ListColumnsAsync(ExecuteRequest request, CancellationToken cancellationToken)
    {
        string item = DatabaseService.NormalizeDatabaseType(request.GetString("tipo_banco")).Type;
        return Success("dados", await databases.ListColumnsAsync(item, request.GetString("servidor"), RequestedDatabase(request), request.GetString("esquema"), request.GetString("tabela"), cancellationToken));
    }

    private static string RequestedDatabase(ExecuteRequest request)
    {
        return request.GetString("banco").Trim();
    }

    private async Task<object> SaveConfigurationAsync(string content, CancellationToken cancellationToken)
    {
        await configuration.ReplaceRedactedAsync(content, cancellationToken);
        return new
        {
            ok = true,
            reinicio_necessario = true
        };
    }

    private string ZApiDestination(ExecuteRequest request)
    {
        string text = request.GetString("telefone");
        if (text.Length <= 0)
        {
            return configuration.Get("WHATSAPP_ZAPI", "GRUPO_PADRAO");
        }
        return text;
    }

    private static object Success(string name, object value)
    {
        if (!(name == "dados"))
        {
            if (name == "arquivos")
            {
                return new
                {
                    ok = true,
                    arquivos = value
                };
            }
            return new
            {
                ok = true,
                resultado = value
            };
        }
        return new
        {
            ok = true,
            dados = value
        };
    }
}
