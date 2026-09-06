using FacilApp.Sql.Api.Configuration;
using FacilApp.Sql.Api.Models;
using FacilApp.Sql.Api.Services;
using Microsoft.Data.Sqlite;

string root = Path.Combine(Path.GetTempPath(), "facilapp-schema-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string ini = Path.Combine(root, "FacilAppSQL.ini");
    string scriptsDirectory = Path.Combine(root, "Scripts");
    await File.WriteAllTextAsync(ini, $"[SQLite]\nDiretorio=BasesSQLite\nBanco=smoke.db\n[ServidorHTTP]\nPorta=5050\nToken=teste\nTimeoutSegundos=30\n[Scripts]\nAtivo=1\nDiretorio={scriptsDirectory}\nTimeoutSegundos=30\n");
    var service = new DatabaseService(new IniConfiguration(ini));
    var initial = new List<MultibankColumnDefinition>
    {
        new() { Name = "id", Type = "inteiro", PrimaryKey = true, AutoIncrement = true, Nullable = false },
        new() { Name = "nome", Type = "texto_curto", Length = 80, Nullable = false }
    };
    await service.EnsureStructureAsync("sqlite", "", "smoke.db", "subcategorias", initial, CancellationToken.None);
    initial.Add(new MultibankColumnDefinition { Name = "ativo", Type = "booleano", Nullable = true });
    await service.EnsureStructureAsync("sqlite", "", "smoke.db", "subcategorias", initial, CancellationToken.None);
    var columns = await service.ListColumnsAsync("sqlite", "", "smoke.db", "", "subcategorias", CancellationToken.None);
    if (columns.Count != 3 || !columns.Any(row => Convert.ToString(row["campo"]) == "ativo"))
        throw new Exception("Estrutura SQLite não foi criada/atualizada corretamente.");
    Console.WriteLine("OK: tabela criada e campo ausente adicionado.");

    // Simula uma instalação antiga cujo banco interno já contém empresas,
    // mas ainda não possui o campo de mensalidade nem a tabela de pagamentos.
    string consoleDirectory = Path.Combine(root, "Dados");
    Directory.CreateDirectory(consoleDirectory);
    string consoleDatabase = Path.Combine(consoleDirectory, "facilapp_sql.db");
    await using (SqliteConnection legacy = new($"Data Source={consoleDatabase}"))
    {
        await legacy.OpenAsync();
        await using SqliteCommand legacySchema = legacy.CreateCommand();
        legacySchema.CommandText = "CREATE TABLE console_empresas(id TEXT PRIMARY KEY, cnpj TEXT, ativo INTEGER); INSERT INTO console_empresas VALUES('empresa-legada','123',1);";
        await legacySchema.ExecuteNonQueryAsync();
    }
    ConsoleStore consoleStore = new(new IniConfiguration(ini));
    await consoleStore.InitializeAsync();
    await using (SqliteConnection migrated = new($"Data Source={consoleDatabase}"))
    {
        await migrated.OpenAsync();
        await using SqliteCommand checkMigration = migrated.CreateCommand();
        checkMigration.CommandText = "SELECT (SELECT COUNT(*) FROM pragma_table_info('console_empresas') WHERE name='tipo_pagamento'), (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='console_pagamentos');";
        await using SqliteDataReader migrationReader = await checkMigration.ExecuteReaderAsync();
        await migrationReader.ReadAsync();
        if (migrationReader.GetInt64(0) != 1 || migrationReader.GetInt64(1) != 1)
            throw new Exception("Migração de mensalidades não atualizou o banco antigo.");
    }
    Console.WriteLine("OK: banco antigo recebeu campo de mensalidade e histórico de pagamentos.");
    Directory.CreateDirectory(scriptsDirectory);
    string scriptPath = Path.Combine(scriptsDirectory, "Teste-Controlado.ps1");
    await File.WriteAllTextAsync(scriptPath, "Write-Output 'PS1_OK'\nexit 0\n");
    object scriptResult = await new PowerShellScriptService(new IniConfiguration(ini))
        .ExecuteAsync(scriptPath, CancellationToken.None);
    if (!scriptResult.ToString()!.Contains("PS1_OK", StringComparison.Ordinal))
        throw new Exception("O PS1 autorizado não foi executado.");
    Console.WriteLine("OK: PS1 existente executado dentro da pasta autorizada.");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}
