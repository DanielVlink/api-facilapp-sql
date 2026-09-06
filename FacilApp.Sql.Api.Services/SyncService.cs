using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Replica tabelas SQLite para um PostgreSQL multiempresa. A estrutura de origem
/// e lida automaticamente; tabelas e colunas ausentes sao criadas sem excluir dados.
/// </summary>
public sealed class SyncService
{
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IniConfiguration configuration;

    public SyncService(IniConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public object Status()
    {
        return new
        {
            ativo = configuration.GetBoolean("Sincronizacao", "Ativo", false),
            tipoBancoNuvem = configuration.Get("Sincronizacao", "TipoBancoNuvem", "PostgreSQL"),
            bancoLocal = configuration.Get("Sincronizacao", "BancoLocal", configuration.Get("SQLite", "Banco", "facilapp.db")),
            documentoEmpresaConfigurado = OnlyDigits(configuration.Get("Sincronizacao", "DocumentoEmpresa")).Length is 11 or 14,
            intervaloSegundos = configuration.GetInt("Sincronizacao", "IntervaloSegundos", 10),
            quantidadePorLote = configuration.GetInt("Sincronizacao", "QuantidadePorLote", 100)
        };
    }

    public async Task<object> SynchronizeTableAsync(
        string table,
        string requestedLocalDatabase,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetBoolean("Sincronizacao", "Ativo", false))
            throw new InvalidOperationException("A sincronizacao esta desativada no INI.");

        if (!configuration.Get("Sincronizacao", "TipoBancoNuvem", "PostgreSQL")
                .Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("A primeira versao da sincronizacao usa PostgreSQL como destino.");

        table = ValidateIdentifier(table, "tabela");
        string cloudDatabase = configuration.Get("Sincronizacao", "BancoNuvem", configuration.Get("PostgreSQL", "Banco", "FACILAPP_API"));
        string document = OnlyDigits(configuration.Get("Sincronizacao", "DocumentoEmpresa"));
        if (document.Length is not (11 or 14))
            document = new string(cloudDatabase.TakeWhile(char.IsDigit).ToArray());
        if (document.Length is not (11 or 14))
            throw new InvalidDataException("O BancoNuvem deve iniciar com CPF/CNPJ ou DocumentoEmpresa deve ser configurado.");

        string localDatabase = string.IsNullOrWhiteSpace(requestedLocalDatabase)
            ? configuration.Get("Sincronizacao", "BancoLocal", configuration.Get("SQLite", "Banco", "facilapp.db"))
            : requestedLocalDatabase.Trim();
        string sqlitePath = ResolveSqlitePath(localDatabase);
        if (!File.Exists(sqlitePath))
            throw new FileNotFoundException($"Banco SQLite '{Path.GetFileName(sqlitePath)}' nao encontrado.");

        await using SqliteConnection source = new(new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        await source.OpenAsync(cancellationToken);

        IReadOnlyList<SourceColumn> columns = await ReadSourceColumnsAsync(source, table, cancellationToken);
        if (columns.Count == 0)
            throw new InvalidDataException($"Tabela SQLite '{table}' nao encontrada ou sem campos.");

        await using NpgsqlConnection destination = CreatePostgreSqlConnection(cloudDatabase);
        await destination.OpenAsync(cancellationToken);
        await EnsureDestinationSchemaAsync(destination, table, columns, cancellationToken);

        int synchronized = 0;
        int batchSize = Math.Clamp(configuration.GetInt("Sincronizacao", "QuantidadePorLote", 100), 1, 5000);
        await using SqliteCommand select = source.CreateCommand();
        select.CommandText = $"SELECT * FROM {QuoteSqlite(table)}";
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
        NpgsqlTransaction transaction = await destination.BeginTransactionAsync(cancellationToken);
        try
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                await UpsertAsync(destination, transaction, table, columns, reader, document, cancellationToken);
                synchronized++;
                if (synchronized % batchSize == 0)
                {
                    await transaction.CommitAsync(cancellationToken);
                    await transaction.DisposeAsync();
                    transaction = await destination.BeginTransactionAsync(cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await transaction.DisposeAsync();
        }

        return new
        {
            ok = true,
            tabela = table,
            bancoLocal = Path.GetFileName(sqlitePath),
            bancoNuvem = destination.Database,
            documentoEmpresa = document,
            registrosSincronizados = synchronized,
            campos = columns.Count
        };
    }

    private async Task<IReadOnlyList<SourceColumn>> ReadSourceColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        List<SourceColumn> columns = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteSqlite(table)})";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string name = ValidateIdentifier(reader.GetString(1), "campo");
            string sqliteType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            columns.Add(new SourceColumn(name, MapPostgreSqlType(sqliteType), reader.GetInt32(5) > 0));
        }
        return columns;
    }

    private static async Task EnsureDestinationSchemaAsync(
        NpgsqlConnection connection,
        string table,
        IReadOnlyList<SourceColumn> columns,
        CancellationToken cancellationToken)
    {
        string fields = string.Join(", ", columns.Select(c => $"{QuotePostgres(c.Name)} {c.PostgreSqlType}"));
        await using (NpgsqlCommand create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE IF NOT EXISTS {QuotePostgres(table)} ({fields}, _facilapp_empresa text NOT NULL, _facilapp_uuid text NOT NULL, _facilapp_sincronizado_em timestamptz NOT NULL DEFAULT now(), UNIQUE (_facilapp_empresa, _facilapp_uuid))";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (SourceColumn column in columns)
        {
            await using NpgsqlCommand add = connection.CreateCommand();
            add.CommandText = $"ALTER TABLE {QuotePostgres(table)} ADD COLUMN IF NOT EXISTS {QuotePostgres(column.Name)} {column.PostgreSqlType}";
            await add.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach ((string name, string type) in new[]
                 {
                     ("_facilapp_empresa", "text"), ("_facilapp_uuid", "text"), ("_facilapp_sincronizado_em", "timestamptz")
                 })
        {
            await using NpgsqlCommand add = connection.CreateCommand();
            add.CommandText = $"ALTER TABLE {QuotePostgres(table)} ADD COLUMN IF NOT EXISTS {QuotePostgres(name)} {type}";
            await add.ExecuteNonQueryAsync(cancellationToken);
        }
        await using NpgsqlCommand index = connection.CreateCommand();
        index.CommandText = $"CREATE UNIQUE INDEX IF NOT EXISTS {QuotePostgres("ux_" + table + "_empresa_uuid")} ON {QuotePostgres(table)} (_facilapp_empresa, _facilapp_uuid)";
        await index.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        IReadOnlyList<SourceColumn> columns,
        SqliteDataReader reader,
        string document,
        CancellationToken cancellationToken)
    {
        string uuid = ResolveStableUuid(table, columns, reader, document);
        List<string> names = columns.Select(c => QuotePostgres(c.Name)).ToList();
        names.Add("_facilapp_empresa");
        names.Add("_facilapp_uuid");
        List<string> parameters = Enumerable.Range(0, columns.Count).Select(i => $"@p{i}").ToList();
        parameters.Add("@documento");
        parameters.Add("@uuid");
        string updates = string.Join(", ", columns.Select(c => $"{QuotePostgres(c.Name)}=EXCLUDED.{QuotePostgres(c.Name)}"));

        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {QuotePostgres(table)} ({string.Join(",", names)}) VALUES ({string.Join(",", parameters)}) ON CONFLICT (_facilapp_empresa, _facilapp_uuid) DO UPDATE SET {updates}, _facilapp_sincronizado_em=now()";
        for (int i = 0; i < columns.Count; i++)
            command.Parameters.AddWithValue($"p{i}", NormalizeValue(reader.GetValue(i)) ?? DBNull.Value);
        command.Parameters.AddWithValue("documento", document);
        command.Parameters.AddWithValue("uuid", uuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private NpgsqlConnection CreatePostgreSqlConnection(string cloudDatabase)
    {
        return new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = configuration.Get("PostgreSQL", "Servidor", "127.0.0.1"),
            Port = configuration.GetInt("PostgreSQL", "Porta", 5432),
            Database = cloudDatabase,
            Username = configuration.Get("PostgreSQL", "Usuario"),
            Password = configuration.Get("PostgreSQL", "Senha"),
            Pooling = true,
            Timeout = configuration.GetInt("ServidorHTTP", "TimeoutSegundos", 30),
            CommandTimeout = configuration.GetInt("ServidorHTTP", "TimeoutSegundos", 30)
        }.ConnectionString);
    }

    private string ResolveSqlitePath(string database)
    {
        string file = Path.GetFileName(database);
        if (!file.Equals(database, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(file))
            throw new InvalidDataException("Informe somente o nome do banco SQLite.");
        string directory = configuration.Get("SQLite", "Diretorio", "BasesSQLite");
        if (!Path.IsPathRooted(directory)) directory = Path.Combine(configuration.DataDirectory, directory);
        return Path.Combine(Path.GetFullPath(directory), file);
    }

    private static string ResolveStableUuid(
        string table,
        IReadOnlyList<SourceColumn> columns,
        SqliteDataReader reader,
        string document)
    {
        int explicitUuid = columns.ToList().FindIndex(c => c.Name.Equals("uuid_origem", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("uuid", StringComparison.OrdinalIgnoreCase));
        if (explicitUuid >= 0 && !reader.IsDBNull(explicitUuid))
        {
            string value = Convert.ToString(reader.GetValue(explicitUuid), CultureInfo.InvariantCulture) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        int[] keyIndexes = columns.Select((column, index) => (column, index)).Where(x => x.column.IsPrimaryKey).Select(x => x.index).ToArray();
        if (keyIndexes.Length == 0) keyIndexes = Enumerable.Range(0, columns.Count).ToArray();
        string identity = string.Join("|", keyIndexes.Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "<null>"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{document}|{table}|{identity}"))).ToLowerInvariant();
    }

    private static object? NormalizeValue(object value)
    {
        if (value is DBNull) return null;
        return value is byte[] or long or int or short or double or float or decimal or bool or DateTime
            ? value
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string MapPostgreSqlType(string sqliteType)
    {
        string type = sqliteType.ToUpperInvariant();
        if (type.Contains("INT")) return "bigint";
        if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB")) return "double precision";
        if (type.Contains("NUM") || type.Contains("DEC")) return "numeric";
        if (type.Contains("BLOB")) return "bytea";
        if (type.Contains("BOOL")) return "boolean";
        return "text";
    }

    private static string ValidateIdentifier(string value, string field)
    {
        value = value.Trim();
        if (!IdentifierPattern.IsMatch(value)) throw new InvalidDataException($"Nome de {field} invalido: '{value}'.");
        return value;
    }

    private static string QuoteSqlite(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string QuotePostgres(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string OnlyDigits(string value) => new(value.Where(char.IsDigit).ToArray());

    private sealed record SourceColumn(string Name, string PostgreSqlType, bool IsPrimaryKey);
}
