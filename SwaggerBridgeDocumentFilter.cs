using System.Collections.Generic;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

/// <summary>
/// Acrescenta ao contrato OpenAPI as rotas da Ponte Claude, que são atendidas
/// pela porta local exclusiva configurada no INI e não pela porta da API SQL.
/// </summary>
public sealed class SwaggerBridgeDocumentFilter : IDocumentFilter
{
    private const string TagName = "Ponte Claude — arquivos locais (porta 3000)";
    private const string PrintTagName = "Impressão local — agente porta 4000";
    private readonly int bridgePort;

    /// <summary>
    /// Inicializa a documentação usando a porta efetivamente lida do INI na
    /// partida do serviço.
    /// </summary>
    /// <param name="bridgePort">Porta local configurada em PonteClaude/Porta.</param>
    public SwaggerBridgeDocumentFilter(int bridgePort)
    {
        this.bridgePort = bridgePort;
    }

    /// <summary>
    /// Insere as rotas de status, lançador e publicação de arquivo local.
    /// </summary>
    /// <param name="swaggerDoc">Documento OpenAPI gerado pelo Swagger.</param>
    /// <param name="context">Contexto da geração do documento.</param>
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        OpenApiServer bridgeServer = new()
        {
            Url = $"http://127.0.0.1:{bridgePort}",
            Description = $"Ponte local configurada em [PonteClaude] Porta={bridgePort}"
        };

        swaggerDoc.Tags ??= new List<OpenApiTag>();
        swaggerDoc.Tags.Add(new OpenApiTag
        {
            Name = TagName,
            Description = "Serve arquivos locais para IAs e navegadores. O token não é um Bearer: ele fica em "
                + "[PonteClaude] Token no FacilAppSQL.ini e é enviado no parâmetro de query token. O valor é "
                + "relido do INI a cada chamada, portanto pode ser trocado sem reiniciar a API. Nunca copie o "
                + "token para prompts, páginas HTML, repositórios, capturas ou logs."
        });
        swaggerDoc.Tags.Add(new OpenApiTag
        {
            Name = PrintTagName,
            Description = "Agente local de impressão textual/ESC-POS. É um serviço separado da API SQL e atende em http://127.0.0.1:4000."
        });

        swaggerDoc.Paths["/API/status"] = new OpenApiPathItem
        {
            Operations = new Dictionary<OperationType, OpenApiOperation>
            {
                [OperationType.Get] = CreateOperation(
                    bridgeServer,
                    "Verifica se a ponte local está ativa",
                    "Execute na porta da ponte, não na porta 5050 da API. A IA deve obter o token no "
                        + "FacilAppSQL.ini autorizado, seção [PonteClaude], chave Token, e enviá-lo somente "
                        + "no parâmetro token.",
                    includePathParameter: false,
                    contentType: "application/json")
            }
        };

        swaggerDoc.Paths["/lancador.html"] = new OpenApiPathItem
        {
            Operations = new Dictionary<OperationType, OpenApiOperation>
            {
                [OperationType.Get] = CreateOperation(
                    bridgeServer,
                    "Abre o lançador de arquivos locais",
                    "O lançador converte caminhos Windows para URLs UTF-8, incluindo espaços e acentos. "
                        + "Depois da primeira autenticação válida, a ponte cria um cookie HTTP-only para os "
                        + "recursos filhos.",
                    includePathParameter: false,
                    contentType: "text/html")
            }
        };

        swaggerDoc.Paths["/HTML/{caminhoAbsoluto}"] = new OpenApiPathItem
        {
            Operations = new Dictionary<OperationType, OpenApiOperation>
            {
                [OperationType.Get] = CreateOperation(
                    bridgeServer,
                    "Publica um arquivo local pela ponte",
                    "Informe o caminho absoluto do arquivo. A ponte aceita barras invertidas ou barras de URL "
                        + "e normaliza os dois formatos. Exemplo lógico: "
                        + "C:\\FacilApp\\Curso\\COWORK-SQLITE\\index.html. O caminho deve ser codificado "
                        + "em UTF-8 na URL. Esta rota somente transporta arquivos; ela não executa SQL.",
                    includePathParameter: true,
                    contentType: "application/octet-stream")
            }
        };

        OpenApiServer printServer = new()
        {
            Url = "http://127.0.0.1:4000",
            Description = "Agente de impressão local"
        };
        swaggerDoc.Paths["/imprime"] = new OpenApiPathItem
        {
            Operations = new Dictionary<OperationType, OpenApiOperation>
            {
                [OperationType.Post] = new OpenApiOperation
                {
                    Tags = new[] { new OpenApiTag { Name = PrintTagName } },
                    Summary = "Imprime texto em impressora local, de rede ou IP",
                    Description = "Execute na porta 4000. Exige Authorization: Bearer {access_token}. Aceita impressão textual/ESC-POS por network_ip, windows_share, windows_installed ou serial_com.",
                    Servers = new List<OpenApiServer> { printServer },
                    RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema { Type = "object" },
                                Example = new Microsoft.OpenApi.Any.OpenApiString("{\"tipo\":\"network_ip\",\"ip\":\"192.168.2.200\",\"porta\":9100,\"texto\":\"COMANDA TESTE\\n\",\"cortar\":true}")
                            }
                        }
                    },
                    Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse { Description = "Texto enviado para a impressora." },
                        ["400"] = new OpenApiResponse { Description = "Dados de impressão inválidos." },
                        ["401"] = new OpenApiResponse { Description = "Bearer ausente ou inválido." },
                        ["500"] = new OpenApiResponse { Description = "Falha de comunicação com a impressora." }
                    }
                }
            }
        };

        // As operações multibanco e PS1 são rotas reais da API 5050. Elas são
        // declaradas também no documento para que apareçam de forma estável no
        // Swagger, inclusive em instalações já compiladas com descoberta mínima.
        AddProtectedPost(
            swaggerDoc,
            "/multibanco/consultar",
            "Multibanco",
            "Consultar — multibanco",
            "Consulta estruturada. A API gera o dialeto de SQLite, PostgreSQL, SQL Server, MySQL, MariaDB, Oracle ou HFSQL.",
            "{\"tipo_banco\":\"sqlite\",\"banco\":\"gourmet.db\",\"tabela\":\"categorias\",\"campos\":\"id,nome,ativo\",\"where\":{\"ativo\":1}}"
        );
        AddProtectedPost(
            swaggerDoc,
            "/multibanco/inserir",
            "Multibanco",
            "Inserir — multibanco",
            "Insere dados parametrizados no banco informado; a tela não precisa escrever SQL.",
            "{\"tipo_banco\":\"sqlite\",\"banco\":\"gourmet.db\",\"tabela\":\"categorias\",\"dados\":{\"nome\":\"Bebidas\",\"ativo\":1}}"
        );
        AddProtectedPost(
            swaggerDoc,
            "/multibanco/alterar",
            "Multibanco",
            "Alterar — multibanco",
            "Altera dados parametrizados. O objeto where é obrigatório.",
            "{\"tipo_banco\":\"postgresql\",\"banco\":\"GOURMET\",\"tabela\":\"categorias\",\"dados\":{\"nome\":\"Bebidas geladas\"},\"where\":{\"id\":1}}"
        );
        AddProtectedPost(
            swaggerDoc,
            "/multibanco/excluir",
            "Multibanco",
            "Excluir — multibanco",
            "Exclui dados parametrizados. O objeto where é obrigatório.",
            "{\"tipo_banco\":\"sqlserver\",\"banco\":\"GOURMET\",\"tabela\":\"categorias\",\"where\":{\"id\":1}}"
        );
        AddProtectedPost(
            swaggerDoc,
            "/multibanco/estrutura/api",
            "Multibanco",
            "Publicar estrutura do SQLite da API",
            "Usa Dados/facilapp_sql.db como referência dinâmica. Cria tabelas ausentes e adiciona/adequa campos no banco destino; após alterações futuras na estrutura da API, execute novamente esta mesma chamada.",
            "{\"tipo_banco\":\"postgresql\",\"banco\":\"FACILAPP\",\"tabelas\":[]}"
        );
        AddProtectedPost(
            swaggerDoc,
            "/scripts/executar",
            "Scripts PS1",
            "Executa arquivo PowerShell autorizado",
            "Executa somente arquivo .ps1 existente. Aceita caminho local ou UNC configurado em [Scripts] DiretoriosPermitidos; não aceita comando PowerShell no JSON. Sem caminho, procura no diretório padrão [Scripts] Diretorio.",
            "{\"arquivo\":\"C:\\FacilApp\\API_FACILAPP_SQL\\Scripts\\Migrar-Pedidos.ps1\"}"
        );

        // Estrutura é uma operação interna do console/aplicações: não aparece
        // como teste no Swagger público, mas a rota continua disponível à API.
        swaggerDoc.Paths.Remove("/multibanco/estrutura");
    }

    private static void AddProtectedPost(
        OpenApiDocument document,
        string path,
        string tag,
        string summary,
        string description,
        string example)
    {
        document.Paths[path] = new OpenApiPathItem
        {
            Operations = new Dictionary<OperationType, OpenApiOperation>
            {
                [OperationType.Post] = new OpenApiOperation
                {
                    Tags = new[] { new OpenApiTag { Name = tag } },
                    Summary = summary,
                    Description = description + " Exige Authorization: Bearer {access_token} válido.",
                    Security = new List<OpenApiSecurityRequirement>
                    {
                        new()
                        {
                            [new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "Bearer"
                                }
                            }] = System.Array.Empty<string>()
                        }
                    },
                    RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema { Type = "object" },
                                Example = new Microsoft.OpenApi.Any.OpenApiString(example)
                            }
                        }
                    },
                    Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse { Description = "Operação executada." },
                        ["400"] = new OpenApiResponse { Description = "Dados inválidos ou conflito de estrutura." },
                        ["401"] = new OpenApiResponse { Description = "Bearer ausente ou inválido." },
                        ["403"] = new OpenApiResponse { Description = "Acesso recusado." }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Cria uma operação da ponte com servidor, token e respostas padronizados.
    /// </summary>
    private static OpenApiOperation CreateOperation(
        OpenApiServer bridgeServer,
        string summary,
        string description,
        bool includePathParameter,
        string contentType)
    {
        List<OpenApiParameter> parameters = new();
        if (includePathParameter)
        {
            parameters.Add(new OpenApiParameter
            {
                Name = "caminhoAbsoluto",
                In = ParameterLocation.Path,
                Required = true,
                Description = "Caminho absoluto Windows codificado para URL, com todos os segmentos do arquivo.",
                Schema = new OpenApiSchema { Type = "string" },
                Example = new Microsoft.OpenApi.Any.OpenApiString("C:\\FacilApp\\Curso\\COWORK-SQLITE\\index.html")
            });
        }

        parameters.Add(new OpenApiParameter
        {
            Name = "token",
            In = ParameterLocation.Query,
            Required = true,
            Description = "Token configurado em [PonteClaude] Token no FacilAppSQL.ini. Não é o Bearer da API SQL e não deve ser exposto.",
            Schema = new OpenApiSchema { Type = "string", Format = "password" }
        });

        return new OpenApiOperation
        {
            Tags = new[] { new OpenApiTag { Name = TagName } },
            Summary = summary,
            Description = description,
            Servers = new List<OpenApiServer> { bridgeServer },
            Parameters = parameters,
            Security = new List<OpenApiSecurityRequirement>(),
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse
                {
                    Description = "Requisição atendida pela ponte.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        [contentType] = new OpenApiMediaType()
                    }
                },
                ["400"] = new OpenApiResponse { Description = "Caminho inválido ou não absoluto." },
                ["401"] = new OpenApiResponse { Description = "Token da ponte ausente ou inválido." },
                ["404"] = new OpenApiResponse { Description = "Arquivo ou rota não encontrado." },
                ["503"] = new OpenApiResponse { Description = "Token da ponte não configurado no INI." }
            }
        };
    }
}
