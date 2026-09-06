# FacilApp SQL SDK para C#

SDK oficial para .NET 8 ou superior.

```csharp
using FacilApp.Sql.Sdk;
using var api = new FacilAppSqlClient();
var login = await api.LoginWithClientSecretAsync("CLIENT_ID", "CLIENT_SECRET");
var resultado = await api.ExecutarAsync(new { funcao = "consultar_sqlserver", banco = "BANCO", tabela = "TABELA" });
```

Não grave Client Secret ou Bearer no código-fonte. Documentação: https://sql.facilapp.com.br/docs/.
