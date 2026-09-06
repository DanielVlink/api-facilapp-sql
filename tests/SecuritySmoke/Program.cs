using System.Text.Json;
using FacilApp.Sql.Api.Configuration;
using FacilApp.Sql.Api.Models;
using FacilApp.Sql.Api.Services;
using Microsoft.Data.Sqlite;

string testDirectory = Path.Combine(Path.GetTempPath(), $"FacilAppSqlSecurity-{Guid.NewGuid():N}");
Directory.CreateDirectory(testDirectory);

try
{
    string iniPath = Path.Combine(testDirectory, "FacilAppSQL.ini");
    await File.WriteAllTextAsync(iniPath, """
        [ServidorHTTP]
        Porta=5059
        Portas=5059
        TimeoutSegundos=10

        [Seguranca]
        Token=

        [SQLite]
        Ativo=1
        Banco=login.db
        Diretorio=BasesSQLite
        """);

    IniConfiguration configuration = new(iniPath);
    ConsoleStore store = new(configuration);
    await store.InitializeAsync();

    CompanyView company = await store.SaveCompanyAsync(
        new CompanyRequest(
            "juridica",
            "57202784000158",
            "teste@empresa.local",
            "Empresa de Teste Ltda",
            "Empresa Teste",
            null,
            null,
            null,
            "18230000",
            "3550209",
            "SP",
            "São Miguel Arcanjo",
            "Rua de Teste",
            "1",
            null,
            "Centro"),
        CancellationToken.None);

    CompanyPaymentStatus? unpaidStatus = await store.GetCompanyPaymentStatusAsync(company.Id, CancellationToken.None);
    Ensure(unpaidStatus is { AcessoLiberado: false, Situacao: "sem_pagamento" }, "Empresa mensal sem pagamento deveria ficar pendente.");
    await store.RegisterCompanyPaymentAsync(company.Cnpj, new CompanyPaymentRequest(DateTimeOffset.UtcNow, 100m)
    {
        Referencia = "Teste automático"
    }, CancellationToken.None);
    CompanyPaymentStatus? paidStatus = await store.GetCompanyPaymentStatusAsync(company.Id, CancellationToken.None);
    Ensure(paidStatus is { AcessoLiberado: true, Situacao: "em_dia" }, "Pagamento recente deveria liberar a empresa por 45 dias.");

    ApiCredentialCreated createdCredential = await store.CreateCredentialAsync(
        new ApiCredentialRequest("Teste seguro", "HOMOLOGAÇÃO", ["sqlite"]),
        CancellationToken.None);

    LocalUserView createdUser = await store.CreateLocalUserAsync(
        new LocalUserRequest
        {
            EmpresaId = company.Id,
            Nome = "Usuário Teste",
            Usuario = "usuario.teste",
            Senha = "Senha-Teste-123",
            ClientId = createdCredential.Credencial.ClientId
        },
        CancellationToken.None);
    LocalUserView repeatedUser = await store.CreateLocalUserAsync(
        new LocalUserRequest
        {
            EmpresaId = company.Id,
            Nome = "Usuário Teste",
            Usuario = "usuario.teste",
            Senha = "Senha-Teste-123",
            ClientId = createdCredential.Credencial.ClientId
        },
        CancellationToken.None);
    Ensure(repeatedUser.Id == createdUser.Id, "Uma submissão idêntica criou duplicata ou retornou erro.");

    CompanyView secondCompany = await store.SaveCompanyAsync(
        new CompanyRequest(
            "juridica",
            "36024110000130",
            "segunda@empresa.local",
            "Segunda Empresa Ltda",
            "Segunda Empresa",
            null,
            null,
            null,
            "18230000",
            "3550209",
            "SP",
            "São Miguel Arcanjo",
            "Rua Dois",
            "2",
            null,
            "Centro"),
        CancellationToken.None);
    await store.CreateLocalUserAsync(
        new LocalUserRequest
        {
            EmpresaId = secondCompany.Id,
            Nome = "Usuário Homônimo",
            Usuario = "usuario.teste",
            Senha = "Outra-Senha-456",
            ClientId = createdCredential.Credencial.ClientId
        },
        CancellationToken.None);

    LocalUserAuthentication? validLocalLogin = await store.AuthenticateLocalUserAsync(
        "usuario.teste",
        "Senha-Teste-123",
        null, company.Cnpj, CancellationToken.None, "curso");
    Ensure(validLocalLogin != null, "O login local válido foi recusado.");
    Ensure(await store.AuthenticateLocalUserAsync(
        "USUARIO.TESTE",
        "Senha-Teste-123",
        null, company.Cnpj, CancellationToken.None, "curso") != null, "O login local diferenciou letras maiúsculas e minúsculas.");
    Ensure(await store.UpdateLocalUserPasswordAsync(
        company.Id,
        createdUser.Id,
        "Nova-Senha-<>\"",
        CancellationToken.None), "A alteração de senha não atualizou o usuário.");
    Ensure(await store.AuthenticateLocalUserAsync(
        "usuario.teste",
        "Senha-Teste-123",
        null, company.Cnpj, CancellationToken.None, "curso") == null, "A senha anterior continuou autenticando após a alteração.");
    Ensure((await store.AuthenticateLocalUserAsync(
        "usuario.teste",
        "Nova-Senha-<>\"",
        null, company.Cnpj, CancellationToken.None, "curso"))?.Usuario.EmpresaId == company.Id,
        "A nova senha não autenticou o usuário na empresa correta.");
    Ensure(await store.UpdateLocalUserPasswordAsync(
        company.Id,
        createdUser.Id,
        "Senha-Teste-123",
        CancellationToken.None), "A senha de continuidade do teste não foi restaurada.");

    LocalUserView specialPasswordUser = await store.CreateLocalUserAsync(
        new LocalUserRequest
        {
            EmpresaId = company.Id,
            Nome = "Usuário Senha Livre",
            Usuario = "senha.livre",
            Senha = "\"",
            ClientId = createdCredential.Credencial.ClientId
        },
        CancellationToken.None);
    Ensure(await store.AuthenticateLocalUserAsync(
        "senha.livre",
        "\"",
        null, company.Cnpj, CancellationToken.None, "curso") != null, "Uma senha de um caractere especial foi recusada.");
    Ensure((await store.AuthenticateLocalUserAsync(
        "usuario.teste",
        "Outra-Senha-456",
        null, secondCompany.Cnpj, CancellationToken.None, "curso"))?.Usuario.EmpresaId == secondCompany.Id,
        "A senha não resolveu o login homônimo para a empresa correta.");
    Ensure((await store.AuthenticateLocalUserAsync(
        "usuario.teste",
        "Senha-Teste-123",
        null, company.Cnpj, CancellationToken.None, "curso"))?.Usuario.EmpresaId == company.Id,
        "A senha da primeira empresa não resolveu o vínculo interno correto.");
    Ensure(await store.AuthenticateLocalUserAsync(
        "usuario.teste",
        "senha-invalida",
        null, company.Cnpj, CancellationToken.None, "curso") == null, "Uma senha local inválida foi aceita.");

    AccessTokenService tokenService = new(configuration);
    AccessTokenResult localToken = tokenService.Create(validLocalLogin!.Credencial, "sqlite");
    Ensure(tokenService.Validate(localToken.AccessToken).IsValid, "O Bearer local emitido é inválido.");

    string externalDatabasePath = Path.Combine(testDirectory, "BasesSQLite", "login.db");
    Directory.CreateDirectory(Path.GetDirectoryName(externalDatabasePath)!);
    await using (SqliteConnection connection = new($"Data Source={externalDatabasePath}"))
    {
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE USUARIOS (LOGIN TEXT, SENHA TEXT, ID_FILIAL INTEGER);
            INSERT INTO USUARIOS VALUES ('vlink.teste','Senha-Vlink-123',1);
            """;
        await command.ExecuteNonQueryAsync();
    }

    ExecuteRequest externalRequest = JsonSerializer.Deserialize<ExecuteRequest>("""
        {
            "acao": "LoginVlink",
            "tipo_banco": "sqlite",
            "banco": "login.db",
            "tabela_usuario": "USUARIOS",
            "campo_usuario": "LOGIN",
            "campo_senha": "SENHA",
            "usuario": "vlink.teste",
            "senha": "Senha-Vlink-123"
        }
        """) ?? throw new InvalidOperationException("Pedido de teste inválido.");
    Ensure(externalRequest.EffectiveFunction == "LoginVlink", "A ação LoginVlink não foi reconhecida.");

    DatabaseService databaseService = new(configuration);
    bool externalLoginAccepted = await databaseService.ValidateExternalUserAsync(
        externalRequest,
        CancellationToken.None);
    Ensure(externalLoginAccepted, "O login externo válido foi recusado.");
    Ensure(await store.DeleteLocalUserAsync(createdUser.Id, CancellationToken.None), "O usuário local não foi excluído.");
    Ensure(await store.AuthenticateLocalUserAsync(
        "usuario.teste",
        "Senha-Teste-123",
        null, company.Cnpj, CancellationToken.None, "curso") == null, "O usuário excluído continuou autenticando.");
    LocalUserView recreatedUser = await store.CreateLocalUserAsync(
        new LocalUserRequest
        {
            EmpresaId = company.Id,
            Nome = "Usuário Recriado",
            Usuario = "usuario.teste",
            Senha = "<>",
            ClientId = createdCredential.Credencial.ClientId
        },
        CancellationToken.None);
    Ensure(recreatedUser.Id != createdUser.Id, "O cadastro excluído não foi recriado como um novo registro.");
    Ensure(await store.AuthenticateLocalUserAsync(
        "usuario.teste",
        "<>",
        null, company.Cnpj, CancellationToken.None, "curso") != null, "O usuário recriado não autenticou com a nova senha.");
    Ensure(await store.DeleteLocalUserAsync(recreatedUser.Id, CancellationToken.None), "O usuário recriado não foi excluído.");
    Ensure(await store.DeleteLocalUserAsync(specialPasswordUser.Id, CancellationToken.None), "O usuário com senha livre não foi excluído.");

    Console.WriteLine("SECURITY_SMOKE_OK");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(testDirectory))
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(testDirectory, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(100);
            }
        }
    }
}

static void Ensure(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
