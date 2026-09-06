using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;
using Microsoft.Data.Sqlite;

namespace FacilApp.Sql.Api.Services;

public sealed class ConnectionProfileStore
{
    private readonly string connectionString;

    private readonly byte[] masterKey;

    public ConnectionProfileStore(IniConfiguration configuration)
    {
        string text = Path.Combine(configuration.DataDirectory, "Dados");
        string text2 = Path.Combine(configuration.DataDirectory, "Segredos");
        Directory.CreateDirectory(text);
        Directory.CreateDirectory(text2);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(text, "facilapp_sql.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
        string path = Path.Combine(text2, "facilapp_sql.masterkey");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
        }
        masterKey = File.ReadAllBytes(path);
        if (masterKey.Length != 32)
        {
            throw new InvalidDataException("Chave mestra do FacilApp SQL inválida.");
        }
    }

    public async Task<IReadOnlyList<ConnectionProfileView>> ListAsync(string cnpj, CancellationToken cancellationToken)
    {
        List<ConnectionProfileView> profiles = new List<ConnectionProfileView>();
        IReadOnlyList<ConnectionProfileView> result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            IReadOnlyList<ConnectionProfileView> readOnlyList2;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT c.id,c.nome,c.tipo_banco,c.configuracao_json,c.senha_cifrada IS NOT NULL,c.ativo,c.atualizado_em\nFROM console_conexoes c JOIN console_empresas e ON e.id=c.empresa_id\nWHERE e.cnpj=$cnpj ORDER BY c.nome;";
                command.Parameters.AddWithValue("$cnpj", OnlyDigits(cnpj));
                IReadOnlyList<ConnectionProfileView> readOnlyList;
                await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        profiles.Add(new ConnectionProfileView(reader.GetString(0), reader.GetString(1), reader.GetString(2), JsonDocument.Parse(reader.GetString(3)).RootElement.Clone(), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetString(6)));
                    }
                    readOnlyList = profiles;
                }
                readOnlyList2 = readOnlyList;
            }
            result = readOnlyList2;
        }
        return result;
    }

    public async Task<ConnectionProfileView> SaveAsync(string cnpj, ConnectionProfileRequest request, CancellationToken cancellationToken)
    {
        string normalizedType = DatabaseService.NormalizeDatabaseType(request.TipoBanco).Type;
        string profileId = (string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("D") : request.Id);
        string normalizedCnpj = OnlyDigits(cnpj);
        string now = DateTimeOffset.Now.ToString("O");
        byte[] cipher = null;
        byte[] nonce = null;
        byte[] tag = null;
        if (!string.IsNullOrEmpty(request.Senha))
        {
            nonce = RandomNumberGenerator.GetBytes(12);
            tag = new byte[16];
            cipher = new byte[Encoding.UTF8.GetByteCount(request.Senha)];
            using AesGcm aesGcm = new AesGcm(masterKey, tag.Length);
            aesGcm.Encrypt(nonce, Encoding.UTF8.GetBytes(request.Senha), cipher, tag, Encoding.UTF8.GetBytes(normalizedCnpj + "|" + profileId + "|v1"));
        }
        ConnectionProfileView result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            ConnectionProfileView connectionProfileView;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO console_conexoes(id,empresa_id,nome,tipo_banco,configuracao_json,senha_cifrada,senha_nonce,senha_tag,ativo,criado_em,atualizado_em)\nSELECT $id,e.id,$nome,$tipo,$json,$cipher,$nonce,$tag,1,$now,$now FROM console_empresas e WHERE e.cnpj=$cnpj\nON CONFLICT(empresa_id,nome) DO UPDATE SET tipo_banco=excluded.tipo_banco,configuracao_json=excluded.configuracao_json,\n    senha_cifrada=COALESCE(excluded.senha_cifrada,console_conexoes.senha_cifrada),\n    senha_nonce=COALESCE(excluded.senha_nonce,console_conexoes.senha_nonce),senha_tag=COALESCE(excluded.senha_tag,console_conexoes.senha_tag),\n    ativo=1,atualizado_em=excluded.atualizado_em;";
                command.Parameters.AddWithValue("$id", profileId);
                command.Parameters.AddWithValue("$nome", request.Nome.Trim());
                command.Parameters.AddWithValue("$tipo", normalizedType);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(request.Configuracao));
                command.Parameters.AddWithValue("$cipher", ((object)cipher) ?? ((object)DBNull.Value));
                command.Parameters.AddWithValue("$nonce", ((object)nonce) ?? ((object)DBNull.Value));
                command.Parameters.AddWithValue("$tag", ((object)tag) ?? ((object)DBNull.Value));
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$cnpj", normalizedCnpj);
                if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new KeyNotFoundException("Empresa não encontrada.");
                }
                connectionProfileView = (await ListAsync(normalizedCnpj, cancellationToken)).Single((ConnectionProfileView item) => item.Nome.Equals(request.Nome.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            result = connectionProfileView;
        }
        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        SqliteConnection result;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            result = connection;
        }
        return result;
    }

    private static string OnlyDigits(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
    }
}
