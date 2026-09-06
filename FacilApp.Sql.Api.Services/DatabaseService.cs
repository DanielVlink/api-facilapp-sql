using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;
using FacilApp.Sql.Api.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Executa operações isoladas nos bancos suportados pela API FacilApp SQL.
/// Cada chamada cria e libera sua própria conexão para preservar a segurança entre requisições.
/// </summary>
public sealed class DatabaseService
{
    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_$#]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> SqliteExtensions = new(
        [".db", ".sqlite", ".sqlite3"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IniConfiguration configuration;

    /// <summary>Inicializa o serviço com a configuração da instalação.</summary>
    public DatabaseService(IniConfiguration configuration)
    {
        this.configuration = configuration;
    }

    /// <summary>Abre e fecha uma conexão para validar a configuração salva.</summary>
    public async Task TestConnectionAsync(string databaseType, CancellationToken cancellationToken)
    {
        await using DbConnection connection = CreateConnection(databaseType, string.Empty, string.Empty);
        await connection.OpenAsync(cancellationToken);
    }

    /// <summary>
    /// Cria um arquivo SQLite vazio dentro do diretório autorizado pelo INI.
    /// </summary>
    /// <param name="requestedDatabase">Nome do arquivo SQLite, por exemplo <c>curso.db</c>.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <returns>Identificação do arquivo criado ou já existente, sem expor caminhos locais.</returns>
    /// <exception cref="InvalidDataException">Lançada quando o nome ou a extensão do arquivo não forem válidos.</exception>
    /// <exception cref="UnauthorizedAccessException">Lançada quando o nome tentar sair do diretório SQLite autorizado.</exception>
    public async Task<object> CreateSqliteDatabaseAsync(
        string requestedDatabase,
        CancellationToken cancellationToken)
    {
        string databasePath = ResolveSqliteDatabasePath("SQLite", requestedDatabase.Trim());
        bool alreadyExisted = File.Exists(databasePath);

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = configuration.GetInt("ServidorHTTP", "TimeoutSegundos", 30)
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        return new
        {
            ok = true,
            banco = Path.GetFileName(databasePath),
            criado = !alreadyExisted,
            diretorio = configuration.Get("SQLite", "Diretorio", "BasesSQLite")
        };
    }

    /// <summary>
    /// Aplica os três catálogos de menu aos usuários ativos de um banco
    /// SQLite autorizado e cria uma cópia de segurança antes da alteração.
    /// </summary>
    /// <param name="requestedDatabase">Nome do arquivo SQLite dentro do diretório autorizado.</param>
    /// <param name="menuData">Lista JSON do menu lateral.</param>
    /// <param name="menuSuperiorData">Lista JSON do menu superior.</param>
    /// <param name="dashBoardData">Lista JSON das dashboards.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <returns>Quantidade de usuários atualizados e identificação segura do backup.</returns>
    /// <exception cref="FileNotFoundException">Lançada quando o banco informado não existe.</exception>
    /// <exception cref="InvalidDataException">Lançada quando o JSON ou a estrutura do banco são inválidos.</exception>
    public async Task<object> ApplySqliteMenuProfileAsync(
        string requestedDatabase,
        JsonElement menuData,
        JsonElement menuSuperiorData,
        JsonElement dashBoardData,
        CancellationToken cancellationToken)
    {
        string lateralJson = NormalizeMenuArray(menuData, "MenuData");
        string superiorJson = NormalizeMenuArray(menuSuperiorData, "MenuSuperiorData");
        string dashboardsJson = NormalizeMenuArray(dashBoardData, "DashBoardData");
        string databasePath = ResolveSqliteDatabasePath("SQLite", requestedDatabase.Trim());

        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                $"Banco SQLite '{Path.GetFileName(databasePath)}' não encontrado no diretório autorizado.");
        }

        // O backup é criado antes de abrir a transação. Assim uma falha de
        // schema, bloqueio ou gravação nunca remove o estado anterior do banco.
        string backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "BKP");
        Directory.CreateDirectory(backupDirectory);
        string backupName = string.Concat(
            Path.GetFileNameWithoutExtension(databasePath),
            "_menus_",
            DateTime.Now.ToString("ddMMyy_HHmmss"),
            Path.GetExtension(databasePath));
        File.Copy(databasePath, Path.Combine(backupDirectory, backupName), overwrite: false);

        await using SqliteConnection connection = (SqliteConnection)CreateSqliteConnection(
            "SQLite",
            requestedDatabase);
        await connection.OpenAsync(cancellationToken);

        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        await using (SqliteCommand schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = "PRAGMA table_info(\"USUARIOS\")";
            await using SqliteDataReader reader = await schemaCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(reader.GetOrdinal("name")));
            }
        }

        string[] requiredColumns =
        [
            "MenuData",
            "MenuSuperiorData",
            "DashBoardData",
            "FLAG_ATIVO"
        ];
        string[] missingColumns = requiredColumns.Where(column => !columns.Contains(column)).ToArray();
        if (missingColumns.Length > 0)
        {
            throw new InvalidDataException(
                "A tabela USUARIOS não possui os campos obrigatórios: " +
                string.Join(", ", missingColumns));
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = """
            UPDATE "USUARIOS"
               SET "MenuData" = $menuData,
                   "MenuSuperiorData" = $menuSuperiorData,
                   "DashBoardData" = $dashBoardData
             WHERE COALESCE("FLAG_ATIVO", 0) = 1
            """;
        updateCommand.Parameters.AddWithValue("$menuData", lateralJson);
        updateCommand.Parameters.AddWithValue("$menuSuperiorData", superiorJson);
        updateCommand.Parameters.AddWithValue("$dashBoardData", dashboardsJson);
        int updatedUsers = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new
        {
            ok = true,
            banco = Path.GetFileName(databasePath),
            usuariosAtualizados = updatedUsers,
            backup = Path.Combine("BKP", backupName).Replace('\\', '/'),
            itens = new
            {
                lateral = menuData.GetArrayLength(),
                superior = menuSuperiorData.GetArrayLength(),
                dashboards = dashBoardData.GetArrayLength()
            }
        };
    }

    /// <summary>Consulta os campos solicitados da tabela no banco informado pela chamada.</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> QueryTableAsync(
        string databaseType,
        string requestedServer,
        string requestedDatabase,
        string table,
        string fields,
        CancellationToken cancellationToken)
    {
        string normalizedType = NormalizeDatabaseType(databaseType).Type;
        string safeTable = ValidateQualifiedIdentifier(table);
        string safeFields = BuildFieldList(fields);
        string quotedTable = QuoteQualifiedIdentifier(normalizedType, safeTable);
        string selectedFields = safeFields == "*"
            ? "*"
            : string.Join(", ", safeFields.Split(',').Select(field =>
                QuoteIdentifier(normalizedType, field.Trim())));
        string sql = $"SELECT {selectedFields} FROM {quotedTable}";

        try
        {
            return await ExecuteQueryAsync(
                normalizedType,
                requestedServer,
                requestedDatabase,
                sql,
                cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            throw new InvalidDataException(
                $"Tabela '{safeTable}' não encontrada no banco SQL Server.",
                exception);
        }
        catch (SqlException exception) when (exception.Number == 207)
        {
            throw new InvalidDataException(
                $"Um ou mais campos não existem na tabela '{safeTable}'.",
                exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            throw new InvalidDataException(
                $"Tabela ou campo inválido no arquivo SQLite: {exception.Message}",
                exception);
        }
    }

    /// <summary>Executa o SQL recebido no banco informado pela chamada.</summary>
    public async Task<object> ExecuteFreeSqlAsync(
        string databaseType,
        string requestedServer,
        string requestedDatabase,
        string sql,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidDataException("SQL não informado.");
        }

        await using DbConnection connection = CreateConnection(
            databaseType,
            requestedServer,
            requestedDatabase);
        await connection.OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;

        if (LooksLikeQuery(sql))
        {
            return await ReadRowsAsync(command, cancellationToken);
        }

        return new
        {
            linhas_afetadas = await command.ExecuteNonQueryAsync(cancellationToken)
        };
    }

    public async Task<object> ExecuteStructuredCrudAsync(
        string operation,
        string databaseType,
        string requestedServer,
        string requestedDatabase,
        string table,
        IReadOnlyDictionary<string, object?> data,
        IReadOnlyDictionary<string, object?> where,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken)
    {
        string type = NormalizeDatabaseType(databaseType).Type;
        string safeTable = QuoteQualifiedIdentifier(type, ValidateQualifiedIdentifier(table));
        operation = operation.Trim().ToLowerInvariant();
        if ((operation == "update" || operation == "delete") && where.Count == 0)
            throw new InvalidDataException("where é obrigatório para alterar ou excluir.");

        await using DbConnection connection = CreateConnection(type, requestedServer, requestedDatabase);
        await connection.OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        int parameterIndex = 0;
        string AddValue(object? value)
        {
            string name = $"p{parameterIndex++}";
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
            return type == "hfsql" ? "?" : type == "oracle" ? $":{name}" : $"@{name}";
        }

        string WhereSql() => " WHERE " + string.Join(" AND ", where.Select(item =>
            $"{QuoteIdentifier(type, ValidateIdentifier(item.Key))} = {AddValue(item.Value)}"));

        switch (operation)
        {
            case "select":
                string projection = fields.Count == 0
                    ? "*"
                    : string.Join(", ", fields.Select(field => QuoteIdentifier(type, ValidateIdentifier(field))));
                command.CommandText = $"SELECT {projection} FROM {safeTable}" + (where.Count == 0 ? string.Empty : WhereSql());
                return await ReadRowsAsync(command, cancellationToken);
            case "insert":
                if (data.Count == 0) throw new InvalidDataException("dados é obrigatório para inserir.");
                command.CommandText = $"INSERT INTO {safeTable} ({string.Join(", ", data.Keys.Select(key => QuoteIdentifier(type, ValidateIdentifier(key))))}) VALUES ({string.Join(", ", data.Values.Select(AddValue))})";
                break;
            case "update":
                if (data.Count == 0) throw new InvalidDataException("dados é obrigatório para alterar.");
                command.CommandText = $"UPDATE {safeTable} SET {string.Join(", ", data.Select(item => $"{QuoteIdentifier(type, ValidateIdentifier(item.Key))} = {AddValue(item.Value)}"))}{WhereSql()}";
                break;
            case "delete":
                command.CommandText = $"DELETE FROM {safeTable}{WhereSql()}";
                break;
            default:
                throw new InvalidDataException("operação multibanco inválida.");
        }

        return new { linhas_afetadas = await command.ExecuteNonQueryAsync(cancellationToken) };
    }

    public async Task<object> EnsureStructureAsync(
        string databaseType, string requestedServer, string requestedDatabase,
        string table, IReadOnlyList<MultibankColumnDefinition> columns,
        CancellationToken cancellationToken)
    {
        string type = NormalizeDatabaseType(databaseType).Type;
        string validatedTable = ValidateQualifiedIdentifier(table);
        if (columns.Count == 0) throw new InvalidDataException("campos é obrigatório.");
        foreach (MultibankColumnDefinition column in columns) ValidateIdentifier(column.Name);
        if (columns.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count)
            throw new InvalidDataException("Existem campos repetidos.");

        string[] tableParts = validatedTable.Split('.');
        string schema = tableParts.Length == 2 ? tableParts[0] : string.Empty;
        string tableName = tableParts[^1];
        IReadOnlyList<Dictionary<string, object?>> tables = await ListTablesAsync(type, requestedServer, requestedDatabase, cancellationToken);
        bool exists = tables.Any(row => string.Equals(Convert.ToString(row.GetValueOrDefault("tabela")), tableName, StringComparison.OrdinalIgnoreCase));
        var added = new List<string>();
        var changed = new List<string>();

        if (!exists)
        {
            string definitions = string.Join(", ", columns.Select(c => BuildColumnDefinition(type, c, true)));
            await ExecuteFreeSqlAsync(type, requestedServer, requestedDatabase,
                $"CREATE TABLE {QuoteQualifiedIdentifier(type, validatedTable)} ({definitions})", cancellationToken);
            return new { ok = true, tabela = tableName, criada = true, campos_adicionados = columns.Select(c => c.Name), campos_alterados = changed };
        }

        IReadOnlyList<Dictionary<string, object?>> current = await ListColumnsAsync(type, requestedServer, requestedDatabase, schema, tableName, cancellationToken);
        Dictionary<string, Dictionary<string, object?>> currentByName = current.ToDictionary(
            row => Convert.ToString(row.GetValueOrDefault("campo")) ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
        foreach (MultibankColumnDefinition column in columns)
        {
            if (!currentByName.TryGetValue(column.Name, out Dictionary<string, object?>? currentColumn))
            {
                string add = type is "sqlserver" or "oracle" or "hfsql" ? "ADD" : "ADD COLUMN";
                await ExecuteFreeSqlAsync(type, requestedServer, requestedDatabase,
                    $"ALTER TABLE {QuoteQualifiedIdentifier(type, validatedTable)} {add} {BuildColumnDefinition(type, column, false)}", cancellationToken);
                added.Add(column.Name);
                continue;
            }

            if (ColumnMatches(type, column, currentColumn)) continue;
            if (type == "sqlite")
                throw new InvalidOperationException($"O campo {column.Name} possui tipo/tamanho diferente. SQLite exige reconstrução transacional da tabela; a alteração foi cancelada para não perder dados.");

            string definition = BuildColumnDefinition(type, column, false);
            string alterSql = type switch
            {
                "postgresql" => $"ALTER TABLE {QuoteQualifiedIdentifier(type, validatedTable)} ALTER COLUMN {QuoteIdentifier(type, column.Name)} TYPE {BuildSqlType(type, column)} USING {QuoteIdentifier(type, column.Name)}::{BuildSqlType(type, column)}",
                "sqlserver" => $"ALTER TABLE {QuoteQualifiedIdentifier(type, validatedTable)} ALTER COLUMN {definition}",
                "mysql" or "mariadb" => $"ALTER TABLE {QuoteQualifiedIdentifier(type, validatedTable)} MODIFY COLUMN {definition}",
                "oracle" => $"ALTER TABLE {QuoteQualifiedIdentifier(type, validatedTable)} MODIFY ({definition})",
                "hfsql" => $"ALTER TABLE {QuoteQualifiedIdentifier(type, validatedTable)} MODIFY COLUMN {definition}",
                _ => throw new NotSupportedException("Alteração de estrutura não suportada para este banco.")
            };
            await ExecuteFreeSqlAsync(type, requestedServer, requestedDatabase, alterSql, cancellationToken);
            changed.Add(column.Name);
        }

        return new { ok = true, tabela = tableName, criada = false, campos_adicionados = added, campos_alterados = changed };
    }

    /// <summary>
    /// Replica a estrutura atual do SQLite cadastral da própria instalação para
    /// o banco destino. Não contém uma lista estática: alterações futuras na
    /// estrutura da API passam a ser usadas automaticamente na próxima chamada.
    /// </summary>
    public async Task<object> EnsureInstalledApiStructureAsync(
        string targetType,
        string targetServer,
        string targetDatabase,
        IReadOnlyList<string> requestedTables,
        CancellationToken cancellationToken)
    {
        string sourcePath = Path.Combine(configuration.DataDirectory, "Dados", "facilapp_sql.db");
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("SQLite cadastral da API não encontrado.", sourcePath);
        }

        HashSet<string>? selected = requestedTables.Count == 0
            ? null
            : new HashSet<string>(requestedTables.Select(ValidateIdentifier), StringComparer.OrdinalIgnoreCase);
        var results = new List<object>();
        await using SqliteConnection source = new(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await source.OpenAsync(cancellationToken);
        await using SqliteCommand tablesCommand = source.CreateCommand();
        tablesCommand.CommandText = "SELECT name, sql FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using SqliteDataReader tablesReader = await tablesCommand.ExecuteReaderAsync(cancellationToken);
        var tables = new List<(string Name, string Definition)>();
        while (await tablesReader.ReadAsync(cancellationToken))
        {
            string name = tablesReader.GetString(0);
            if (selected is null || selected.Contains(name)) tables.Add((name, tablesReader.IsDBNull(1) ? string.Empty : tablesReader.GetString(1)));
        }
        if (selected is not null && tables.Count != selected.Count)
        {
            string[] missing = selected.Where(item => !tables.Any(table => string.Equals(table.Name, item, StringComparison.OrdinalIgnoreCase))).ToArray();
            throw new InvalidDataException($"Tabela(s) de referência não encontrada(s): {string.Join(", ", missing)}.");
        }

        foreach ((string name, string definition) in tables)
        {
            var columns = new List<MultibankColumnDefinition>();
            await using SqliteCommand fieldsCommand = source.CreateCommand();
            fieldsCommand.CommandText = $"PRAGMA table_info({QuoteIdentifier("sqlite", name)});";
            await using SqliteDataReader fieldsReader = await fieldsCommand.ExecuteReaderAsync(cancellationToken);
            while (await fieldsReader.ReadAsync(cancellationToken))
            {
                string fieldName = fieldsReader.GetString(1);
                string sqliteType = fieldsReader.IsDBNull(2) ? string.Empty : fieldsReader.GetString(2);
                bool primaryKey = fieldsReader.GetInt64(5) > 0;
                columns.Add(new MultibankColumnDefinition
                {
                    Name = fieldName,
                    Type = MapSqliteTypeToGeneric(sqliteType),
                    Length = ExtractSqliteLength(sqliteType),
                    Nullable = fieldsReader.GetInt64(3) == 0,
                    PrimaryKey = primaryKey,
                    AutoIncrement = primaryKey && definition.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase)
                });
            }
            results.Add(await EnsureStructureAsync(targetType, targetServer, targetDatabase, name, columns, cancellationToken));
        }

        return new { ok = true, origem = "Dados/facilapp_sql.db", tabelas_processadas = tables.Count, resultado = results };
    }

    private static string MapSqliteTypeToGeneric(string sqliteType)
    {
        string type = sqliteType.Trim().ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal)) return "inteiro";
        if (type.Contains("BOOL", StringComparison.Ordinal)) return "booleano";
        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal) || type.Contains("DOUB", StringComparison.Ordinal) || type.Contains("DEC", StringComparison.Ordinal) || type.Contains("NUM", StringComparison.Ordinal)) return "numero";
        if (type.Contains("BLOB", StringComparison.Ordinal)) return "binario";
        if (type.Contains("DATE", StringComparison.Ordinal) || type.Contains("TIME", StringComparison.Ordinal)) return "datahora";
        if (type.Contains("CHAR", StringComparison.Ordinal) || type.Contains("VARCHAR", StringComparison.Ordinal)) return "texto_curto";
        return "texto";
    }

    private static int? ExtractSqliteLength(string sqliteType)
    {
        Match match = Regex.Match(sqliteType, @"\((?<length>\d+)\)", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["length"].Value, out int length) ? length : null;
    }

    private static bool ColumnMatches(string type, MultibankColumnDefinition desired, IReadOnlyDictionary<string, object?> current)
    {
        string actualType = (Convert.ToString(current.GetValueOrDefault("tipo")) ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        string wantedType = BuildSqlType(type, desired).Replace(" ", string.Empty).ToUpperInvariant();
        object? actualLength = current.GetValueOrDefault("tamanho");
        bool typeMatches = actualType == wantedType || actualType.StartsWith(wantedType + "(", StringComparison.Ordinal);
        bool lengthMatches = desired.Length is null || actualLength is null || Convert.ToInt32(actualLength) == desired.Length.Value;
        return typeMatches && lengthMatches;
    }

    private static string BuildColumnDefinition(string type, MultibankColumnDefinition column, bool allowIdentity)
    {
        bool identity = allowIdentity && column.AutoIncrement;
        string sqlType = identity ? type switch
        {
            "sqlite" => "INTEGER PRIMARY KEY AUTOINCREMENT",
            "postgresql" => "BIGINT GENERATED BY DEFAULT AS IDENTITY",
            "sqlserver" => "BIGINT IDENTITY(1,1)",
            "mysql" or "mariadb" or "hfsql" => "BIGINT AUTO_INCREMENT",
            "oracle" => "NUMBER(19) GENERATED BY DEFAULT AS IDENTITY",
            _ => BuildSqlType(type, column)
        } : BuildSqlType(type, column);
        string primary = column.PrimaryKey && !(type == "sqlite" && identity) ? " PRIMARY KEY" : string.Empty;
        string nullable = !column.Nullable && !column.PrimaryKey ? " NOT NULL" : string.Empty;
        return $"{QuoteIdentifier(type, column.Name)} {sqlType}{primary}{nullable}";
    }

    private static string BuildSqlType(string type, MultibankColumnDefinition column)
    {
        int length = Math.Clamp(column.Length ?? 255, 1, 65535);
        return column.Type.Trim().ToLowerInvariant() switch
        {
            "int" or "inteiro" => type == "oracle" ? "NUMBER(10)" : "INTEGER",
            "bigint" or "inteiro_longo" => type == "oracle" ? "NUMBER(19)" : "BIGINT",
            "decimal" or "numero" => type == "oracle" ? "NUMBER(18,2)" : "DECIMAL(18,2)",
            "bool" or "booleano" => type switch { "oracle" => "NUMBER(1)", "sqlserver" => "BIT", "sqlite" => "INTEGER", _ => "BOOLEAN" },
            "data" or "date" => "DATE",
            "datahora" or "datetime" => type switch { "postgresql" or "oracle" => "TIMESTAMP", "sqlite" => "TEXT", _ => "DATETIME" },
            "blob" or "binario" => type switch { "postgresql" => "BYTEA", "sqlserver" => "VARBINARY(MAX)", "oracle" => "BLOB", _ => "BLOB" },
            "uuid" => type switch { "postgresql" => "UUID", "sqlserver" => "UNIQUEIDENTIFIER", "oracle" => "VARCHAR2(36)", _ => "VARCHAR(36)" },
            "varchar" or "texto_curto" => type == "oracle" ? $"VARCHAR2({length})" : $"VARCHAR({length})",
            "text" or "texto" => type switch { "sqlserver" => "NVARCHAR(MAX)", "oracle" => "CLOB", _ => "TEXT" },
            _ => throw new InvalidDataException($"Tipo genérico não suportado: {column.Type}.")
        };
    }

    /// <summary>Executa as operações legadas específicas do HFSQL.</summary>
    public async Task<object> MaintainHfSqlAsync(
        string operation,
        string requestedDatabase,
        string table,
        string idField,
        string nameField,
        int id,
        string name,
        CancellationToken cancellationToken)
    {
        string safeTable = QuoteQualifiedIdentifier("hfsql", ValidateQualifiedIdentifier(table));
        string safeId = QuoteIdentifier("hfsql", ValidateIdentifier(idField));
        string safeName = QuoteIdentifier("hfsql", ValidateIdentifier(nameField));

        await using DbConnection connection = CreateConnection("hfsql", string.Empty, requestedDatabase);
        await connection.OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = operation switch
        {
            "inserir" => $"INSERT INTO {safeTable} ({safeName}) VALUES (?)",
            "alterar" => $"UPDATE {safeTable} SET {safeName}=? WHERE {safeId}=?",
            "excluir" => $"DELETE FROM {safeTable} WHERE {safeId}=?",
            _ => throw new InvalidDataException("operação HFSQL inválida.")
        };

        if (operation is "inserir" or "alterar")
        {
            AddParameter(command, name);
        }

        if (operation is "alterar" or "excluir")
        {
            AddParameter(command, id);
        }

        return new
        {
            linhas_afetadas = await command.ExecuteNonQueryAsync(cancellationToken)
        };
    }

    /// <summary>Lista tabelas e visões do banco solicitado.</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListTablesAsync(
        string databaseType,
        string requestedServer,
        string requestedDatabase,
        CancellationToken cancellationToken)
    {
        string normalizedType = NormalizeDatabaseType(databaseType).Type;
        await using DbConnection connection = CreateConnection(
            normalizedType,
            requestedServer,
            requestedDatabase);
        await connection.OpenAsync(cancellationToken);

        if (normalizedType == "sqlite")
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT '' AS esquema, name AS tabela, type AS tipo
                FROM sqlite_schema
                WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;
            return await ReadRowsAsync(command, cancellationToken);
        }

        DataTable schema = connection.GetSchema("Tables");
        return schema.Rows.Cast<DataRow>()
            .Where(row => !string.Equals(
                row["TABLE_TYPE"]?.ToString(),
                "SYSTEM TABLE",
                StringComparison.OrdinalIgnoreCase))
            .Select(row => new Dictionary<string, object?>
            {
                ["esquema"] = schema.Columns.Contains("TABLE_SCHEMA")
                    ? row["TABLE_SCHEMA"]?.ToString()
                    : string.Empty,
                ["tabela"] = row["TABLE_NAME"]?.ToString(),
                ["tipo"] = row["TABLE_TYPE"]?.ToString()
            })
            .ToList();
    }

    /// <summary>Lista campos e tipos de uma tabela do banco solicitado.</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListColumnsAsync(
        string databaseType,
        string requestedServer,
        string requestedDatabase,
        string schemaName,
        string table,
        CancellationToken cancellationToken)
    {
        string normalizedType = NormalizeDatabaseType(databaseType).Type;
        string safeTable = ValidateIdentifier(table);
        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            ValidateIdentifier(schemaName);
        }

        await using DbConnection connection = CreateConnection(
            normalizedType,
            requestedServer,
            requestedDatabase);
        await connection.OpenAsync(cancellationToken);

        if (normalizedType == "sqlite")
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier("sqlite", safeTable)});";
            IReadOnlyList<Dictionary<string, object?>> rows = await ReadRowsAsync(
                command,
                cancellationToken);
            return rows.Select(row => new Dictionary<string, object?>
            {
                ["campo"] = row.GetValueOrDefault("name"),
                ["tipo"] = row.GetValueOrDefault("type"),
                ["tamanho"] = null,
                ["nulo"] = Convert.ToInt64(row.GetValueOrDefault("notnull") ?? 0) == 0
                    ? "YES"
                    : "NO",
                ["chave_primaria"] = Convert.ToInt64(row.GetValueOrDefault("pk") ?? 0) > 0
            }).ToList();
        }

        string?[] restrictions =
        [
            null,
            string.IsNullOrWhiteSpace(schemaName) ? null : schemaName,
            safeTable,
            null
        ];
        DataTable columns = connection.GetSchema("Columns", restrictions);
        return columns.Rows.Cast<DataRow>()
            .Select(row => new Dictionary<string, object?>
            {
                ["campo"] = row["COLUMN_NAME"]?.ToString(),
                ["tipo"] = row["DATA_TYPE"]?.ToString(),
                ["tamanho"] = columns.Columns.Contains("CHARACTER_MAXIMUM_LENGTH")
                    ? NormalizeDbValue(row["CHARACTER_MAXIMUM_LENGTH"])
                    : null,
                ["nulo"] = columns.Columns.Contains("IS_NULLABLE")
                    ? row["IS_NULLABLE"]?.ToString()
                    : null
            })
            .ToList();
    }

    /// <summary>
    /// Valida usuário e senha na tabela externa do VLINK.
    /// </summary>
    /// <remarks>
    /// Servidor, porta e credenciais técnicas vêm exclusivamente do INI. A chamada escolhe
    /// apenas o banco e o mapeamento da tabela de usuários, todos validados como identificadores
    /// SQL. A credencial OAuth não é procurada no banco VLINK: ela fica exclusivamente no INI.
    /// </remarks>
    /// <param name="request">Mapeamento da tabela externa e credenciais informadas no login.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <returns><see langword="true"/> quando existe uma única correspondência; caso contrário, <see langword="false"/>.</returns>
    public async Task<bool> ValidateExternalUserAsync(
        ExecuteRequest request,
        CancellationToken cancellationToken)
    {
        string type = NormalizeDatabaseType(request.GetString("tipo_banco")).Type;
        string database = request.GetString("banco").Trim();
        string userTable = QuoteQualifiedIdentifier(
            type,
            ValidateQualifiedIdentifier(request.GetString("tabela_usuario")));
        string userField = QuoteIdentifier(type, ValidateIdentifier(request.GetString("campo_usuario")));
        string passwordField = QuoteIdentifier(type, ValidateIdentifier(request.GetString("campo_senha")));
        string userName = request.GetString("usuario");
        string password = request.GetString("senha");
        if (database.Length == 0 || userName.Length == 0 || password.Length == 0)
        {
            throw new InvalidDataException("Banco, usuário e senha são obrigatórios.");
        }

        string userParameter = type switch
        {
            "oracle" => ":p_usuario",
            "hfsql" => "?",
            _ => "@p_usuario"
        };
        string passwordParameter = type switch
        {
            "oracle" => ":p_senha",
            "hfsql" => "?",
            _ => "@p_senha"
        };
        string selectLimit = type == "sqlserver" ? "TOP (1) " : string.Empty;
        string trailingLimit = type == "sqlserver"
            ? string.Empty
            : type == "oracle"
                ? " FETCH FIRST 1 ROWS ONLY"
                : " LIMIT 1";

        await using DbConnection connection = CreateConnection(type, string.Empty, database);
        await connection.OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = $"""
            SELECT {selectLimit}1
            FROM {userTable} u
            WHERE u.{userField}={userParameter}
              AND u.{passwordField}={passwordParameter}{trailingLimit}
            """;
        AddNamedParameter(command, type, "p_usuario", userName);
        AddNamedParameter(command, type, "p_senha", password);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    /// <summary>
    /// Grava (INSERT/UPDATE já existente, via UPDATE) o Client ID / Client Secret de uma
    /// credencial OAuth dentro de uma tabela do banco externo do cliente, localizando a
    /// linha pelo mesmo campo de vínculo usado no login (ex.: LoginVlink usa
    /// usuarios.ID_Filial = Filial.ID_FILIAL).
    /// </summary>
    /// <remarks>
    /// Este método NUNCA lê nem tenta recuperar um client_secret já existente — os segredos
    /// das credenciais locais ficam salvos apenas como hash (ver ConsoleStore), então não há
    /// como "copiar" um segredo já criado. O client_id/client_secret a gravar devem vir
    /// explícitos na requisição: normalmente são colados logo depois de criar a credencial em
    /// "Credenciais de API", momento em que o valor em texto puro é exibido uma única vez.
    /// Servidor, porta e credenciais técnicas de conexão vêm exclusivamente do INI; nomes de
    /// tabela/campo são validados como identificadores SQL antes de compor o comando, e os
    /// valores gravados são sempre enviados como parâmetro, nunca concatenados ao SQL.
    /// </remarks>
    /// <param name="request">Mapeamento genérico de tabela/campos e os valores a gravar.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <returns>Quantidade de linhas afetadas pela atualização.</returns>
    public async Task<object> LinkExternalCredentialAsync(
        ExecuteRequest request,
        CancellationToken cancellationToken)
    {
        string type = NormalizeDatabaseType(request.GetString("tipo_banco")).Type;
        string database = request.GetString("banco").Trim();
        string credentialTable = QuoteQualifiedIdentifier(
            type,
            ValidateQualifiedIdentifier(request.GetString("tabela_credencial")));
        string linkField = QuoteIdentifier(
            type,
            ValidateIdentifier(request.GetString("campo_vinculo_credencial")));
        string clientIdField = QuoteIdentifier(
            type,
            ValidateIdentifier(request.GetString("campo_client_id")));
        string clientSecretField = QuoteIdentifier(
            type,
            ValidateIdentifier(request.GetString("campo_client_secret")));
        string linkValue = request.GetString("valor_vinculo");
        string clientId = request.GetString("client_id");
        string clientSecret = request.GetString("client_secret");

        if (database.Length == 0 || linkValue.Length == 0 || clientId.Length == 0 || clientSecret.Length == 0)
        {
            throw new InvalidDataException(
                "Banco, valor de vínculo, client_id e client_secret são obrigatórios.");
        }

        string clientIdParameter = type switch
        {
            "oracle" => ":p_client_id",
            "hfsql" => "?",
            _ => "@p_client_id"
        };
        string clientSecretParameter = type switch
        {
            "oracle" => ":p_client_secret",
            "hfsql" => "?",
            _ => "@p_client_secret"
        };
        string linkParameter = type switch
        {
            "oracle" => ":p_vinculo",
            "hfsql" => "?",
            _ => "@p_vinculo"
        };

        await using DbConnection connection = CreateConnection(type, string.Empty, database);
        await connection.OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandTimeout = configuration.GetInt("ServidorHTTP", "TimeoutSegundos", 30);
        command.CommandText = $"""
            UPDATE {credentialTable}
               SET {clientIdField} = {clientIdParameter},
                   {clientSecretField} = {clientSecretParameter}
             WHERE {linkField} = {linkParameter}
            """;
        // Ordem dos parâmetros deve bater com a ordem dos "?" no texto acima (necessário
        // para provedores com parâmetros posicionais, como HFSQL).
        AddNamedParameter(command, type, "p_client_id", clientId);
        AddNamedParameter(command, type, "p_client_secret", clientSecret);
        AddNamedParameter(command, type, "p_vinculo", linkValue);

        int rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0)
        {
            throw new InvalidDataException(
                $"Nenhum registro encontrado em {request.GetString("tabela_credencial")} "
                    + $"com {request.GetString("campo_vinculo_credencial")} = {linkValue}.");
        }

        return new { ok = true, linhas_afetadas = rows };
    }

    /// <summary>Normaliza aliases legados e retorna a seção correspondente no INI.</summary>
    public static (string Type, string Section) NormalizeDatabaseType(string databaseType)
    {
        return databaseType.Trim().ToLowerInvariant() switch
        {
            "" or "sql" or "sqlserver" => ("sqlserver", "SQL"),
            "postgres" or "postgresql" => ("postgresql", "PostgreSQL"),
            "mariadb" => ("mariadb", "MariaDB"),
            "mysql" => ("mysql", "MySQL"),
            "oracle" => ("oracle", "Oracle"),
            "hfsql" => ("hfsql", "HFSQL"),
            "sqlite" or "sqlite3" => ("sqlite", "SQLite"),
            _ => throw new NotSupportedException("tipo_banco não suportado.")
        };
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        string databaseType,
        string requestedServer,
        string requestedDatabase,
        string sql,
        CancellationToken cancellationToken)
    {
        await using DbConnection connection = CreateConnection(
            databaseType,
            requestedServer,
            requestedDatabase);
        await connection.OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = configuration.GetInt("ServidorHTTP", "TimeoutSegundos", 30);
        return await ReadRowsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadRowsAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = NormalizeDbValue(reader.GetValue(index));
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Valida e compacta uma lista JSON de menu sem alterar seus textos Unicode.</summary>
    private static string NormalizeMenuArray(JsonElement source, string fieldName)
    {
        if (source.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{fieldName} deve conter uma lista JSON.");
        }

        return JsonSerializer.Serialize(source);
    }

    private DbConnection CreateConnection(
        string databaseType,
        string requestedServer,
        string requestedDatabase)
    {
        (string type, string section) = NormalizeDatabaseType(databaseType);
        if (type == "sqlite")
        {
            return CreateSqliteConnection(section, requestedDatabase);
        }

        bool serverWasSent = !string.IsNullOrWhiteSpace(requestedServer);
        bool databaseWasSent = !string.IsNullOrWhiteSpace(requestedDatabase);
        string server = serverWasSent
            ? requestedServer.Trim()
            : configuration.Get(section, "Servidor");
        string database = string.IsNullOrWhiteSpace(requestedDatabase)
            ? configuration.Get(section, "Banco")
            : requestedDatabase.Trim();
        string user = configuration.Get(section, "Usuario");
        string password = configuration.Get(section, "Senha");
        int port = configuration.GetInt(section, "Porta", DefaultPort(type));

        return type switch
        {
            "sqlserver" => new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = port > 0 ? $"{server},{port}" : server,
                InitialCatalog = database,
                UserID = user,
                Password = password,
                Encrypt = true,
                TrustServerCertificate = true,
                Pooling = true,
                MaxPoolSize = 500
            }.ConnectionString),
            "postgresql" => new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = server,
                Port = port,
                Database = database,
                Username = user,
                Password = password,
                Pooling = true,
                MaxPoolSize = 500
            }.ConnectionString),
            "mariadb" or "mysql" => new MySqlConnection(new MySqlConnectionStringBuilder
            {
                Server = server,
                Port = (uint)port,
                Database = database,
                UserID = user,
                Password = password,
                Pooling = true,
                MaximumPoolSize = 500
            }.ConnectionString),
            "oracle" => new OracleConnection(new OracleConnectionStringBuilder
            {
                DataSource = serverWasSent || databaseWasSent
                    ? $"{server}:{port}/{database}"
                    : configuration.Get(section, "DSN", $"{server}:{port}/{database}"),
                UserID = user,
                Password = password,
                Pooling = true,
                MaxPoolSize = 500
            }.ConnectionString),
            "hfsql" => new OdbcConnection(BuildHfSqlConnectionString(
                section,
                server,
                port,
                database,
                user,
                password,
                useConfiguredDsn: !serverWasSent && !databaseWasSent)),
            _ => throw new NotSupportedException("tipo_banco não suportado.")
        };
    }

    private SqliteConnection CreateSqliteConnection(string section, string requestedDatabase)
    {
        string database = string.IsNullOrWhiteSpace(requestedDatabase)
            ? configuration.Get(section, "Banco")
            : requestedDatabase.Trim();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ResolveSqliteDatabasePath(section, database),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = configuration.GetInt("ServidorHTTP", "TimeoutSegundos", 30)
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private string ResolveSqliteDatabasePath(string section, string database)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidDataException(
                "Informe o arquivo SQLite no campo banco, por exemplo clientes.db.");
        }

        string configuredDirectory = configuration.Get(section, "Diretorio", "BasesSQLite");
        if (string.IsNullOrWhiteSpace(configuredDirectory)
            || configuredDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new InvalidDataException("Diretório-base do SQLite inválido.");
        }

        string baseDirectory = Path.GetFullPath(Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(configuration.DataDirectory, configuredDirectory));
        Directory.CreateDirectory(baseDirectory);

        return Path.GetFullPath(Path.Combine(baseDirectory, database));
    }

    private string BuildHfSqlConnectionString(
        string section,
        string server,
        int port,
        string database,
        string user,
        string password,
        bool useConfiguredDsn)
    {
        string dsn = configuration.Get(section, "DSN");
        return !useConfiguredDsn || string.IsNullOrWhiteSpace(dsn)
            ? $"DRIVER={{HFSQL}};Server Name={server}:{port};Database={database};UID={user};PWD={password};"
            : $"DSN={dsn};UID={user};PWD={password};";
    }

    private static bool LooksLikeQuery(string sql)
    {
        return Regex.IsMatch(
            sql,
            @"^\s*(SELECT|WITH|SHOW|DESCRIBE|DESC|EXPLAIN|PRAGMA|VALUES)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string BuildFieldList(string fields)
    {
        if (string.IsNullOrWhiteSpace(fields) || fields.Trim() == "*")
        {
            return "*";
        }

        return string.Join(
            ',',
            fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ValidateIdentifier));
    }

    private static string ValidateQualifiedIdentifier(string value)
    {
        string[] parts = value.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2)
        {
            throw new InvalidDataException("Nome SQL inválido.");
        }

        return string.Join('.', parts.Select(ValidateIdentifier));
    }

    private static string ValidateIdentifier(string value)
    {
        if (!IdentifierPattern.IsMatch(value))
        {
            throw new InvalidDataException("Nome SQL inválido.");
        }

        return value;
    }

    private static string QuoteQualifiedIdentifier(string type, string value)
    {
        return string.Join('.', value.Split('.').Select(part => QuoteIdentifier(type, part)));
    }

    private static string QuoteIdentifier(string type, string value)
    {
        return type switch
        {
            "sqlserver" => $"[{value}]",
            "mysql" or "mariadb" => $"`{value}`",
            _ => $"\"{value}\""
        };
    }

    private static object? NormalizeDbValue(object? value)
    {
        return value switch
        {
            DBNull => null,
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => value
        };
    }

    private static int DefaultPort(string type)
    {
        return type switch
        {
            "sqlserver" => 1433,
            "postgresql" => 5432,
            "mariadb" or "mysql" => 3306,
            "oracle" => 1521,
            "hfsql" => 4900,
            _ => 0
        };
    }

    private static void AddParameter(DbCommand command, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AddNamedParameter(
        DbCommand command,
        string databaseType,
        string name,
        object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = databaseType == "hfsql"
            ? string.Empty
            : databaseType == "oracle"
                ? $":{name}"
                : $"@{name}";
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
