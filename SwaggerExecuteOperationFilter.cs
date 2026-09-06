using System;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

/// <summary>
/// Enriquece a rota genérica <c>/executar</c> com o catálogo real de funções
/// aceitas pela API. As funções não recebem rotas artificiais: todas continuam
/// sendo executadas pelo mesmo contrato HTTP já usado pelos clientes.
/// </summary>
public sealed class SwaggerExecuteOperationFilter : IOperationFilter
{
    /// <summary>
    /// Insere no Swagger a documentação operacional da rota <c>/executar</c>.
    /// </summary>
    /// <param name="operation">Operação OpenAPI que será exibida pela interface.</param>
    /// <param name="context">Contexto com a descrição da rota mapeada.</param>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        string relativePath = context.ApiDescription.RelativePath ?? string.Empty;
        if (relativePath.StartsWith("multibanco/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureBearer(operation);
            return;
        }

        if (string.Equals(relativePath, "sync/status", StringComparison.OrdinalIgnoreCase))
        {
            operation.Summary = "Consulta a configuração da sincronização";
            operation.Description = "Exibe o estado da sincronização SQLite → PostgreSQL e os parâmetros não sensíveis da seção [Sincronizacao]. Exige Authorization: Bearer {access_token} válido.";
            ConfigureSyncOperation(operation);
            return;
        }

        if (string.Equals(relativePath, "sync/enviar", StringComparison.OrdinalIgnoreCase))
        {
            operation.Summary = "Sincroniza uma tabela SQLite com o PostgreSQL";
            operation.Description = """
                Recebe uma tabela SQLite, cria a tabela de destino quando necessário, adiciona colunas ausentes e faz upsert dos registros no PostgreSQL configurado.

                Exige `Authorization: Bearer {access_token}` válido.

                Corpo mínimo: `{ "tabela": "vendas" }`.

                Corpo com banco local explícito: `{ "tabela": "vendas", "banco": "gourmet.db" }`.

                Quando `banco` não for enviado, usa `[Sincronizacao] BancoLocal`. O destino usa `[Sincronizacao] BancoNuvem` e as credenciais de `[PostgreSQL]`. A operação atual é sob demanda, envia/cria/atualiza registros e não propaga exclusões.
                """;
            ConfigureSyncOperation(operation);
            return;
        }

        if (!string.Equals(context.ApiDescription.RelativePath, "executar", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        operation.Summary = "Executa uma função da FacilApp SQL API";
        operation.Description = """
            Todas as operações abaixo são enviadas por **POST /executar** no campo `funcao`.

            **Login público, sem Bearer de entrada:** `Login`, `LoginVlink`. Envie somente `usuario` e `senha`; a API resolve a credencial vinculada internamente e responde com `access_token`.

            `Login` também devolve `cnpj` somente com números no nível principal e dentro de `usuario`, além de `usuario` (`idUsuario`, `idFilial`, `login`, `nome`) e `permissoes` (`lateral`, `superior`, `dashboards`) cadastradas no SQLite. `LoginVlink` valida os mesmos campos na tabela `usuarios` do banco VLINK e retorna o Bearer.

            **Renovação:** `ObterToken` exige um Bearer ainda válido no cabeçalho `Authorization`. Ela devolve um novo `access_token` com os mesmos escopos, sem solicitar usuário, senha, Client ID ou Client Secret. Se o Bearer já venceu, chame `Login` novamente.

            **Banco definido pela chamada:** envie `tipo_banco`, `servidor` e `banco` no JSON. Esses valores têm prioridade sobre os padrões do INI; o INI fornece somente as credenciais técnicas quando necessárias.

            **SQL livre:** `consulta_livre`, `inserir`, `alterar` e `excluir` executam exatamente o campo `sql` no banco informado. Aceitam SELECT, JOIN, INSERT, UPDATE, DELETE, DDL e demais comandos suportados pelo SGBD, sem limite fixo de registros.

            **Funções genéricas recomendadas (use estas em todas as telas):** `consultar` (consulta estruturada), `consulta_livre` (SQL livre), `listar_tabelas` e `listar_campos`. Informe `tipo_banco` como `sqlite`, `postgresql`, `sqlserver`, `mariadb`, `mysql`, `oracle` ou `hfsql`.

            **Multibanco estruturado:** use os endpoints separados `/multibanco/consultar`, `/multibanco/inserir`, `/multibanco/alterar`, `/multibanco/excluir` e `/multibanco/estrutura`. O último cria tabelas ausentes, adiciona campos ausentes e confere tipo/tamanho usando o dialeto de SQLite, PostgreSQL, SQL Server, MariaDB, MySQL, Oracle ou HFSQL. As funções antigas e o SQL livre continuam funcionando.

            Exemplo: `{ "funcao": "inserir_multibanco", "tipo_banco": "sqlite", "banco": "gourmet.db", "tabela": "categorias", "dados": { "nome": "Bebidas", "ativo": 1 } }`.

            1. Listar tabelas: `{ "funcao": "listar_tabelas", "tipo_banco": "sqlite", "banco": "curso.db" }`.

            2. Consultar uma tabela: `{ "funcao": "consultar_sqlite", "banco": "curso.db", "tabela": "clientes", "campos": "id, nome, email" }`.

            3. Consulta livre: `{ "funcao": "consultar_sqlite_livre", "banco": "curso.db", "sql": "SELECT id, nome, email FROM clientes ORDER BY nome" }`.

            A resposta bem-sucedida contém `ok: true` e o resultado em `dados`. No SQLite, `banco` aceita o nome ou o caminho informado pela aplicação.

            **Consultas padrão:** `consultar`, `consulta_livre`, `listar_tabelas`, `listar_campos`. As funções específicas por banco (`consultar_sqlite`, `consultar_sqlite_livre` e equivalentes) ficam restritas à compatibilidade com telas antigas; novas telas devem usar somente as funções genéricas e trocar `tipo_banco`.

            **Alterações em dados:** `inserir`, `alterar`, `excluir`, `inserir_hfsql`, `alterar_hfsql`, `excluir_hfsql`.

            **Gravar e ler arquivos:** `arquivo_salvar`, `arquivo_ler`, `arquivo_listar`, `arquivo_excluir`. Em `arquivo_salvar` e `arquivo_ler`, informe em `nome_arquivo` o **caminho completo incluindo o nome e a extensão do arquivo**, por exemplo `C:\\FacilApp\\Curso\\HTML\\config.js` ou `C:\\FacilApp\\Curso\\FacilAppSQL.ini`; a API usa exatamente esse destino. Quando receber somente `config.js`, mantém o modo compatível e grava no `[Arquivos] DiretorioBase` com categoria/desenvolvedor/usuário. Somente extensões presentes em `[Arquivos] ExtensoesPermitidas` são aceitas.

            **Integrações:** `mapa_geocodificar`, `whatsapp_zapi_enviar`, `whatsapp_enviar`, `whatsapp_meta_enviar`, `ia_chat`, `consulta_via_ia`, `executar_via_ia`.

            **Configuração:** `cfg_ler`, `cfg_gravar`, `vincular_credencial_externa`.

            A autenticação de `/executar` é `Authorization: Bearer {access_token}` válido. Com Bearer válido, não há bloqueio por origem, IP, LAN, WAN, Tailscale, domínio, escopo, banco, tabela, comando SQL ou quantidade de registros. Para arquivos, permanece válida somente a lista configurável `[Arquivos] ExtensoesPermitidas`.

            **Login SQLite obrigatório:** identifica o usuário por `sistema_id + cnpj + usuario + senha`.

            **Exemplo Login:** `{ "funcao": "Login", "sistema_id": "gourmet", "cnpj": "12345678000190", "usuario": "usuario", "senha": "senha" }`

            **Endpoints de usuário:** `GET /api/console/empresas/{empresaId}/usuarios` (listar), `GET /api/console/empresas/{empresaId}/usuarios/{id}` (consultar), `POST /api/console/empresas/{empresaId}/usuarios` (cadastrar), `PUT /api/console/empresas/{empresaId}/usuarios/{id}` (alterar), `PUT /api/console/empresas/{empresaId}/usuarios/{id}/senha`, `DELETE /api/console/usuarios/{id}`, `GET /api/console/empresas/{empresaId}/usuarios/{id}/menus` e `PUT /api/console/empresas/{empresaId}/usuarios/{id}/menus`. Pertencem à API única e funcionam igualmente por localhost, LAN, internet ou Tailscale com access token válido, preservando o vínculo com a empresa.

            **Exemplo ObterToken:** `{ "funcao": "ObterToken" }` com o Bearer atual no cabeçalho.
            """;
        operation.Tags = new[] { new OpenApiTag { Name = "Funções da API" } };
        // A rota contém Login e LoginVlink, que são públicos. Não há um
        // cadeado global no Swagger: o Bearer é exigido apenas pelas funções
        // protegidas, depois que um login válido devolve o token temporário.
    }

    private static void ConfigureSyncOperation(OpenApiOperation operation)
    {
        operation.Tags = new[] { new OpenApiTag { Name = "Sincronização" } };
        ConfigureBearer(operation);
    }

    private static void ConfigureBearer(OpenApiOperation operation)
    {
        operation.Security = new System.Collections.Generic.List<OpenApiSecurityRequirement>
        {
            new()
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                }] = Array.Empty<string>()
            }
        };
    }
}
