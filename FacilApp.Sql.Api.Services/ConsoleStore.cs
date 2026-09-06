using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;
using Microsoft.Data.Sqlite;

namespace FacilApp.Sql.Api.Services;

public sealed class ConsoleStore
{
    private const string MasterCredentialerId = "facilapp-sql-master";

    private readonly string connectionString;

    private const string CompanySelect = "SELECT id,tipo_pessoa,cnpj,email,razao_social,nome_fantasia,inscricao_estadual,inscricao_municipal,telefone,cep,codigo_ibge,uf,cidade,endereco,numero,complemento,bairro,criado_em,atualizado_em,genero,data_nascimento,nome_mae,idade,zodiaco,telefones_json,enderecos_json,emails_json,salario_estimado,status_cadastral,data_status_cadastral,ultima_atualizacao_fonte,consulta_json,tipo_pagamento FROM console_empresas";

    public ConsoleStore(IniConfiguration configuration)
    {
        string text = Path.Combine(configuration.DataDirectory, "Dados");
        Directory.CreateDirectory(text);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(text, "facilapp_sql.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default(CancellationToken))
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS console_sistemas (\n    id TEXT PRIMARY KEY COLLATE NOCASE, nome TEXT NOT NULL, ativo INTEGER NOT NULL DEFAULT 1,\n    criado_em TEXT NOT NULL, atualizado_em TEXT NOT NULL\n);\nCREATE TABLE IF NOT EXISTS console_credenciadores (\n    id TEXT PRIMARY KEY, nome TEXT NOT NULL, email TEXT NOT NULL UNIQUE,\n    tipo TEXT NOT NULL, situacao TEXT NOT NULL, criado_em TEXT NOT NULL, atualizado_em TEXT NOT NULL\n);\nCREATE TABLE IF NOT EXISTS console_empresas (\n    id TEXT PRIMARY KEY, credenciador_id TEXT NOT NULL, tipo_pessoa TEXT NOT NULL,\n    cnpj TEXT NOT NULL UNIQUE, email TEXT NOT NULL, razao_social TEXT NOT NULL,\n    nome_fantasia TEXT, inscricao_estadual TEXT, inscricao_municipal TEXT, telefone TEXT,\n    cep TEXT NOT NULL, codigo_ibge TEXT NOT NULL, uf TEXT NOT NULL, cidade TEXT NOT NULL,\n    endereco TEXT NOT NULL, numero TEXT NOT NULL, complemento TEXT, bairro TEXT NOT NULL,\n    ativo INTEGER NOT NULL DEFAULT 1, criado_em TEXT NOT NULL, atualizado_em TEXT NOT NULL,\n    FOREIGN KEY(credenciador_id) REFERENCES console_credenciadores(id)\n);\nCREATE TABLE IF NOT EXISTS console_credenciais (\n    client_id TEXT PRIMARY KEY, credenciador_id TEXT NOT NULL, nome TEXT NOT NULL,\n    secret_hash BLOB NOT NULL, secret_salt BLOB NOT NULL, tipo TEXT NOT NULL,\n    permissoes_json TEXT NOT NULL, ativo INTEGER NOT NULL DEFAULT 1,\n    criado_em TEXT NOT NULL, atualizado_em TEXT NOT NULL,\n    FOREIGN KEY(credenciador_id) REFERENCES console_credenciadores(id)\n);\nCREATE TABLE IF NOT EXISTS console_conexoes (\n    id TEXT PRIMARY KEY, empresa_id TEXT NOT NULL, nome TEXT NOT NULL, tipo_banco TEXT NOT NULL,\n    configuracao_json TEXT NOT NULL, senha_cifrada BLOB, senha_nonce BLOB, senha_tag BLOB,\n    ativo INTEGER NOT NULL DEFAULT 1, criado_em TEXT NOT NULL, atualizado_em TEXT NOT NULL,\n    UNIQUE(empresa_id,nome), FOREIGN KEY(empresa_id) REFERENCES console_empresas(id) ON DELETE CASCADE\n);\nCREATE INDEX IF NOT EXISTS ix_console_conexoes_empresa ON console_conexoes(empresa_id,ativo);";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using SqliteCommand paymentSchema = connection.CreateCommand();
        paymentSchema.CommandText = """
            CREATE TABLE IF NOT EXISTS console_pagamentos (
                id TEXT PRIMARY KEY,
                empresa_id TEXT NOT NULL,
                data_pagamento TEXT NOT NULL,
                valor TEXT NOT NULL,
                referencia TEXT,
                criado_em TEXT NOT NULL,
                FOREIGN KEY(empresa_id) REFERENCES console_empresas(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_console_pagamentos_empresa_data
                ON console_pagamentos(empresa_id,data_pagamento DESC);
            """;
        await paymentSchema.ExecuteNonQueryAsync(cancellationToken);
        await using SqliteCommand logoSchema = connection.CreateCommand();
        logoSchema.CommandText = "CREATE TABLE IF NOT EXISTS console_empresa_logos (empresa_id TEXT PRIMARY KEY, mime_type TEXT NOT NULL, dados BLOB NOT NULL, criado_em TEXT NOT NULL, atualizado_em TEXT NOT NULL, FOREIGN KEY(empresa_id) REFERENCES console_empresas(id) ON DELETE CASCADE);";
        await logoSchema.ExecuteNonQueryAsync(cancellationToken);
        await using SqliteCommand authSchema = connection.CreateCommand();
        authSchema.CommandText = "CREATE TABLE IF NOT EXISTS console_master_auth (conta_id TEXT PRIMARY KEY, email TEXT NOT NULL, senha_hash BLOB NOT NULL, senha_salt BLOB NOT NULL, atualizado_em TEXT NOT NULL); CREATE TABLE IF NOT EXISTS console_sessoes (token_hash BLOB PRIMARY KEY, criado_em TEXT NOT NULL, expira_em TEXT NOT NULL);";
        await authSchema.ExecuteNonQueryAsync(cancellationToken);
        await using SqliteCommand userMigration = connection.CreateCommand();
        userMigration.CommandText = """
			CREATE TABLE IF NOT EXISTS console_usuarios (
			    id TEXT PRIMARY KEY,
			    empresa_id TEXT NOT NULL,
			    sistema_id TEXT NOT NULL DEFAULT 'curso',
			    nome TEXT NOT NULL,
			    usuario TEXT NOT NULL COLLATE NOCASE,
			    senha_hash BLOB NOT NULL,
			    senha_salt BLOB NOT NULL,
			    client_id TEXT NOT NULL,
			    menu_data TEXT NOT NULL DEFAULT '[]',
			    menu_superior_data TEXT NOT NULL DEFAULT '[]',
			    dashboard_data TEXT NOT NULL DEFAULT '[]',
			    ativo INTEGER NOT NULL DEFAULT 1,
			    criado_em TEXT NOT NULL,
			    atualizado_em TEXT NOT NULL,
			    UNIQUE(sistema_id,empresa_id,usuario),
			    FOREIGN KEY(empresa_id) REFERENCES console_empresas(id) ON DELETE CASCADE,
			    FOREIGN KEY(client_id) REFERENCES console_credenciais(client_id)
			);
			""";
        await userMigration.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "console_usuarios", "sistema_id", "TEXT NOT NULL DEFAULT 'curso'", cancellationToken);
        await using (SqliteCommand userSystemIndex = connection.CreateCommand())
        {
            userSystemIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_console_usuarios_sistema_empresa_usuario ON console_usuarios(sistema_id,empresa_id,usuario);";
            await userSystemIndex.ExecuteNonQueryAsync(cancellationToken);
        }
        // Migração interna da versão atual: o instalador nunca substitui o
        // facilapp_sql.db do cliente. Em toda inicialização, portanto, a API
        // compara a lista de campos desta versão e aplica ALTER TABLE apenas
        // para o que ainda não existe, preservando registros já gravados.
        await EnsureCurrentSchemaColumnsAsync(connection, cancellationToken);
        await EnsureColumnAsync(connection, "console_usuarios", "menu_data", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "console_usuarios", "menu_superior_data", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "console_usuarios", "dashboard_data", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "genero", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "data_nascimento", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "nome_mae", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "idade", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "zodiaco", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "telefones_json", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "enderecos_json", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "emails_json", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "salario_estimado", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "status_cadastral", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "data_status_cadastral", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "ultima_atualizacao_fonte", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "consulta_json", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "console_empresas", "tipo_pagamento", "TEXT NOT NULL DEFAULT 'mensalidade'", cancellationToken);
        string value = DateTimeOffset.Now.ToString("O");
        await using SqliteCommand seed = connection.CreateCommand();
        seed.CommandText = "INSERT OR IGNORE INTO console_credenciadores(id,nome,email,tipo,situacao,criado_em,atualizado_em) VALUES($id,'FacilApp','master@facilapp.com.br','Software House','ATIVO',$now,$now);";
        seed.Parameters.AddWithValue("$id", "facilapp-sql-master");
        seed.Parameters.AddWithValue("$now", value);
        await seed.ExecuteNonQueryAsync(cancellationToken);
        await using SqliteCommand systemSeed = connection.CreateCommand();
        systemSeed.CommandText = "INSERT OR IGNORE INTO console_sistemas(id,nome,ativo,criado_em,atualizado_em) VALUES " +
                                 "('curso','Curso',1,$now,$now)," +
                                 "('gourmet','Gourmet',1,$now,$now)," +
                                 "('cereais','Cereais',1,$now,$now)," +
                                 "('quimicos','Químicos',1,$now,$now)," +
                                 "('nfe','NFe',1,$now,$now)," +
                                 "('pedidos','Pedidos',1,$now,$now);";
        systemSeed.Parameters.AddWithValue("$now", value);
        await systemSeed.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Informa se a senha do console já foi configurada.</summary>
    public async Task<bool> IsMasterConfiguredAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM console_master_auth WHERE conta_id='master');";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    /// <summary>Configura a senha inicial do console uma única vez.</summary>
    public async Task<bool> ConfigureMasterPasswordAsync(string email, string password, CancellationToken cancellationToken)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO console_master_auth(conta_id,email,senha_hash,senha_salt,atualizado_em) VALUES('master',$email,$hash,$salt,$now);";
        command.Parameters.AddWithValue("$email", email.Trim());
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$salt", salt);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Valida o e-mail e a senha do console sem expor o hash.</summary>
    public async Task<bool> AuthenticateMasterAsync(string email, string password, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT senha_hash,senha_salt FROM console_master_auth WHERE conta_id='master' AND lower(email)=lower($email) LIMIT 1;";
        command.Parameters.AddWithValue("$email", email.Trim());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return false;
        byte[] expected = (byte[])reader[0];
        byte[] supplied = Rfc2898DeriveBytes.Pbkdf2(password, (byte[])reader[1], 210000, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    /// <summary>Cria uma sessão temporária; somente o hash do cookie é persistido.</summary>
    public async Task<string> CreateMasterSessionAsync(CancellationToken cancellationToken)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        // Instalações anteriores podem ter criado console_sessoes com a coluna
        // usuario_id obrigatória. Detectamos esse formato para que a atualização
        // preserve a base existente sem exigir apagar o banco do cliente.
        bool legacyUserColumn;
        await using (SqliteCommand schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = "PRAGMA table_info(console_sessoes);";
            legacyUserColumn = false;
            await using SqliteDataReader schemaReader = await schemaCommand.ExecuteReaderAsync(cancellationToken);
            while (await schemaReader.ReadAsync(cancellationToken))
            {
                if (string.Equals(schemaReader.GetString(1), "usuario_id", StringComparison.OrdinalIgnoreCase))
                {
                    legacyUserColumn = true;
                    break;
                }
            }
        }
        await using SqliteCommand command = connection.CreateCommand();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        command.CommandText = legacyUserColumn
            ? "INSERT INTO console_sessoes(token_hash,usuario_id,criado_em,expira_em) VALUES($hash,$usuario,$now,$expires);"
            : "INSERT INTO console_sessoes(token_hash,criado_em,expira_em) VALUES($hash,$now,$expires);";
        command.Parameters.AddWithValue("$hash", SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        if (legacyUserColumn) command.Parameters.AddWithValue("$usuario", "master");
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$expires", now.AddHours(12).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return token;
    }

    /// <summary>Valida uma sessão temporária do console.</summary>
    public async Task<bool> IsMasterSessionAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM console_sessoes WHERE token_hash=$hash AND expira_em>$now);";
        command.Parameters.AddWithValue("$hash", SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    /// <summary>Revoga uma sessão do console.</summary>
    public async Task RevokeSessionAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM console_sessoes WHERE token_hash=$hash;";
        command.Parameters.AddWithValue("$hash", SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CompanyView>> ListCompaniesAsync(CancellationToken cancellationToken)
    {
        List<CompanyView> items = new List<CompanyView>();
        IReadOnlyList<CompanyView> result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            IReadOnlyList<CompanyView> readOnlyList2;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = CompanySelect + " WHERE ativo=1 ORDER BY razao_social;";
                IReadOnlyList<CompanyView> readOnlyList;
                await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        items.Add(ReadCompany(reader));
                    }
                    readOnlyList = items;
                }
                readOnlyList2 = readOnlyList;
            }
            result = readOnlyList2;
        }
        return result;
    }

    public async Task<CompanyView?> GetCompanyAsync(string cnpj, CancellationToken cancellationToken)
    {
        CompanyView result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            CompanyView companyView2;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = CompanySelect + " WHERE cnpj=$cnpj AND ativo=1 LIMIT 1;";
                command.Parameters.AddWithValue("$cnpj", OnlyDigits(cnpj));
                CompanyView companyView;
                await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    companyView = ((await reader.ReadAsync(cancellationToken)) ? ReadCompany(reader) : null);
                }
                companyView2 = companyView;
            }
            result = companyView2;
        }
        return result;
    }

    public async Task<CompanyView> SaveCompanyAsync(CompanyRequest request, CancellationToken cancellationToken)
    {
        string cnpj = OnlyDigits(request.Cnpj);
        if (cnpj.Length != 11 && cnpj.Length != 14)
        {
            throw new InvalidDataException("CPF/CNPJ deve possuir 11 ou 14 dígitos.");
        }
        string now = DateTimeOffset.Now.ToString("O");
        CompanyView result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            CompanyView companyView;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO console_empresas(id,credenciador_id,tipo_pessoa,cnpj,email,razao_social,nome_fantasia,\n    inscricao_estadual,inscricao_municipal,telefone,cep,codigo_ibge,uf,cidade,endereco,numero,\n    complemento,bairro,ativo,criado_em,atualizado_em,genero,data_nascimento,nome_mae,idade,zodiaco,\n    telefones_json,enderecos_json,emails_json,salario_estimado,status_cadastral,data_status_cadastral,ultima_atualizacao_fonte,consulta_json)\nVALUES($id,$cred,$tipo,$cnpj,$email,$razao,$fantasia,$ie,$im,$fone,$cep,$ibge,$uf,$cidade,$endereco,\n    $numero,$complemento,$bairro,1,$now,$now,$genero,$nascimento,$mae,$idade,$zodiaco,$telefones,$enderecos,$emails,$salario,$status,$dataStatus,$ultimaFonte,$consulta)\nON CONFLICT(cnpj) DO UPDATE SET tipo_pessoa=excluded.tipo_pessoa,email=excluded.email,\n    razao_social=excluded.razao_social,nome_fantasia=excluded.nome_fantasia,\n    inscricao_estadual=excluded.inscricao_estadual,inscricao_municipal=excluded.inscricao_municipal,\n    telefone=excluded.telefone,cep=excluded.cep,codigo_ibge=excluded.codigo_ibge,uf=excluded.uf,\n    cidade=excluded.cidade,endereco=excluded.endereco,numero=excluded.numero,\n    complemento=excluded.complemento,bairro=excluded.bairro,genero=excluded.genero,data_nascimento=excluded.data_nascimento,\n    nome_mae=excluded.nome_mae,idade=excluded.idade,zodiaco=excluded.zodiaco,telefones_json=excluded.telefones_json,\n    enderecos_json=excluded.enderecos_json,emails_json=excluded.emails_json,salario_estimado=excluded.salario_estimado,\n    status_cadastral=excluded.status_cadastral,data_status_cadastral=excluded.data_status_cadastral,\n    ultima_atualizacao_fonte=excluded.ultima_atualizacao_fonte,consulta_json=excluded.consulta_json,ativo=1,atualizado_em=excluded.atualizado_em;";
                Add(command, "$id", Guid.NewGuid().ToString("D"));
                Add(command, "$cred", "facilapp-sql-master");
                Add(command, "$tipo", request.TipoPessoa);
                Add(command, "$cnpj", cnpj);
                Add(command, "$email", request.Email);
                Add(command, "$razao", request.RazaoSocial);
                Add(command, "$fantasia", request.NomeFantasia);
                Add(command, "$ie", request.InscricaoEstadual);
                Add(command, "$im", request.InscricaoMunicipal);
                Add(command, "$fone", request.Telefone);
                Add(command, "$cep", request.Cep);
                Add(command, "$ibge", request.CodigoIbge);
                Add(command, "$uf", request.Uf.ToUpperInvariant());
                Add(command, "$cidade", request.Cidade);
                Add(command, "$endereco", request.Endereco);
                Add(command, "$numero", request.Numero);
                Add(command, "$complemento", request.Complemento);
                Add(command, "$bairro", request.Bairro);
                Add(command, "$genero", request.Genero);
                Add(command, "$nascimento", request.DataNascimento);
                Add(command, "$mae", request.NomeMae);
                command.Parameters.AddWithValue("$idade", request.Idade.HasValue ? request.Idade.Value : DBNull.Value);
                Add(command, "$zodiaco", request.Zodiaco);
                Add(command, "$telefones", request.TelefonesJson);
                Add(command, "$enderecos", request.EnderecosJson);
                Add(command, "$emails", request.EmailsJson);
                Add(command, "$salario", request.SalarioEstimado);
                Add(command, "$status", request.StatusCadastral);
                Add(command, "$dataStatus", request.DataStatusCadastral);
                Add(command, "$ultimaFonte", request.UltimaAtualizacaoFonte);
                Add(command, "$consulta", request.ConsultaJson);
                Add(command, "$now", now);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await using (SqliteCommand paymentTypeCommand = connection.CreateCommand())
                {
                    paymentTypeCommand.CommandText = "UPDATE console_empresas SET tipo_pagamento=$tipo WHERE cnpj=$cnpj;";
                    Add(paymentTypeCommand, "$tipo", NormalizePaymentType(request.TipoPagamento));
                    Add(paymentTypeCommand, "$cnpj", cnpj);
                    await paymentTypeCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                companyView = (await GetCompanyAsync(cnpj, cancellationToken)) ?? throw new InvalidOperationException("Empresa não encontrada após a gravação.");
            }
            result = companyView;
        }
        if (!string.IsNullOrWhiteSpace(request.LogoData))
        {
            await SaveCompanyLogoAsync(cnpj, request.LogoData, cancellationToken);
        }
        return result;
    }

    /// <summary>Grava um pagamento da empresa e preserva seu histórico financeiro.</summary>
    public async Task<CompanyPaymentView> RegisterCompanyPaymentAsync(
        string cnpj,
        CompanyPaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Valor <= 0)
        {
            throw new InvalidDataException("O valor do pagamento deve ser maior que zero.");
        }

        CompanyView? company = await GetCompanyAsync(cnpj, cancellationToken);
        if (company is null)
        {
            throw new KeyNotFoundException("Empresa não encontrada.");
        }

        string id = Guid.NewGuid().ToString("D");
        string paidAt = request.DataPagamento.ToUniversalTime().ToString("O");
        string createdAt = DateTimeOffset.UtcNow.ToString("O");
        decimal value = decimal.Round(request.Valor, 2, MidpointRounding.AwayFromZero);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO console_pagamentos(id,empresa_id,data_pagamento,valor,referencia,criado_em) VALUES($id,$empresa,$data,$valor,$referencia,$criado);";
        Add(command, "$id", id);
        Add(command, "$empresa", company.Id);
        Add(command, "$data", paidAt);
        Add(command, "$valor", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(command, "$referencia", request.Referencia);
        Add(command, "$criado", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new CompanyPaymentView(id, company.Id, paidAt, value, request.Referencia?.Trim(), createdAt);
    }

    /// <summary>Lista os pagamentos de uma empresa, do mais recente para o mais antigo.</summary>
    public async Task<IReadOnlyList<CompanyPaymentView>> ListCompanyPaymentsAsync(string cnpj, CancellationToken cancellationToken)
    {
        CompanyView? company = await GetCompanyAsync(cnpj, cancellationToken);
        if (company is null) return Array.Empty<CompanyPaymentView>();

        List<CompanyPaymentView> payments = new();
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,empresa_id,data_pagamento,valor,referencia,criado_em FROM console_pagamentos WHERE empresa_id=$empresa ORDER BY data_pagamento DESC,criado_em DESC;";
        Add(command, "$empresa", company.Id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            decimal value = decimal.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture);
            payments.Add(new CompanyPaymentView(reader.GetString(0), reader.GetString(1), reader.GetString(2), value, Text(reader, 4), reader.GetString(5)));
        }
        return payments;
    }

    /// <summary>Lista todas as empresas com a situação de mensalidade calculada para o browse administrativo.</summary>
    public async Task<IReadOnlyList<CompanyPaymentOverview>> ListCompanyPaymentOverviewsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CompanyView> companies = await ListCompaniesAsync(cancellationToken);
        List<CompanyPaymentOverview> overviews = new(companies.Count);
        foreach (CompanyView company in companies)
        {
            CompanyPaymentStatus status = await GetCompanyPaymentStatusAsync(company.Id, cancellationToken)
                ?? new CompanyPaymentStatus("mensalidade", false, "empresa_inativa", null, null, 0);
            overviews.Add(new CompanyPaymentOverview(company.Id, company.Cnpj, company.RazaoSocial, company.NomeFantasia, status));
        }
        return overviews;
    }

    /// <summary>Calcula se uma empresa pode usar a API conforme seu modelo de pagamento.</summary>
    public async Task<CompanyPaymentStatus?> GetCompanyPaymentStatusAsync(string companyId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT tipo_pagamento FROM console_empresas WHERE id=$empresa AND ativo=1 LIMIT 1;";
        Add(command, "$empresa", companyId);
        object? rawType = await command.ExecuteScalarAsync(cancellationToken);
        if (rawType is null || rawType is DBNull) return null;

        string type = NormalizePaymentType(Convert.ToString(rawType));
        if (type == "vitalicio")
        {
            return new CompanyPaymentStatus(type, true, "vitalicio", null, null, int.MaxValue);
        }

        await using SqliteCommand latest = connection.CreateCommand();
        latest.CommandText = "SELECT data_pagamento FROM console_pagamentos WHERE empresa_id=$empresa ORDER BY data_pagamento DESC,criado_em DESC LIMIT 1;";
        Add(latest, "$empresa", companyId);
        object? rawPayment = await latest.ExecuteScalarAsync(cancellationToken);
        if (rawPayment is null || rawPayment is DBNull || !DateTimeOffset.TryParse(Convert.ToString(rawPayment), out DateTimeOffset lastPayment))
        {
            return new CompanyPaymentStatus(type, false, "sem_pagamento", null, null, 0);
        }

        DateTimeOffset limit = lastPayment.AddDays(45);
        double remaining = (limit - DateTimeOffset.UtcNow).TotalDays;
        bool allowed = remaining >= 0;
        return new CompanyPaymentStatus(
            type,
            allowed,
            allowed ? "em_dia" : "vencido",
            lastPayment.ToString("O"),
            limit.ToString("O"),
            Math.Max(0, (int)Math.Ceiling(remaining)));
    }

    /// <summary>
    /// Exclui definitivamente a empresa identificada pelo CNPJ e os cadastros que só existem
    /// em função dela: logins, conexões e logotipo. A transação evita registros parciais.
    /// </summary>
    /// <param name="cnpj">CNPJ da empresa a excluir.</param>
    /// <param name="cancellationToken">Token de cancelamento da operação.</param>
    /// <returns>Verdadeiro quando uma empresa foi removida.</returns>
    public async Task<bool> DeleteCompanyAsync(string cnpj, CancellationToken cancellationToken)
    {
        string normalizedCnpj = OnlyDigits(cnpj);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction();

        async Task ExecuteAsync(string sql)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            Add(command, "$cnpj", normalizedCnpj);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteAsync("DELETE FROM console_empresa_logos WHERE empresa_id IN (SELECT id FROM console_empresas WHERE cnpj=$cnpj);");
        await ExecuteAsync("DELETE FROM console_usuarios WHERE empresa_id IN (SELECT id FROM console_empresas WHERE cnpj=$cnpj);");
        await ExecuteAsync("DELETE FROM console_conexoes WHERE empresa_id IN (SELECT id FROM console_empresas WHERE cnpj=$cnpj);");

        await using SqliteCommand deleteCompany = connection.CreateCommand();
        deleteCompany.Transaction = transaction;
        deleteCompany.CommandText = "DELETE FROM console_empresas WHERE cnpj=$cnpj;";
        Add(deleteCompany, "$cnpj", normalizedCnpj);
        bool deleted = await deleteCompany.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (deleted)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return deleted;
    }

    /// <summary>Retorna a logo persistida sem expor a imagem em cada listagem.</summary>
    public async Task<CompanyLogo?> GetCompanyLogoAsync(string cnpj, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT l.mime_type,l.dados FROM console_empresa_logos l INNER JOIN console_empresas e ON e.id=l.empresa_id WHERE e.cnpj=$cnpj AND e.ativo=1 LIMIT 1;";
        command.Parameters.AddWithValue("$cnpj", OnlyDigits(cnpj));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CompanyLogo(reader.GetString(0), (byte[])reader[1]);
    }

    /// <summary>Valida e grava a logo associada à empresa no SQLite interno.</summary>
    public async Task SaveCompanyLogoAsync(string cnpj, string dataUrl, CancellationToken cancellationToken)
    {
        CompanyLogo logo = ParseCompanyLogo(dataUrl);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO console_empresa_logos(empresa_id,mime_type,dados,criado_em,atualizado_em) SELECT e.id,$mime,$dados,$now,$now FROM console_empresas e WHERE e.cnpj=$cnpj AND e.ativo=1 ON CONFLICT(empresa_id) DO UPDATE SET mime_type=excluded.mime_type,dados=excluded.dados,atualizado_em=excluded.atualizado_em;";
        command.Parameters.AddWithValue("$cnpj", OnlyDigits(cnpj));
        command.Parameters.AddWithValue("$mime", logo.MimeType);
        command.Parameters.AddWithValue("$dados", logo.Data);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidDataException("Empresa não encontrada para gravar a logo.");
        }
    }

    public async Task<IReadOnlyList<ApiCredentialView>> ListCredentialsAsync(CancellationToken cancellationToken)
    {
        List<ApiCredentialView> items = new List<ApiCredentialView>();
        IReadOnlyList<ApiCredentialView> result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            IReadOnlyList<ApiCredentialView> readOnlyList2;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT client_id,nome,tipo,permissoes_json,ativo,criado_em FROM console_credenciais WHERE ativo=1 ORDER BY criado_em DESC;";
                IReadOnlyList<ApiCredentialView> readOnlyList;
                await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        items.Add(ReadCredential(reader));
                    }
                    readOnlyList = items;
                }
                readOnlyList2 = readOnlyList;
            }
            result = readOnlyList2;
        }
        return result;
    }

    public async Task<ApiCredentialCreated> CreateCredentialAsync(ApiCredentialRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Nome) || request.Permissoes.Count == 0)
        {
            throw new InvalidDataException("Nome e permissões são obrigatórios.");
        }
        string clientId = RandomToken(15);
        string secret = RandomToken(32);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, 210000, HashAlgorithmName.SHA256, 32);
        string now = DateTimeOffset.Now.ToString("O");
        ApiCredentialCreated result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            ApiCredentialCreated apiCredentialCreated;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO console_credenciais(client_id,credenciador_id,nome,secret_hash,secret_salt,tipo,permissoes_json,ativo,criado_em,atualizado_em) VALUES($id,$cred,$nome,$hash,$salt,$tipo,$permissoes,1,$now,$now);";
                Add(command, "$id", clientId);
                Add(command, "$cred", "facilapp-sql-master");
                Add(command, "$nome", request.Nome);
                Add(command, "$hash", hash);
                Add(command, "$salt", salt);
                Add(command, "$tipo", request.Tipo);
                Add(command, "$permissoes", JsonSerializer.Serialize(request.Permissoes.Distinct<string>(StringComparer.OrdinalIgnoreCase)));
                Add(command, "$now", now);
                await command.ExecuteNonQueryAsync(cancellationToken);
                apiCredentialCreated = new ApiCredentialCreated(new ApiCredentialView(clientId, request.Nome.Trim(), request.Tipo.Trim(), request.Permissoes, Ativo: true, now), secret);
            }
            result = apiCredentialCreated;
        }
        return result;
    }

    public async Task<ApiCredentialView?> UpdateCredentialAsync(string clientId, ApiCredentialRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(request.Nome) || request.Permissoes.Count == 0)
        {
            throw new InvalidDataException("Client ID, nome e permissões são obrigatórios.");
        }

        string[] permissoes = request.Permissoes
            .Where(permissao => !string.IsNullOrWhiteSpace(permissao))
            .Select(permissao => permissao.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string now = DateTimeOffset.Now.ToString("O");

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE console_credenciais SET nome=$nome,tipo=$tipo,permissoes_json=$permissoes,atualizado_em=$now WHERE client_id=$id AND ativo=1;";
        Add(command, "$nome", request.Nome.Trim());
        Add(command, "$tipo", request.Tipo.Trim());
        Add(command, "$permissoes", JsonSerializer.Serialize(permissoes));
        Add(command, "$now", now);
        Add(command, "$id", clientId.Trim());
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return await GetActiveCredentialAsync(clientId, cancellationToken);
    }

    public async Task<bool> RevokeCredentialAsync(string clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }
        bool result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            bool flag;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE console_credenciais SET ativo=0, atualizado_em=$now WHERE client_id=$id AND ativo=1;";
                Add(command, "$id", clientId.Trim());
                Add(command, "$now", DateTimeOffset.Now.ToString("O"));
                flag = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            }
            result = flag;
        }
        return result;
    }

    public async Task<ApiCredentialView?> ValidateCredentialAsync(string clientId, string secret, CancellationToken cancellationToken)
    {
        ApiCredentialView result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            ApiCredentialView apiCredentialView2;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT nome,tipo,permissoes_json,ativo,criado_em,secret_hash,secret_salt FROM console_credenciais WHERE client_id=$id AND ativo=1 LIMIT 1;";
                Add(command, "$id", clientId);
                ApiCredentialView apiCredentialView;
                await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    if (!(await reader.ReadAsync(cancellationToken)))
                    {
                        apiCredentialView = null;
                    }
                    else
                    {
                        byte[] array = Rfc2898DeriveBytes.Pbkdf2(secret, (byte[])reader[6], 210000, HashAlgorithmName.SHA256, 32);
                        apiCredentialView = (CryptographicOperations.FixedTimeEquals(array, (byte[])reader[5]) ? new ApiCredentialView(clientId, reader.GetString(0), reader.GetString(1), JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? Array.Empty<string>(), reader.GetInt32(3) == 1, reader.GetString(4)) : null);
                    }
                }
                apiCredentialView2 = apiCredentialView;
            }
            result = apiCredentialView2;
        }
        return result;
    }

    public async Task<ApiCredentialView?> GetActiveCredentialAsync(string clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }
        ApiCredentialView result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            ApiCredentialView apiCredentialView2;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT client_id,nome,tipo,permissoes_json,ativo,criado_em FROM console_credenciais WHERE client_id=$id AND ativo=1 LIMIT 1;";
                Add(command, "$id", clientId.Trim());
                ApiCredentialView apiCredentialView;
                await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    apiCredentialView = ((await reader.ReadAsync(cancellationToken)) ? ReadCredential(reader) : null);
                }
                apiCredentialView2 = apiCredentialView;
            }
            result = apiCredentialView2;
        }
        return result;
    }

    /// <summary>Obtém a empresa vinculada à credencial para retorno de login por Client ID/Secret.</summary>
    public async Task<ClientCredentialContext> GetCredentialContextAsync(string clientId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT u.empresa_id,e.cnpj FROM console_usuarios u INNER JOIN console_empresas e ON e.id=u.empresa_id WHERE u.client_id=$client AND u.ativo=1 AND e.ativo=1 ORDER BY u.criado_em LIMIT 1;";
        Add(command, "$client", clientId.Trim());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ClientCredentialContext(reader.GetString(0), reader.GetString(1))
            : new ClientCredentialContext(string.Empty, string.Empty);
    }

    /// <summary>
    /// Lista usuários locais e suas credenciais OAuth, sem retornar dados de senha.
    /// </summary>
    public async Task<IReadOnlyList<LocalUserView>> ListLocalUsersAsync(
        string companyId,
        CancellationToken cancellationToken)
    {
        List<LocalUserView> users = new List<LocalUserView>();
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
			SELECT u.id,u.empresa_id,u.sistema_id,e.razao_social,e.cnpj,u.nome,u.usuario,u.client_id,c.nome,u.ativo,u.criado_em,
			       u.menu_data,u.menu_superior_data,u.dashboard_data
			FROM console_usuarios u
			INNER JOIN console_empresas e ON e.id=u.empresa_id
			INNER JOIN console_credenciais c ON c.client_id=u.client_id
			WHERE u.empresa_id=$empresa AND u.ativo=1 AND e.ativo=1
			ORDER BY u.nome;
			""";
        Add(command, "$empresa", companyId.Trim());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(ReadLocalUser(reader));
        }

        return users;
    }

    /// <summary>
    /// Cria um usuário local vinculado a uma credencial OAuth ativa ou reativa o
    /// cadastro inativo que possua o mesmo login na empresa.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Gerada quando os dados são inválidos, já existe um usuário ativo ou a credencial está inativa.
    /// </exception>
    public async Task<LocalUserView> CreateLocalUserAsync(
        Models.LocalUserRequest request,
        CancellationToken cancellationToken)
    {
        string name = request.Nome.Trim();
        string userName = request.Usuario.Trim();
        string clientId = request.ClientId.Trim();
        string companyId = request.EmpresaId.Trim();
        string systemId = string.IsNullOrWhiteSpace(request.SistemaId) ? "curso" : request.SistemaId.Trim().ToLowerInvariant();
        if (companyId.Length == 0 || name.Length == 0 || userName.Length < 3 || request.Senha.Length == 0 || clientId.Length == 0)
        {
            throw new InvalidDataException(
                "Informe empresa, nome, usuário com ao menos 3 caracteres, uma senha e uma credencial.");
        }

        await using (SqliteConnection companyConnection = await OpenAsync(cancellationToken))
        await using (SqliteCommand companyCommand = companyConnection.CreateCommand())
        {
            companyCommand.CommandText = "SELECT COUNT(*) FROM console_empresas WHERE id=$id AND ativo=1;";
            Add(companyCommand, "$id", companyId);
            if (Convert.ToInt32(await companyCommand.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                throw new InvalidDataException("A empresa selecionada não existe ou está inativa.");
            }
        }

        await using (SqliteConnection systemConnection = await OpenAsync(cancellationToken))
        await using (SqliteCommand systemCommand = systemConnection.CreateCommand())
        {
            systemCommand.CommandText = "SELECT COUNT(*) FROM console_sistemas WHERE id=$id AND ativo=1;";
            Add(systemCommand, "$id", systemId);
            if (Convert.ToInt32(await systemCommand.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new InvalidDataException("O sistema selecionado não existe ou está inativo.");
        }

        if (await GetActiveCredentialAsync(clientId, cancellationToken) == null)
        {
            throw new InvalidDataException("A credencial selecionada não existe ou está inativa.");
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            request.Senha,
            salt,
            210000,
            HashAlgorithmName.SHA256,
            32);
        string id = Guid.NewGuid().ToString("D");
        string now = DateTimeOffset.Now.ToString("O");
        string menuData = NormalizeJsonArray(request.MenuData, "MenuData");
        string menuSuperiorData = NormalizeJsonArray(request.MenuSuperiorData, "MenuSuperiorData");
        string dashBoardData = NormalizeJsonArray(request.DashBoardData, "DashBoardData");

        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO console_usuarios(
                    id,empresa_id,sistema_id,nome,usuario,senha_hash,senha_salt,client_id,menu_data,menu_superior_data,dashboard_data,ativo,criado_em,atualizado_em)
                VALUES($id,$empresa,$sistema,$nome,$usuario,$hash,$salt,$client,$menuData,$menuSuperiorData,$dashBoardData,1,$now,$now)
                ON CONFLICT(sistema_id,empresa_id,usuario) DO UPDATE SET
                    nome=excluded.nome,
                    senha_hash=excluded.senha_hash,
                    senha_salt=excluded.senha_salt,
                    client_id=excluded.client_id,
                    menu_data=excluded.menu_data,
                    menu_superior_data=excluded.menu_superior_data,
                    dashboard_data=excluded.dashboard_data,
                    ativo=1,
                    atualizado_em=excluded.atualizado_em
                WHERE console_usuarios.ativo=0
                RETURNING id;
                """;
            Add(command, "$id", id);
            Add(command, "$empresa", companyId);
            Add(command, "$sistema", systemId);
            Add(command, "$nome", name);
            Add(command, "$usuario", userName);
            Add(command, "$hash", hash);
            Add(command, "$salt", salt);
            Add(command, "$client", clientId);
            Add(command, "$menuData", menuData);
            Add(command, "$menuSuperiorData", menuSuperiorData);
            Add(command, "$dashBoardData", dashBoardData);
            Add(command, "$now", now);
            object? savedId = await command.ExecuteScalarAsync(cancellationToken);
            if (savedId == null || savedId == DBNull.Value)
            {
                await using SqliteCommand existingCommand = connection.CreateCommand();
                existingCommand.CommandText = """
					SELECT id,nome,client_id,senha_hash,senha_salt
					FROM console_usuarios
					WHERE sistema_id=$sistema AND empresa_id=$empresa AND usuario=$usuario AND ativo=1
					LIMIT 1;
					""";
                Add(existingCommand, "$empresa", companyId);
                Add(existingCommand, "$sistema", systemId);
                Add(existingCommand, "$usuario", userName);
                await using SqliteDataReader existingReader = await existingCommand.ExecuteReaderAsync(cancellationToken);
                if (!await existingReader.ReadAsync(cancellationToken))
                {
                    throw new InvalidDataException("Não foi possível confirmar o cadastro do usuário.");
                }

                byte[] existingHash = (byte[])existingReader["senha_hash"];
                byte[] existingSalt = (byte[])existingReader["senha_salt"];
                byte[] submittedHash = Rfc2898DeriveBytes.Pbkdf2(
                    request.Senha,
                    existingSalt,
                    210000,
                    HashAlgorithmName.SHA256,
                    32);
                bool sameSubmission = string.Equals(existingReader.GetString(1), name, StringComparison.Ordinal)
                    && string.Equals(existingReader.GetString(2), clientId, StringComparison.Ordinal)
                    && CryptographicOperations.FixedTimeEquals(existingHash, submittedHash);
                if (!sameSubmission)
                {
                    throw new InvalidDataException("Já existe um usuário ativo com esse login nesta empresa.");
                }

                id = existingReader.GetString(0);
            }
            else
            {
                id = Convert.ToString(savedId) ?? id;
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidDataException("Já existe um usuário com esse login nesta empresa.", exception);
        }

        return (await ListLocalUsersAsync(companyId, cancellationToken)).First(user => user.Id == id);
    }

    /// <summary>
    /// Exclui definitivamente um usuário local e impede imediatamente novas autenticações.
    /// </summary>
    public async Task<bool> DeleteLocalUserAsync(string id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
			DELETE FROM console_usuarios
			WHERE id=$id;
			""";
        Add(command, "$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Lista os credenciadores cadastrados para o browse administrativo.</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListCredentialersAsync(CancellationToken cancellationToken)
    {
        List<Dictionary<string, object?>> items = new();
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,nome,email,tipo,situacao,criado_em FROM console_credenciadores ORDER BY nome;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new Dictionary<string, object?>
            {
                ["id"] = reader.GetString(0),
                ["nome"] = reader.GetString(1),
                ["email"] = reader.GetString(2),
                ["tipo"] = reader.GetString(3),
                ["situacao"] = reader.GetString(4),
                ["criado_em"] = reader.GetString(5)
            });
        }
        return items;
    }

    /// <summary>Lista os sistemas cadastrados e ativos/inativos da API.</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListSystemsAsync(CancellationToken cancellationToken)
    {
        var items = new List<Dictionary<string, object?>>();
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,nome,ativo,criado_em,atualizado_em FROM console_sistemas ORDER BY nome;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new Dictionary<string, object?>
            {
                ["id"] = reader.GetString(0),
                ["nome"] = reader.GetString(1),
                ["ativo"] = reader.GetInt32(2) == 1,
                ["criado_em"] = reader.GetString(3),
                ["atualizado_em"] = reader.GetString(4)
            });
        }
        return items;
    }

    /// <summary>Altera nome e situação de um sistema, mantendo seu código estável.</summary>
    public async Task<bool> UpdateSystemAsync(string id, Models.SystemRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidDataException("Informe o nome do sistema.");
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE console_sistemas SET nome=$nome,ativo=$ativo,atualizado_em=$quando WHERE id=$id;";
        Add(command, "$nome", request.Name.Trim());
        Add(command, "$ativo", request.Active ? 1 : 0);
        Add(command, "$quando", DateTimeOffset.Now.ToString("O"));
        Add(command, "$id", id.Trim().ToLowerInvariant());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Cadastra um sistema com código estável, sempre normalizado em minúsculas.</summary>
    public async Task<Dictionary<string, object?>> CreateSystemAsync(Models.SystemRequest request, CancellationToken cancellationToken)
    {
        string id = (request.Id ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Informe o código do sistema.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9_-]+$"))
            throw new InvalidDataException("O código aceita somente letras minúsculas, números, hífen e sublinhado.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidDataException("Informe o nome do sistema.");
        string now = DateTimeOffset.Now.ToString("O");
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO console_sistemas(id,nome,ativo,criado_em,atualizado_em) VALUES($id,$nome,$ativo,$quando,$quando);";
        Add(command, "$id", id);
        Add(command, "$nome", request.Name.Trim());
        Add(command, "$ativo", request.Active ? 1 : 0);
        Add(command, "$quando", now);
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        { throw new InvalidDataException("Já existe um sistema com esse código."); }
        return new Dictionary<string, object?> { ["id"] = id, ["nome"] = request.Name.Trim(), ["ativo"] = request.Active, ["criado_em"] = now, ["atualizado_em"] = now };
    }

    /// <summary>Desativa um sistema sem apagar usuários ou históricos vinculados.</summary>
    public async Task<bool> DisableSystemAsync(string id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE console_sistemas SET ativo=0,atualizado_em=$quando WHERE id=$id;";
        Add(command, "$quando", DateTimeOffset.Now.ToString("O"));
        Add(command, "$id", id.Trim().ToLowerInvariant());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Substitui a senha de um usuário ativo pertencente à empresa informada.
    /// </summary>
    /// <param name="companyId">Identificador interno da empresa proprietária.</param>
    /// <param name="userId">Identificador interno do usuário.</param>
    /// <param name="password">Nova senha em texto puro, mantida apenas durante o cálculo do hash.</param>
    /// <param name="cancellationToken">Token de cancelamento da operação.</param>
    /// <returns>Verdadeiro quando exatamente um usuário ativo foi atualizado.</returns>
    /// <exception cref="InvalidDataException">Gerada quando a nova senha está vazia.</exception>
    public async Task<bool> UpdateLocalUserPasswordAsync(
        string companyId,
        string userId,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidDataException("Informe a nova senha.");
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            210000,
            HashAlgorithmName.SHA256,
            32);

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE console_usuarios
            SET senha_hash=$hash, senha_salt=$salt, atualizado_em=$now
            WHERE id=$id AND empresa_id=$empresa AND ativo=1;
            """;
        Add(command, "$hash", hash);
        Add(command, "$salt", salt);
        Add(command, "$now", DateTimeOffset.Now.ToString("O"));
        Add(command, "$id", userId.Trim());
        Add(command, "$empresa", companyId.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Atualiza os dados administrativos de um Login da empresa. Quando a senha vem vazia,
    /// o hash atual é preservado para que a alteração de nome ou credencial não invalide o acesso.
    /// </summary>
    /// <param name="companyId">Identificador interno da empresa proprietária.</param>
    /// <param name="userId">Identificador interno do Login.</param>
    /// <param name="request">Dados revisados enviados pelo console.</param>
    /// <param name="cancellationToken">Token de cancelamento da operação.</param>
    /// <returns>Verdadeiro quando um Login ativo da empresa foi atualizado.</returns>
    public async Task<bool> UpdateLocalUserAsync(
        string companyId,
        string userId,
        Models.LocalUserRequest request,
        CancellationToken cancellationToken)
    {
        string name = request.Nome.Trim();
        string userName = request.Usuario.Trim();
        string clientId = request.ClientId.Trim();
        string systemId = string.IsNullOrWhiteSpace(request.SistemaId) ? "curso" : request.SistemaId.Trim().ToLowerInvariant();
        string menuData = NormalizeJsonArray(request.MenuData, "MenuData");
        string menuSuperiorData = NormalizeJsonArray(request.MenuSuperiorData, "MenuSuperiorData");
        string dashBoardData = NormalizeJsonArray(request.DashBoardData, "DashBoardData");
        if (name.Length == 0 || userName.Length < 3 || clientId.Length == 0)
        {
            throw new InvalidDataException("Informe nome, usuário e credencial SQL.");
        }

        if (await GetActiveCredentialAsync(clientId, cancellationToken) is null)
        {
            throw new InvalidDataException("A credencial SQL selecionada não existe ou está inativa.");
        }

        byte[]? salt = null;
        byte[]? hash = null;
        if (!string.IsNullOrEmpty(request.Senha))
        {
            salt = RandomNumberGenerator.GetBytes(16);
            hash = Rfc2898DeriveBytes.Pbkdf2(request.Senha, salt, 210000, HashAlgorithmName.SHA256, 32);
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE console_usuarios
            SET sistema_id=$sistema,nome=$nome, usuario=$usuario, client_id=$client,
                menu_data=$menuData, menu_superior_data=$menuSuperiorData, dashboard_data=$dashBoardData,
                senha_hash=COALESCE($hash,senha_hash), senha_salt=COALESCE($salt,senha_salt), atualizado_em=$now
            WHERE id=$id AND empresa_id=$empresa AND ativo=1;
            """;
        Add(command, "$nome", name);
        Add(command, "$usuario", userName);
        Add(command, "$client", clientId);
        Add(command, "$sistema", systemId);
        Add(command, "$menuData", menuData);
        Add(command, "$menuSuperiorData", menuSuperiorData);
        Add(command, "$dashBoardData", dashBoardData);
        Add(command, "$hash", (object?)hash ?? DBNull.Value);
        Add(command, "$salt", (object?)salt ?? DBNull.Value);
        Add(command, "$now", DateTimeOffset.Now.ToString("O"));
        Add(command, "$id", userId.Trim());
        Add(command, "$empresa", companyId.Trim());
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidDataException("Já existe um usuário com esse login nesta empresa.", exception);
        }
    }

    /// <summary>
    /// Consulta os menus cadastrados de um usuário sem expor dados de senha ou credencial.
    /// </summary>
    public async Task<LocalUserMenuView?> GetLocalUserMenusAsync(
        string companyId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,empresa_id,menu_data,menu_superior_data,dashboard_data FROM console_usuarios WHERE id=$id AND empresa_id=$empresa AND ativo=1 LIMIT 1;";
        Add(command, "$id", userId);
        Add(command, "$empresa", companyId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new LocalUserMenuView(
            reader.GetString(0),
            reader.GetString(1),
            Text(reader, 2) ?? "[]",
            Text(reader, 3) ?? "[]",
            Text(reader, 4) ?? "[]");
    }

    /// <summary>
    /// Grava os menus e dashboards de um usuário da empresa informada.
    /// </summary>
    public async Task<bool> UpdateLocalUserMenusAsync(
        string companyId,
        string userId,
        Models.LocalUserMenuRequest request,
        CancellationToken cancellationToken)
    {
        string menuData = NormalizeJsonArray(request.MenuData, "MenuData");
        string menuSuperiorData = NormalizeJsonArray(request.MenuSuperiorData, "MenuSuperiorData");
        string dashBoardData = NormalizeJsonArray(request.DashBoardData, "DashBoardData");
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE console_usuarios SET menu_data=$menuData,menu_superior_data=$menuSuperiorData,dashboard_data=$dashBoardData,atualizado_em=$now WHERE id=$id AND empresa_id=$empresa AND ativo=1;";
        Add(command, "$menuData", menuData);
        Add(command, "$menuSuperiorData", menuSuperiorData);
        Add(command, "$dashBoardData", dashBoardData);
        Add(command, "$now", DateTimeOffset.Now.ToString("O"));
        Add(command, "$id", userId);
        Add(command, "$empresa", companyId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Compatibilidade do login simples: autentica somente por usuário e senha.</summary>
    public Task<LocalUserAuthentication?> AuthenticateLocalUserAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        return AuthenticateLocalUserAsync(userName, password, null, null, cancellationToken, null, allowWithoutContext: true);
    }

    /// <summary>Login sem contexto de empresa/sistema, mantido somente para clientes legados.</summary>
    public Task<LocalUserAuthentication?> AuthenticateSimpleLocalUserAsync(string userName, string password, CancellationToken cancellationToken)
    {
        return AuthenticateLocalUserAsync(userName, password, null, null, cancellationToken, null, allowWithoutContext: true);
    }

    /// <summary>
    /// Valida usuário e senha localmente e resolve internamente a empresa e a credencial OAuth vinculadas.
    /// </summary>
    /// <param name="userName">Login cadastrado para o usuário.</param>
    /// <param name="password">Senha em texto puro recebida somente para validação.</param>
    /// <param name="cancellationToken">Token de cancelamento da operação.</param>
    /// <returns>A primeira autenticação válida encontrada; caso contrário, nulo.</returns>
    public async Task<LocalUserAuthentication?> AuthenticateLocalUserAsync(
        string userName,
        string password,
        string? companyId,
        string? cnpj,
        CancellationToken cancellationToken,
        string? systemId = null,
        bool allowWithoutContext = false)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)
            || (!allowWithoutContext && string.IsNullOrWhiteSpace(systemId)))
        {
            return null;
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        string normalizedCompanyId = companyId?.Trim() ?? string.Empty;
        string normalizedCnpj = new string((cnpj ?? string.Empty).Where(char.IsDigit).ToArray());
        string normalizedSystem = (systemId ?? string.Empty).Trim().ToLowerInvariant();
        if (!allowWithoutContext && normalizedCompanyId.Length == 0 && normalizedCnpj.Length == 0) return null;
        command.CommandText = "SELECT u.id,u.empresa_id,u.sistema_id,e.razao_social,e.cnpj,u.nome,u.usuario,u.client_id,c.nome,u.ativo,u.criado_em, " +
                              "u.menu_data,u.menu_superior_data,u.dashboard_data,u.senha_hash,u.senha_salt,c.tipo,c.permissoes_json,c.ativo,c.criado_em " +
                              "FROM console_usuarios u INNER JOIN console_empresas e ON e.id=u.empresa_id " +
                              "INNER JOIN console_credenciais c ON c.client_id=u.client_id " +
                              "WHERE e.ativo=1 AND u.usuario=$usuario COLLATE NOCASE AND u.ativo=1 AND c.ativo=1 " +
                              "AND ($empresa='' OR u.empresa_id=$empresa) AND ($cnpj='' OR e.cnpj=$cnpj) " +
                              "AND ($sistema='' OR u.sistema_id=$sistema) ORDER BY u.criado_em;";
        Add(command, "$usuario", userName);
        Add(command, "$empresa", normalizedCompanyId);
        Add(command, "$cnpj", normalizedCnpj);
        Add(command, "$sistema", normalizedSystem);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            byte[] candidateHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                (byte[])reader[15],
                210000,
                HashAlgorithmName.SHA256,
                32);
            if (!CryptographicOperations.FixedTimeEquals(candidateHash, (byte[])reader[14]))
            {
                continue;
            }

            LocalUserView user = ReadLocalUser(reader);
            ApiCredentialView credential = new ApiCredentialView(
                user.ClientId,
                user.CredencialNome,
                reader.GetString(16),
                JsonSerializer.Deserialize<string[]>(reader.GetString(17)) ?? Array.Empty<string>(),
                reader.GetInt32(18) == 1,
                reader.GetString(19));
            CompanyPaymentStatus payment = await GetCompanyPaymentStatusAsync(user.EmpresaId, cancellationToken)
                ?? new CompanyPaymentStatus("mensalidade", false, "empresa_inativa", null, null, 0);
            return new LocalUserAuthentication(user, credential, payment);
        }

        return null;
    }

    public async Task<object> GetDashboardAsync(CancellationToken cancellationToken)
    {
        object result;
        await using (SqliteConnection connection = await OpenAsync(cancellationToken))
        {
            object obj2;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT (SELECT COUNT(*) FROM console_empresas WHERE ativo=1),(SELECT COUNT(*) FROM console_credenciais WHERE ativo=1),(SELECT COUNT(*) FROM console_conexoes WHERE ativo=1);";
                object obj;
                await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    await reader.ReadAsync(cancellationToken);
                    obj = new
                    {
                        empresasAtivas = reader.GetInt32(0),
                        credenciaisAtivas = reader.GetInt32(1),
                        conexoesAtivas = reader.GetInt32(2)
                    };
                }
                obj2 = obj;
            }
            result = obj2;
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
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            result = connection;
        }
        return result;
    }

    private static CompanyView ReadCompany(SqliteDataReader reader)
    {
        return new CompanyView(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), Text(reader, 5), Text(reader, 6), Text(reader, 7), Text(reader, 8), reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetString(13), reader.GetString(14), Text(reader, 15), reader.GetString(16), reader.GetString(17), reader.GetString(18))
        {
            Genero = Text(reader, 19), DataNascimento = Text(reader, 20), NomeMae = Text(reader, 21),
            Idade = reader.IsDBNull(22) ? null : reader.GetInt32(22), Zodiaco = Text(reader, 23),
            TelefonesJson = Text(reader, 24), EnderecosJson = Text(reader, 25), EmailsJson = Text(reader, 26),
            SalarioEstimado = Text(reader, 27), StatusCadastral = Text(reader, 28), DataStatusCadastral = Text(reader, 29),
            UltimaAtualizacaoFonte = Text(reader, 30), ConsultaJson = Text(reader, 31),
            TipoPagamento = NormalizePaymentType(Text(reader, 32))
        };
    }

    private static CompanyLogo ParseCompanyLogo(string dataUrl)
    {
        const int maximumBytes = 1024 * 1024;
        string mimeType;
        string prefix;
        if (dataUrl.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase))
        {
            mimeType = "image/png";
            prefix = "data:image/png;base64,";
        }
        else if (dataUrl.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase))
        {
            mimeType = "image/jpeg";
            prefix = "data:image/jpeg;base64,";
        }
        else if (dataUrl.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase))
        {
            mimeType = "image/webp";
            prefix = "data:image/webp;base64,";
        }
        else
        {
            throw new InvalidDataException("A logo deve ser PNG, JPG ou WebP em Base64.");
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(dataUrl[prefix.Length..]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("A imagem da logo está inválida.", exception);
        }

        if (data.Length == 0 || data.Length > maximumBytes)
        {
            throw new InvalidDataException("A logo deve possuir entre 1 byte e 1 MB.");
        }

        return new CompanyLogo(mimeType, data);
    }

    private static ApiCredentialView ReadCredential(SqliteDataReader reader)
    {
        return new ApiCredentialView(reader.GetString(0), reader.GetString(1), reader.GetString(2), JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? Array.Empty<string>(), reader.GetInt32(4) == 1, reader.GetString(5));
    }

    private static LocalUserView ReadLocalUser(SqliteDataReader reader)
    {
        return new LocalUserView(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt32(9) == 1,
            reader.GetString(10),
            Text(reader, 11) ?? "[]",
            Text(reader, 12) ?? "[]",
            Text(reader, 13) ?? "[]");
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = $"PRAGMA table_info({tableName});";
        await using SqliteDataReader reader = await schemaCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await using SqliteCommand alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Catálogo de campos da versão atual. Toda alteração estrutural do banco
    /// interno deve entrar aqui para atualizar instalações antigas no próximo
    /// início do serviço, sem copiar ou sobrescrever o arquivo SQLite.
    /// </summary>
    private static async Task EnsureCurrentSchemaColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        (string Table, string Column, string Definition)[] fields =
        [
            ("console_sistemas", "id", "TEXT"),
            ("console_sistemas", "nome", "TEXT NOT NULL DEFAULT ''"),
            ("console_sistemas", "ativo", "INTEGER NOT NULL DEFAULT 1"),
            ("console_sistemas", "criado_em", "TEXT NOT NULL DEFAULT ''"),
            ("console_sistemas", "atualizado_em", "TEXT NOT NULL DEFAULT ''"),
            ("console_credenciadores", "nome", "TEXT NOT NULL DEFAULT ''"),
            ("console_credenciadores", "email", "TEXT NOT NULL DEFAULT ''"),
            ("console_credenciadores", "tipo", "TEXT NOT NULL DEFAULT ''"),
            ("console_credenciadores", "situacao", "TEXT NOT NULL DEFAULT 'ATIVO'"),
            ("console_credenciadores", "criado_em", "TEXT NOT NULL DEFAULT ''"),
            ("console_credenciadores", "atualizado_em", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "credenciador_id", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "tipo_pessoa", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "cnpj", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "email", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "razao_social", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "nome_fantasia", "TEXT"),
            ("console_empresas", "inscricao_estadual", "TEXT"),
            ("console_empresas", "inscricao_municipal", "TEXT"),
            ("console_empresas", "telefone", "TEXT"),
            ("console_empresas", "cep", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "codigo_ibge", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "uf", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "cidade", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "endereco", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "numero", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "complemento", "TEXT"),
            ("console_empresas", "bairro", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "ativo", "INTEGER NOT NULL DEFAULT 1"),
            ("console_empresas", "criado_em", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "atualizado_em", "TEXT NOT NULL DEFAULT ''"),
            ("console_empresas", "genero", "TEXT"),
            ("console_empresas", "data_nascimento", "TEXT"),
            ("console_empresas", "nome_mae", "TEXT"),
            ("console_empresas", "idade", "INTEGER"),
            ("console_empresas", "zodiaco", "TEXT"),
            ("console_empresas", "telefones_json", "TEXT"),
            ("console_empresas", "enderecos_json", "TEXT"),
            ("console_empresas", "emails_json", "TEXT"),
            ("console_empresas", "salario_estimado", "TEXT"),
            ("console_empresas", "status_cadastral", "TEXT"),
            ("console_empresas", "data_status_cadastral", "TEXT"),
            ("console_empresas", "ultima_atualizacao_fonte", "TEXT"),
            ("console_empresas", "consulta_json", "TEXT"),
            ("console_empresas", "tipo_pagamento", "TEXT NOT NULL DEFAULT 'mensalidade'"),
            ("console_credenciais", "credenciador_id", "TEXT NOT NULL DEFAULT ''"),
            ("console_credenciais", "nome", "TEXT NOT NULL DEFAULT ''"),
            ("console_credenciais", "secret_hash", "BLOB"),
            ("console_credenciais", "secret_salt", "BLOB"),
            ("console_credenciais", "tipo", "TEXT NOT NULL DEFAULT ''"),
            ("console_credenciais", "permissoes_json", "TEXT NOT NULL DEFAULT '[]'"),
            ("console_credenciais", "ativo", "INTEGER NOT NULL DEFAULT 1"),
            ("console_credenciais", "criado_em", "TEXT NOT NULL DEFAULT ''"),
            ("console_credenciais", "atualizado_em", "TEXT NOT NULL DEFAULT ''"),
            ("console_usuarios", "empresa_id", "TEXT NOT NULL DEFAULT ''"),
            ("console_usuarios", "sistema_id", "TEXT NOT NULL DEFAULT 'curso'"),
            ("console_usuarios", "nome", "TEXT NOT NULL DEFAULT ''"),
            ("console_usuarios", "usuario", "TEXT NOT NULL DEFAULT ''"),
            ("console_usuarios", "senha_hash", "BLOB"),
            ("console_usuarios", "senha_salt", "BLOB"),
            ("console_usuarios", "client_id", "TEXT NOT NULL DEFAULT ''"),
            ("console_usuarios", "menu_data", "TEXT NOT NULL DEFAULT '[]'"),
            ("console_usuarios", "menu_superior_data", "TEXT NOT NULL DEFAULT '[]'"),
            ("console_usuarios", "dashboard_data", "TEXT NOT NULL DEFAULT '[]'"),
            ("console_usuarios", "ativo", "INTEGER NOT NULL DEFAULT 1"),
            ("console_usuarios", "criado_em", "TEXT NOT NULL DEFAULT ''"),
            ("console_usuarios", "atualizado_em", "TEXT NOT NULL DEFAULT ''")
        ];
        foreach ((string table, string column, string definition) in fields)
        {
            await EnsureColumnAsync(connection, table, column, definition, cancellationToken);
        }
    }


    private static string NormalizeJsonArray(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return "[]";
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"{fieldName} deve conter uma lista JSON.");
            }

            return document.RootElement.GetRawText();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{fieldName} contém JSON inválido.", exception);
        }
    }

    private static string? Text(SqliteDataReader reader, int index)
    {
        if (!reader.IsDBNull(index))
        {
            return reader.GetString(index);
        }
        return null;
    }

    private static string NormalizePaymentType(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "0" or "mensalidade" => "mensalidade",
            "vitalicio" or "vitalícia" or "vitalicia" => "vitalicio",
            _ => throw new InvalidDataException("Tipo de pagamento deve ser vitalicio ou mensalidade.")
        };
    }

    private static string OnlyDigits(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
    }

    private static string RandomToken(int bytes)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', '-').Replace('/', '_')
            .TrimEnd('=');
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, (value is string text) ? text.Trim() : (value ?? DBNull.Value));
    }
}
