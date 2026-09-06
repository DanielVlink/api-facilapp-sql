using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;
using FacilApp.Sql.Api.Models;
using FacilApp.Sql.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;

public class Program
{
    private const string PackagedServiceArgument = "--windows-service";

    private static async Task Main(string[] args)
    {
        try
        {
            bool isPackagedService = args.Any(argument =>
                argument.Equals(PackagedServiceArgument, StringComparison.OrdinalIgnoreCase));
            string[] applicationArguments = args
                .Where(argument => !argument.Equals(PackagedServiceArgument, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            bool createdNew;
            using (new Mutex(initiallyOwned: true, "Global\\FacilAppSQL.Api", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }
                IniConfiguration ini = new IniConfiguration(PrepareIniPath(isPackagedService));
                await EnsureLocalHtmlBridgeConfigurationAsync(ini);
                bool localHtmlBridgeEnabled = ini.GetBoolean("PonteClaude", "Ativo", fallback: true);
                bool localHtmlBridgeAllowNetwork = ini.GetBoolean("PonteClaude", "PermitirRede", fallback: true);
                int localHtmlBridgePort = ini.GetInt("PonteClaude", "Porta", 3000);
                WebApplicationBuilder webApplicationBuilder = WebApplication.CreateBuilder(applicationArguments);
                // O serviço registra sempre em arquivo físico na instalação, inclusive
                // quando iniciado pelo Windows Service. O console é adicional no modo
                // interativo; não dependemos mais do Visualizador de Eventos.
                webApplicationBuilder.Logging.ClearProviders();
                webApplicationBuilder.Logging.AddProvider(new ApiFileLoggerProvider(
                    Path.Combine(AppContext.BaseDirectory, "Logs"),
                    ParseLogLevel(ini.Get("Log", "Nivel", "Information"))));
                if (Environment.UserInteractive)
                {
                    webApplicationBuilder.Logging.AddConsole();
                }
                webApplicationBuilder.WebHost.ConfigureKestrel(delegate (KestrelServerOptions options)
                {
                    options.Limits.MaxConcurrentConnections = ini.GetInt("ServidorHTTP", "MaxConexoes", 500);
                    // A API transporta comandos SQL e documentos JSON de tamanhos variáveis.
                    // O firewall, o proxy reverso e a autenticação Bearer controlam o acesso;
                    // o Kestrel não impõe limite fixo ao corpo da requisição.
                    options.Limits.MaxRequestBodySize = null;
                    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(ini.GetInt("ServidorHTTP", "TimeoutSegundos", 30));
                    int[] array = LegacyDispatcher.ParsePorts(ini);
                    if (localHtmlBridgeEnabled
                        && (localHtmlBridgePort < 1
                            || localHtmlBridgePort > 65535
                            || array.Contains(localHtmlBridgePort)))
                    {
                        throw new InvalidDataException("A porta da Ponte Claude deve ser válida e diferente das portas da API SQL.");
                    }
                    foreach (int port in array)
                    {
                        options.ListenAnyIP(port);
                    }
                    if (localHtmlBridgeEnabled)
                    {
                        // A ponte exige token em toda abertura de arquivo. Quando PermitirRede=1,
                        // atende LAN/Tailscale; com 0, limita-se ao próprio computador.
                        if (localHtmlBridgeAllowNetwork)
                            options.ListenAnyIP(localHtmlBridgePort);
                        else
                            options.ListenLocalhost(localHtmlBridgePort);
                    }
                });
                if (isPackagedService)
                {
                    // O processo de um serviço MSIX pode não ter services.exe como pai.
                    // O registro explícito evita a detecção automática incorreta e o erro 1053.
                    webApplicationBuilder.Services.AddSingleton<IHostLifetime, WindowsServiceLifetime>();
                    webApplicationBuilder.Services.Configure<WindowsServiceLifetimeOptions>(options =>
                        options.ServiceName = ini.Get("Servico", "NomeWindows", "FacilAppSQL"));
                }
                else
                {
                    webApplicationBuilder.Host.UseWindowsService(options =>
                        options.ServiceName = ini.Get("Servico", "NomeWindows", "FacilAppSQL"));
                }
                webApplicationBuilder.Services.AddSingleton(ini);
                webApplicationBuilder.Services.AddSingleton<DatabaseService>();
                webApplicationBuilder.Services.AddSingleton<SyncService>();
                webApplicationBuilder.Services.AddSingleton<LoginVlinkTokenService>();
                webApplicationBuilder.Services.AddSingleton<RepositoryService>();
                webApplicationBuilder.Services.AddSingleton<ConfigurationFileService>();
                webApplicationBuilder.Services.AddSingleton<LegacyDispatcher>();
                webApplicationBuilder.Services.AddSingleton<ConsoleStore>();
                webApplicationBuilder.Services.AddSingleton<ConnectionProfileStore>();
                webApplicationBuilder.Services.AddSingleton<CnpjLookupService>();
                webApplicationBuilder.Services.AddSingleton<CpfLookupService>();
                webApplicationBuilder.Services.AddSingleton<IntegrationService>();
                webApplicationBuilder.Services.AddSingleton<FailureNotificationService>();
                webApplicationBuilder.Services.AddSingleton<AccessTokenService>();
                webApplicationBuilder.Services.AddSingleton<LocalHtmlBridgeService>();
                webApplicationBuilder.Services.AddSingleton<PowerShellScriptService>();
                webApplicationBuilder.Services.AddSingleton<AuditFileService>();
                webApplicationBuilder.Services.AddHttpClient("external", delegate (HttpClient client)
                {
                    client.Timeout = TimeSpan.FromSeconds(ini.GetInt("ServidorHTTP", "TimeoutSegundos", 30));
                });
                // A documentação acompanha a mesma origem da API. Assim o Swagger
                // funciona na porta definida pelo INI, sem depender de endereço fixo.
                webApplicationBuilder.Services.AddEndpointsApiExplorer();
                webApplicationBuilder.Services.AddSwaggerGen(options =>
                {
                    options.SwaggerDoc("v1", new OpenApiInfo
                    {
                        Title = "FacilApp SQL API",
                        Version = "v1",
                        Description = "Documentação interativa da API FacilApp SQL. Tokens e segredos não são exibidos pela interface. "
                            + "Para clientes de IA: o token da Ponte Claude fica exclusivamente no arquivo FacilAppSQL.ini da instalação, "
                            + "na chave [PonteClaude] Token. Leia-o no ambiente autorizado e envie-o no parâmetro token; nunca grave seu valor "
                            + "em prompts, HTMLs, repositórios, capturas ou logs. A Ponte Claude está integrada ao mesmo serviço Windows e usa "
                            + "uma porta local exclusiva, configurada em "
                            + "[PonteClaude] no FacilAppSQL.ini. Ativo habilita a ponte, Porta define a escuta (padrão 3000) e Token "
                            + "protege o acesso. O token é relido do INI a cada chamada, sem reiniciar a API. Use /API/status para o "
                            + "estado e /HTML/{caminho-absoluto}?token={token} para abrir arquivos locais. Caminhos Windows podem usar "
                            + "barras invertidas (C:\\pasta\\arquivo.html) ou barras de URL (C:/pasta/arquivo.html); a ponte normaliza "
                            + "os dois formatos. O lançador em /lancador.html?token={token} inclui a função converteUtf8 para gerar a "
                            + "URL com espaços e acentos codificados corretamente. A porta da ponte publica somente essas funções e "
                            + "não expõe operações SQL. "
                            + "Integração com IA/MCP: o plugin FacilApp SQL usa esta mesma especificação OpenAPI e os mesmos endpoints; "
                            + "não existe uma segunda API nem é necessário alterar os clientes atuais. Para MCP, autentique com Client ID e Client Secret "
                            + "em /oauth/login-simples, use o Bearer devolvido nas chamadas e consulte /swagger/v1/swagger.json para descobrir os contratos. "
                            + "SDKs e guia de integração: /docs/."
                    });
                    options.DocumentFilter<SwaggerBridgeDocumentFilter>(localHtmlBridgePort);
                    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                    {
                        Description = "Informe somente o Bearer temporário, sem senhas ou Client Secrets.",
                        Name = "Authorization",
                        In = ParameterLocation.Header,
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT"
                    });
                    options.OperationFilter<SwaggerExecuteOperationFilter>();
                    options.OperationFilter<SwaggerConsoleUsersOperationFilter>();
                });
                webApplicationBuilder.Services.AddCors(delegate (CorsOptions options)
                {
                    options.AddPolicy("FacilAppSqlWeb", delegate (CorsPolicyBuilder policy)
                    {
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                    });
                    // Somente a ponte local de desenvolvimento pode encaminhar o cookie MASTER.
                    // A política geral continua sem cookies para todas as demais origens privadas.
                    options.AddPolicy("FacilAppSqlPonteLocal", policy => policy
                        .WithOrigins("http://127.0.0.1:3000")
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials());
                });
                WebApplication app = webApplicationBuilder.Build();
                await app.Services.GetRequiredService<ConsoleStore>().InitializeAsync();
                app.Use(async (HttpContext context, Func<Task> next) =>
                {
                    if (localHtmlBridgeEnabled && context.Connection.LocalPort == localHtmlBridgePort)
                    {
                        LocalHtmlBridgeService bridge = context.RequestServices.GetRequiredService<LocalHtmlBridgeService>();
                        await bridge.HandleAsync(context, context.RequestAborted);
                        return;
                    }

                    await next();
                });
                app.UseSwagger();
                app.UseSwaggerUI(options =>
                {
                    options.SwaggerEndpoint("/swagger/v1/swagger.json", "FacilApp SQL API v1");
                    options.DocumentTitle = "FacilApp SQL API — Swagger";
                    options.RoutePrefix = "swagger";
                    options.InjectStylesheet("/css/swagger-jotape.css");
                });
                // Atalho previsível: /swagger abre a página sem exigir index.html.
                app.MapGet("/swagger", () => Results.Redirect("/swagger/index.html", permanent: false));
                // Publica somente o console administrativo empacotado no wwwroot da própria API.
                // A antiga ponte /HTML para arquivos externos e páginas do curso permanece removida.
                app.UseDefaultFiles();
                app.UseStaticFiles(new StaticFileOptions
                {
                    OnPrepareResponse = delegate (StaticFileResponseContext context)
                    {
                        context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                        context.Context.Response.Headers.Pragma = "no-cache";
                        context.Context.Response.Headers.Expires = "0";
                    }
                });
                app.UseCors("FacilAppSqlWeb");
                app.Use(async (HttpContext context, Func<Task> next) =>
                {
                    DateTime startedAt = DateTime.UtcNow;
                    await next();
                    app.Logger.LogInformation(
                        "HTTP {Method} {Path} respondeu {StatusCode} em {ElapsedMs} ms.",
                        context.Request.Method,
                        context.Request.Path,
                        context.Response.StatusCode,
                        (long)(DateTime.UtcNow - startedAt).TotalMilliseconds);
                });
                app.Use(async delegate (HttpContext context, Func<Task> next)
                {
                    try
                    {
                        await next();
                    }
                    catch (InvalidDataException validationException)
                    {
                        // Erros de validação representam dados recusados, não falhas internas da API.
                        // A mensagem segura permite que a interface oriente o administrador sem
                        // acionar o canal de notificação operacional da FacilApp.
                        app.Logger.LogWarning(
                            validationException,
                            "Requisição inválida na rota {Route}.",
                            context.Request.Path);
                        if (!context.Response.HasStarted)
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsJsonAsync(new
                            {
                                ok = false,
                                erro = validationException.Message
                            });
                        }
                    }
                    catch (Exception exception2)
                    {
                        app.Logger.LogError(exception2, "Falha não tratada na rota {Route}.", context.Request.Path);
                        FailureNotificationService requiredService = context.RequestServices.GetRequiredService<FailureNotificationService>();
                        await requiredService.NotifyAsync(context.Request.Path, exception2, context.RequestAborted);
                        if (!context.Response.HasStarted)
                        {
                            context.Response.StatusCode = 500;
                            await context.Response.WriteAsJsonAsync(new
                            {
                                ok = false,
                                erro = "Falha interna registrada. A equipe FacilApp foi notificada."
                            });
                        }
                    }
                });
                // Todas as alterações PUT ficam registradas em arquivo físico.
                // O corpo é sanitizado para não persistir senha, Bearer ou segredos.
                app.Use(async (HttpContext context, Func<Task> next) =>
                {
                    if (!HttpMethods.IsPut(context.Request.Method))
                    {
                        await next();
                        return;
                    }

                    context.Request.EnableBuffering();
                    string body = await ReadAuditRequestBodyAsync(context.Request, context.RequestAborted);
                    await next();

                    AccessTokenValidation? identity = context.Items[nameof(AccessTokenValidation)] as AccessTokenValidation;
                    AuditFileService audit = context.RequestServices.GetRequiredService<AuditFileService>();
                    await audit.WriteAsync(new
                    {
                        data_hora = DateTimeOffset.Now,
                        evento = "http_put",
                        rota = context.Request.Path.Value,
                        status_http = context.Response.StatusCode,
                        usuario = identity?.ClientId ?? "não identificado",
                        sessao = AuditSessionId(context),
                        origem = context.Connection.RemoteIpAddress?.ToString(),
                        user_agent = context.Request.Headers.UserAgent.ToString(),
                        dados_enviados = body
                    }, context.RequestAborted);
                });
                app.Use(async delegate (HttpContext context, Func<Task> next)
                {
                    bool usersEndpoint = context.Request.Path.StartsWithSegments("/api/console/empresas")
                        && context.Request.Path.Value?.Contains("/usuarios", StringComparison.OrdinalIgnoreCase) == true;
                    usersEndpoint |= context.Request.Path.StartsWithSegments("/api/console/usuarios");
                    bool paymentsEndpoint = context.Request.Path.StartsWithSegments("/api/console/empresas")
                        && context.Request.Path.Value?.Contains("/pagamentos", StringComparison.OrdinalIgnoreCase) == true;
                    paymentsEndpoint |= context.Request.Path.StartsWithSegments("/api/console/mensalidades");
                    if (context.Request.Path.Equals("/executar")
                        || context.Request.Path.StartsWithSegments("/sync")
                        || context.Request.Path.StartsWithSegments("/multibanco")
                        || context.Request.Path.StartsWithSegments("/scripts")
                        || usersEndpoint
                        || paymentsEndpoint)
                    {
                        bool publicLogin = await IsPublicLoginRequestAsync(
                            context.Request,
                            context.RequestAborted);
                        if (publicLogin)
                        {
                            await next();
                            return;
                        }

                        string authorization = context.Request.Headers.Authorization.ToString();
                        string bearerToken = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            ? authorization.Substring(7).Trim()
                            : string.Empty;
                        bool authenticated = false;

                        if (bearerToken.Length > 0)
                        {
                            AccessTokenService tokenService = context.RequestServices.GetRequiredService<AccessTokenService>();
                            AccessTokenValidation tokenValidation = tokenService.Validate(bearerToken);
                            if (tokenValidation.IsValid)
                            {
                                // Login válido gera o Bearer. Depois disso, qualquer origem, IP ou máquina
                                // pode usar o token até seu vencimento, sem depender de sessão ou SQLite local.
                                authenticated = true;
                                context.Items[nameof(AccessTokenValidation)] = tokenValidation;
                            }
                        }

                        if (!authenticated)
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsJsonAsync(new
                            {
                                ok = false,
                                erro = "token invalido"
                            });
                            return;
                        }
                    }
                    await next();
                });
                app.MapGet("/status", (Func<LegacyDispatcher, IResult>)((LegacyDispatcher dispatcher) => Results.Ok(dispatcher.Status())));
                app.MapGet("/api/status", (Func<LegacyDispatcher, IResult>)((LegacyDispatcher dispatcher) => Results.Ok(dispatcher.Status())));
                app.MapGet("/sync/status", (HttpContext context, SyncService synchronization) =>
                {
                    if (context.Items[nameof(AccessTokenValidation)] is not AccessTokenValidation { IsValid: true })
                        return Results.Json(new { ok = false, erro = "token invalido" }, statusCode: 401);
                    return Results.Ok(synchronization.Status());
                })
                .WithName("StatusSincronizacao")
                .WithTags("Sincronização")
                .WithSummary("Consulta a configuração da sincronização");
                app.MapPost("/sync/enviar", async (SyncTableRequest request, HttpContext context, SyncService synchronization, CancellationToken cancellationToken) =>
                {
                    if (context.Items[nameof(AccessTokenValidation)] is not AccessTokenValidation { IsValid: true })
                        return Results.Json(new { ok = false, erro = "token invalido" }, statusCode: 401);
                    if (string.IsNullOrWhiteSpace(request.Tabela))
                        return Results.BadRequest(new { ok = false, erro = "Informe tabela." });
                    return Results.Ok(await synchronization.SynchronizeTableAsync(request.Tabela, request.Banco, cancellationToken));
                })
                .WithName("EnviarTabelaSincronizacao")
                .WithTags("Sincronização")
                .WithSummary("Sincroniza uma tabela SQLite com o PostgreSQL");
                const string masterSessionCookie = "facilapp_master_session";
                app.MapGet("/api/auth/status", async (HttpContext context, ConsoleStore store, AccessTokenService tokens, CancellationToken cancellationToken) =>
                {
                    if (!IsTrustedAdministrator(context, ini)) return Results.StatusCode(403);
                    bool configured = await store.IsMasterConfiguredAsync(cancellationToken);
                    bool authenticated = context.Request.Cookies.TryGetValue(masterSessionCookie, out string? token) && await store.IsMasterSessionAsync(token, cancellationToken);
                    if (!authenticated) return Results.Ok(new { configured, authenticated, profile = (string?)null, access_token = (string?)null });
                    ApiCredentialView? credential = (await store.ListCredentialsAsync(cancellationToken)).FirstOrDefault();
                    if (credential is null) return Results.Ok(new { configured, authenticated, profile = "MASTER", access_token = (string?)null });
                    AccessTokenResult access = tokens.Create(credential);
                    return Results.Ok(new { configured, authenticated, profile = "MASTER", access_token = access.AccessToken, token_type = "Bearer", expires_in = access.ExpiresIn });
                });
                app.MapPost("/api/auth/setup", async (HttpContext context, MasterLoginRequest request, ConsoleStore store, AccessTokenService tokens, CancellationToken cancellationToken) =>
                {
                    if (!IsTrustedAdministrator(context, ini)) return Results.StatusCode(403);
                    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password)) return Results.BadRequest(new { error = "E-mail e senha são obrigatórios." });
                    if (!await store.ConfigureMasterPasswordAsync(request.Email, request.Password, cancellationToken)) return Results.Conflict(new { error = "O usuário Master já foi configurado." });
                    string session = await store.CreateMasterSessionAsync(cancellationToken);
                    context.Response.Cookies.Append(masterSessionCookie, session, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = false, MaxAge = TimeSpan.FromHours(12) });
                    ApiCredentialView? credential = (await store.ListCredentialsAsync(cancellationToken)).FirstOrDefault();
                    if (credential is null) return Results.Conflict(new { error = "Cadastre uma credencial ativa para gerar o access token." });
                    AccessTokenResult access = tokens.Create(credential);
                    return Results.Ok(new { ok = true, profile = "MASTER", access_token = access.AccessToken, token_type = "Bearer", expires_in = access.ExpiresIn });
                });
                app.MapPost("/api/auth/login", async (HttpContext context, MasterLoginRequest request, ConsoleStore store, AccessTokenService tokens, CancellationToken cancellationToken) =>
                {
                    if (!IsTrustedAdministrator(context, ini)) return Results.StatusCode(403);
                    if (!await store.AuthenticateMasterAsync(request.Email, request.Password, cancellationToken)) return Results.Json(new { error = "E-mail ou senha inválidos." }, statusCode: 401);
                    string session = await store.CreateMasterSessionAsync(cancellationToken);
                    context.Response.Cookies.Append(masterSessionCookie, session, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = false, MaxAge = TimeSpan.FromHours(12) });
                    ApiCredentialView? credential = (await store.ListCredentialsAsync(cancellationToken)).FirstOrDefault();
                    if (credential is null) return Results.Conflict(new { error = "Cadastre uma credencial ativa para gerar o access token." });
                    AccessTokenResult access = tokens.Create(credential);
                    return Results.Ok(new { ok = true, profile = "MASTER", access_token = access.AccessToken, token_type = "Bearer", expires_in = access.ExpiresIn });
                });
                app.MapPost("/api/auth/logout", async (HttpContext context, ConsoleStore store, CancellationToken cancellationToken) =>
                {
                    if (!IsTrustedAdministrator(context, ini)) return Results.StatusCode(403);
                    if (context.Request.Cookies.TryGetValue(masterSessionCookie, out string? token)) await store.RevokeSessionAsync(token, cancellationToken);
                    context.Response.Cookies.Delete(masterSessionCookie);
                    return Results.Ok(new { ok = true });
                });
                app.MapPost("/oauth/token", (Func<HttpRequest, ConsoleStore, AccessTokenService, CancellationToken, Task<IResult>>)async delegate (HttpRequest request, ConsoleStore store, AccessTokenService tokens, CancellationToken cancellationToken)
                {
                    if (!request.HasFormContentType)
                    {
                        return Results.Json(new
                        {
                            error = "invalid_request",
                            error_description = "Use application/x-www-form-urlencoded."
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)400);
                    }
                    IFormCollection formCollection = await request.ReadFormAsync(cancellationToken);
                    if (!string.Equals(formCollection["grant_type"], "client_credentials", StringComparison.Ordinal))
                    {
                        return Results.Json(new
                        {
                            error = "unsupported_grant_type"
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)400);
                    }
                    ApiCredentialView apiCredentialView = await store.ValidateCredentialAsync(formCollection["client_id"].ToString(), formCollection["client_secret"].ToString(), cancellationToken);
                    if ((object)apiCredentialView == null)
                    {
                        return Results.Json(new
                        {
                            error = "invalid_client"
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)401);
                    }
                    AccessTokenResult accessTokenResult;
                    try
                    {
                        accessTokenResult = tokens.Create(apiCredentialView, formCollection["scope"].ToString());
                    }
                    catch (ArgumentException exception)
                    {
                        return Results.Json(new
                        {
                            error = "invalid_scope",
                            error_description = exception.Message
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)400);
                    }

                    request.HttpContext.Response.Headers.CacheControl = "no-store";
                    request.HttpContext.Response.Headers.Pragma = "no-cache";
                    return Results.Ok(new
                    {
                        access_token = accessTokenResult.AccessToken,
                        expires_in = accessTokenResult.ExpiresIn,
                        token_type = "Bearer",
                        scope = accessTokenResult.Scope
                    });
                })
                .WithTags("Autenticação")
                .WithSummary("OAuth padrão: client_credentials por formulário")
                .WithDescription("Recebe application/x-www-form-urlencoded com grant_type=client_credentials, client_id, client_secret e scope opcional.");
                app.MapPost("/oauth/login-simples", async (ClientSecretLoginRequest request, ConsoleStore store, AccessTokenService tokens, CancellationToken cancellationToken) =>
                {
                    ApiCredentialView? credential = await store.ValidateCredentialAsync(request.ClientId, request.ClientSecret, cancellationToken);
                    if (credential is null)
                        return Results.Json(new { error = "invalid_client" }, statusCode: 401);

                    try
                    {
                        AccessTokenResult access = tokens.Create(credential, request.Scope);
                        ClientCredentialContext context = await store.GetCredentialContextAsync(credential.ClientId, cancellationToken);
                        return Results.Ok(new
                        {
                            access_token = access.AccessToken,
                            expires_in = access.ExpiresIn,
                            token_type = "Bearer",
                            scope = access.Scope,
                            credencial = credential.Nome,
                            cnpj = new string(context.Cnpj.Where(char.IsDigit).ToArray()),
                            idFilial = context.IdFilial,
                            idClient = credential.ClientId
                        });
                    }
                    catch (ArgumentException exception)
                    {
                        return Results.Json(new { error = "invalid_scope", error_description = exception.Message }, statusCode: 400);
                    }
                })
                .WithTags("Autenticação")
                .WithSummary("Login simples por Client ID e Client Secret")
                .WithDescription("Endpoint JSON recomendado para aplicações web, integrações e IAs. Gere um Client ID e Client Secret por empresa/integração para evitar reutilização ou duplicidade de usuários. Recebe client_id e client_secret; devolve o Bearer, credencial, CNPJ, idFilial e idClient. Não usa nem altera login, senha ou menus de usuários. O login por usuário e senha continua disponível e é indicado para uso direto da empresa e redes locais.");
                app.MapPost("/api/console/testes/token", (Func<InternalTokenTestRequest, HttpContext, ConsoleStore, AccessTokenService, CancellationToken, Task<IResult>>)async delegate (InternalTokenTestRequest request, HttpContext context, ConsoleStore store, AccessTokenService tokens, CancellationToken cancellationToken)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new
                        {
                            error = "forbidden",
                            error_description = "Console local autorizado obrigatório."
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)403);
                    }
                    ApiCredentialView apiCredentialView = await store.GetActiveCredentialAsync(request.ClientId, cancellationToken);
                    if ((object)apiCredentialView == null)
                    {
                        return Results.Json(new
                        {
                            error = "invalid_client",
                            error_description = "Credencial não encontrada ou inativa."
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)404);
                    }
                    AccessTokenResult accessTokenResult = tokens.Create(apiCredentialView);
                    return Results.Ok(new
                    {
                        access_token = accessTokenResult.AccessToken,
                        expires_in = accessTokenResult.ExpiresIn,
                        token_type = "Bearer",
                        scope = accessTokenResult.Scope,
                        origem = "diagnostico-local"
                    });
                });
                app.MapPost("/oauth/usuario", (Func<LocalUserLoginRequest, HttpContext, ConsoleStore, AccessTokenService, AuditFileService, CancellationToken, Task<IResult>>)async delegate (LocalUserLoginRequest request, HttpContext context, ConsoleStore store, AccessTokenService tokens, AuditFileService audit, CancellationToken cancellationToken)
                {
                    bool simpleLogin = string.IsNullOrWhiteSpace(request.EmpresaId)
                        && string.IsNullOrWhiteSpace(request.Cnpj)
                        && string.IsNullOrWhiteSpace(request.SistemaId);
                    LocalUserAuthentication authentication = simpleLogin
                        ? await store.AuthenticateSimpleLocalUserAsync(request.Usuario, request.Senha, cancellationToken)
                        : await store.AuthenticateLocalUserAsync(
                            request.Usuario, request.Senha, request.EmpresaId, request.Cnpj, cancellationToken, request.SistemaId);
                    if (authentication == null)
                    {
                        // O atraso uniforme reduz tentativas automatizadas e evita enumerar usuários.
                        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                        return Results.Json(new { error = "invalid_credentials" }, statusCode: 401);
                    }

                    if (!authentication.Pagamento.AcessoLiberado)
                    {
                        await WriteLocalLoginAuditAsync(audit, context, authentication, cancellationToken, "login_bloqueado_pagamento");
                        return Results.Json(new { ok = false, erro = "falta de pagamento", codigo = "pagamento_pendente", pagamento = authentication.Pagamento }, statusCode: 402);
                    }

                    await WriteLocalLoginAuditAsync(audit, context, authentication, cancellationToken);

                    // O Login SQLite resolve a credencial internamente e nunca consulta um
                    // banco externo. Todas as permissões da credencial vinculada são inseridas
                    // no token, sem aceitar seleção de escopo pelo chamador.
                    return CreateLocalUserLoginResponse(context, tokens, authentication, includePermissions: true);
                });
                app.MapPost("/executar", (Func<ExecuteRequest, HttpContext, LegacyDispatcher, DatabaseService, LoginVlinkTokenService, ConsoleStore, AccessTokenService, AuditFileService, CancellationToken, Task<IResult>>)async delegate (ExecuteRequest request, HttpContext context, LegacyDispatcher dispatcher, DatabaseService databases, LoginVlinkTokenService loginVlinkTokens, ConsoleStore store, AccessTokenService tokens, AuditFileService audit, CancellationToken cancellationToken)
                {
                    if (request.EffectiveFunction.Equals("login", StringComparison.OrdinalIgnoreCase))
                    {
                        string companyId = request.GetString("empresa_id");
                        string cnpj = request.GetString("cnpj");
                        string systemId = request.GetString("sistema_id");
                        bool simpleLogin = string.IsNullOrWhiteSpace(companyId)
                            && string.IsNullOrWhiteSpace(cnpj)
                            && string.IsNullOrWhiteSpace(systemId);
                        LocalUserAuthentication localAuthentication = simpleLogin
                            ? await store.AuthenticateSimpleLocalUserAsync(request.GetString("usuario"), request.GetString("senha"), cancellationToken)
                            : await store.AuthenticateLocalUserAsync(
                                request.GetString("usuario"), request.GetString("senha"), companyId, cnpj, cancellationToken, systemId);
                        if (localAuthentication == null)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                            return Results.Json(new { ok = false, error = "invalid_credentials" }, statusCode: 401);
                        }

                        if (!localAuthentication.Pagamento.AcessoLiberado)
                        {
                            await WriteLocalLoginAuditAsync(audit, context, localAuthentication, cancellationToken, "login_bloqueado_pagamento");
                            return Results.Json(new { ok = false, erro = "falta de pagamento", codigo = "pagamento_pendente", pagamento = localAuthentication.Pagamento }, statusCode: 402);
                        }

                        await WriteLocalLoginAuditAsync(audit, context, localAuthentication, cancellationToken);

                        return CreateLocalUserLoginResponse(context, tokens, localAuthentication, includePermissions: true);
                    }

                    if (request.EffectiveFunction.Equals("loginvlink", StringComparison.OrdinalIgnoreCase))
                    {
                        // As credenciais VLINK podem ser rotacionadas pelo MASTER no INI.
                        // A releitura ocorre por tentativa para aplicar a troca sem reiniciar o serviço.
                        ini.Reload();
                        bool validExternalUser = await databases.ValidateExternalUserAsync(
                            request,
                            cancellationToken);
                        if (!validExternalUser)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                            return Results.Json(new
                            {
                                ok = false,
                                error = "invalid_credentials"
                            }, (JsonSerializerOptions?)null, (string?)null, (int?)401);
                        }

                        LoginVlinkTokens result = await loginVlinkTokens.RequestTokensAsync(cancellationToken);
                        context.Response.Headers.CacheControl = "no-store";
                        context.Response.Headers.Pragma = "no-cache";
                        return Results.Ok(new
                        {
                            access_token = result.AccessTokenSql,
                            expires_in = result.ExpiresInSql,
                            access_token_nfe = result.AccessTokenNfe,
                            expires_in_nfe = result.ExpiresInNfe,
                            token_type = "Bearer"
                        });
                    }

                    if (request.EffectiveFunction.Equals("obtertoken", StringComparison.OrdinalIgnoreCase))
                    {
                        // A renovação exige um Bearer ainda válido. O cliente nunca envia nem
                        // recebe Client Secret: a credencial ativa é resolvida no SQLite local.
                        if (!context.Items.TryGetValue(nameof(AccessTokenValidation), out object renewalTokenData)
                            || renewalTokenData is not AccessTokenValidation renewalToken)
                        {
                            return Results.Json(new
                            {
                                ok = false,
                                erro = "token invalido"
                            }, statusCode: 401);
                        }

                        ApiCredentialView? activeCredential = await store.GetActiveCredentialAsync(
                            renewalToken.ClientId,
                            cancellationToken);
                        if (activeCredential is null)
                        {
                            return Results.Json(new
                            {
                                ok = false,
                                erro = "credencial inativa"
                            }, statusCode: 401);
                        }

                        // Mantém exatamente os escopos do Bearer recebido para impedir que uma
                        // renovação amplie permissões quando a credencial for alterada no servidor.
                        return CreateLoginTokenResponse(
                            context,
                            tokens,
                            activeCredential,
                            string.Join(" ", renewalToken.Scopes));
                    }

                    // O Bearer válido já autoriza a chamada. A API não bloqueia por escopo,
                    // origem, IP ou máquina depois que o login gerou o access token.

                    try
                    {
                        return Results.Ok(await dispatcher.ExecuteAsync(request, IsTrustedAdministrator(context, ini), cancellationToken));
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = ex.Message
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)403);
                    }
                    catch (KeyNotFoundException ex2)
                    {
                        return Results.NotFound(new
                        {
                            ok = false,
                            erro = ex2.Message
                        });
                    }
                    catch (Exception ex3) when (((ex3 is InvalidDataException || ex3 is InvalidOperationException || ex3 is NotSupportedException || ex3 is FileNotFoundException) ? 1 : 0) != 0)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = ex3.Message
                        });
                    }
                });
                app.MapPost("/multibanco/consultar", (MultibankQueryRequest body, HttpContext context, LegacyDispatcher dispatcher, CancellationToken token) =>
                    ExecuteMultibankEndpointAsync(ToExecuteRequest(body), "consultar_multibanco", IsTrustedAdministrator(context, ini), dispatcher, token))
                    .WithTags("Multibanco").WithSummary("Consultar — multibanco")
                    .WithDescription("Consulta estruturada em SQLite, PostgreSQL, SQL Server, MySQL, MariaDB, Oracle ou HFSQL. Envie Bearer token; não envie SQL nem funcao.");
                app.MapPost("/multibanco/inserir", (MultibankInsertRequest body, HttpContext context, LegacyDispatcher dispatcher, CancellationToken token) =>
                    ExecuteMultibankEndpointAsync(ToExecuteRequest(body), "inserir_multibanco", IsTrustedAdministrator(context, ini), dispatcher, token))
                    .WithTags("Multibanco").WithSummary("Inserir — multibanco")
                    .WithDescription("Insere dados com SQL parametrizado gerado pela API para o banco informado. Envie Bearer token; não envie SQL nem funcao.");
                app.MapPost("/multibanco/alterar", (MultibankUpdateRequest body, HttpContext context, LegacyDispatcher dispatcher, CancellationToken token) =>
                    ExecuteMultibankEndpointAsync(ToExecuteRequest(body), "alterar_multibanco", IsTrustedAdministrator(context, ini), dispatcher, token))
                    .WithTags("Multibanco").WithSummary("Alterar — multibanco")
                    .WithDescription("Altera dados com SQL parametrizado. O objeto where é obrigatório. Envie Bearer token; não envie SQL nem funcao.");
                app.MapPost("/multibanco/excluir", (MultibankDeleteRequest body, HttpContext context, LegacyDispatcher dispatcher, CancellationToken token) =>
                    ExecuteMultibankEndpointAsync(ToExecuteRequest(body), "excluir_multibanco", IsTrustedAdministrator(context, ini), dispatcher, token))
                    .WithTags("Multibanco").WithSummary("Excluir — multibanco")
                    .WithDescription("Exclui dados com SQL parametrizado. O objeto where é obrigatório. Envie Bearer token; não envie SQL nem funcao.");
                app.MapPost("/multibanco/estrutura", async (MultibankSchemaRequest body, DatabaseService databases, CancellationToken token) =>
                {
                    try
                    {
                        return Results.Ok(await databases.EnsureStructureAsync(
                            body.DatabaseType, body.Server, body.Database, body.Table, body.Columns, token));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or NotSupportedException or FileNotFoundException)
                    {
                        return Results.BadRequest(new { ok = false, erro = exception.Message });
                    }
                })
                    .WithTags("Multibanco").WithSummary("Criar ou adequar estrutura — multibanco")
                    .WithDescription("Compara tabela, campos, tipos e tamanhos. Cria tabela ausente, adiciona campos ausentes e ajusta divergências com o dialeto de SQLite, PostgreSQL, SQL Server, MySQL, MariaDB, Oracle ou HFSQL. Tipos genéricos: inteiro, inteiro_longo, decimal, booleano, data, datahora, texto, texto_curto, binario e uuid. Exemplo: { \"tipo_banco\": \"postgresql\", \"banco\": \"GOURMET\", \"tabela\": \"subcategorias\", \"campos\": [{ \"nome\": \"id\", \"tipo\": \"inteiro_longo\", \"chave_primaria\": true, \"auto_incremento\": true, \"nulo\": false }, { \"nome\": \"nome\", \"tipo\": \"texto_curto\", \"tamanho\": 120, \"nulo\": false }] }. Exige Authorization: Bearer válido.");
                app.MapPost("/multibanco/estrutura/api", async (MultibankApiSchemaRequest body, DatabaseService databases, CancellationToken token) =>
                {
                    try
                    {
                        return Results.Ok(await databases.EnsureInstalledApiStructureAsync(
                            body.DatabaseType, body.Server, body.Database, body.Tables, token));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or NotSupportedException or FileNotFoundException)
                    {
                        return Results.BadRequest(new { ok = false, erro = exception.Message });
                    }
                })
                    .WithTags("Multibanco").WithSummary("Publicar estrutura do SQLite da API")
                    .WithDescription("Lê todas as tabelas atuais de Dados/facilapp_sql.db e adequa o banco destino. Não exige declarar campos; após cada atualização da estrutura da API, a chamada reflete automaticamente os campos atuais. Exige Authorization: Bearer válido.");
                app.MapPost("/scripts/executar", async (PowerShellScriptRequest body, PowerShellScriptService scripts, CancellationToken token) =>
                {
                    try
                    {
                        object result = await scripts.ExecuteAsync(body.Arquivo, token);
                        return Results.Ok(result);
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        return Results.Json(new { ok = false, erro = exception.Message }, statusCode: 403);
                    }
                    catch (FileNotFoundException exception)
                    {
                        return Results.NotFound(new { ok = false, erro = exception.Message });
                    }
                    catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or TimeoutException)
                    {
                        return Results.BadRequest(new { ok = false, erro = exception.Message });
                    }
                })
                    .WithTags("Scripts PS1")
                    .WithSummary("Executa um arquivo PowerShell autorizado")
                    .WithDescription("Recebe caminho\\arquivo.ps1 e executa somente um arquivo .ps1 existente. Aceita caminho local ou UNC, por exemplo \\\\servidor\\Scripts\\Migrar.ps1, desde que a pasta esteja em [Scripts] DiretoriosPermitidos. Caminhos múltiplos são separados por ponto e vírgula. Não aceita comandos PowerShell no JSON. Funciona por localhost, LAN, internet ou Tailscale e exige Authorization: Bearer válido.");
                app.MapPost("/webhook/zapi", (Func<ZApiWebhookRequest, HttpContext, IntegrationService, CancellationToken, Task<IResult>>)async delegate (ZApiWebhookRequest webhook, HttpContext context, IntegrationService integrations, CancellationToken cancellationToken)
                {
                    string text = ini.Get("WHATSAPP_ZAPI", "WEBHOOK_SECRET");
                    string right = context.Request.Headers["X-Webhook-Secret"].ToString();
                    if (text.Length == 0 || !SecureEquals(text, right))
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = "webhook recusado"
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)401);
                    }
                    if (webhook.IsStatusReply || webhook.IsEdit || webhook.IsNewsletter || string.IsNullOrWhiteSpace(webhook.Text.Message))
                    {
                        return Results.Ok(new
                        {
                            ok = true,
                            ignorado = true
                        });
                    }
                    string destination = webhook.Phone;
                    string text2 = IntegrationService.ExtractOpenAiText(await integrations.AskOpenAiAsync(webhook.Text.Message, webSearch: false, structured: false, cancellationToken));
                    if (string.IsNullOrWhiteSpace(text2))
                    {
                        text2 = "Não consegui responder agora.";
                    }
                    string message = "[FACILAPP]" + Environment.NewLine + text2;
                    return Results.Ok(new
                    {
                        ok = true,
                        dados = await integrations.SendZApiTextAsync(destination, message, cancellationToken)
                    });
                });
                app.MapGet("/api/console/empresas", (Func<ConsoleStore, CancellationToken, Task<IResult>>)(async (ConsoleStore store, CancellationToken token) => Results.Ok(new
                {
                    ok = true,
                    empresas = await store.ListCompaniesAsync(token)
                })));
                app.MapGet("/api/console/empresas/{cnpj}", (Func<string, ConsoleStore, CancellationToken, Task<IResult>>)async delegate (string cnpj, ConsoleStore store, CancellationToken token)
                {
                    CompanyView companyView = await store.GetCompanyAsync(cnpj, token);
                    return ((object)companyView != null) ? Results.Ok(new
                    {
                        ok = true,
                        empresa = companyView
                    }) : Results.NotFound(new
                    {
                        ok = false,
                        erro = "empresa não encontrada"
                    });
                });
                app.MapGet("/api/console/empresas/{cnpj}/logo", async (string cnpj, ConsoleStore store, CancellationToken token) =>
                {
                    CompanyLogo? logo = await store.GetCompanyLogoAsync(cnpj, token);
                    return logo is null
                        ? Results.NotFound(new { ok = false, erro = "logo não encontrada" })
                        : Results.File(logo.Data, logo.MimeType, enableRangeProcessing: false);
                });
                app.MapGet("/api/console/empresas/consultar-cnpj/{cnpj}", (Func<string, CnpjLookupService, CancellationToken, Task<IResult>>)async delegate (string cnpj, CnpjLookupService lookup, CancellationToken token)
                {
                    try
                    {
                        return Results.Ok(new
                        {
                            ok = true,
                            empresa = await lookup.LookupAsync(cnpj, token)
                        });
                    }
                    catch (InvalidDataException ex)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = ex.Message
                        });
                    }
                    catch (HttpRequestException ex2)
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = "Consulta pública indisponível: " + ex2.Message
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)502);
                    }
                    catch (JsonException)
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = "O serviço público retornou dados inválidos."
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)502);
                    }
                });
                app.MapGet("/api/console/pessoas/consulta-cpf/status", (IniConfiguration configuration) => Results.Ok(new
                {
                    ok = true,
                    ativo = configuration.GetBoolean("FonteDataCPF", "Ativo", false)
                }))
                .WithTags("Pessoas Físicas")
                .WithSummary("Informa se a consulta CPF está ativa");
                app.MapGet("/api/console/pessoas/consultar-cpf/{cpf}", async (string cpf, CpfLookupService lookup, CancellationToken token) =>
                {
                    try
                    {
                        PublicCpfLookupResponse consulta = await lookup.LookupAsync(cpf, token);
                        return Results.Ok(new { ok = true, pessoa = consulta.Pessoa, retorno = consulta.Retorno });
                    }
                    catch (InvalidDataException ex)
                    {
                        return Results.BadRequest(new { ok = false, erro = ex.Message });
                    }
                    catch (InvalidOperationException ex)
                    {
                        return Results.Json(new { ok = false, erro = ex.Message }, statusCode: 503);
                    }
                    catch (HttpRequestException ex)
                    {
                        return Results.Json(new { ok = false, erro = "Consulta FonteData indisponível: " + ex.Message }, statusCode: 502);
                    }
                    catch (JsonException)
                    {
                        return Results.Json(new { ok = false, erro = "A FonteData retornou dados inválidos." }, statusCode: 502);
                    }
                })
                .WithTags("Pessoas Físicas")
                .WithSummary("Consulta CPF na FonteData")
                .WithDescription("Recebe CPF com ou sem máscara e usa exclusivamente [FonteDataCPF]. A credencial permanece somente no FacilAppSQL.ini do servidor.");
                /// <summary>Consulta um CNPJ e devolve o JSON integral recebido da fonte pública, sem redução de campos.</summary>
                app.MapGet("/api/console/empresas/consultar-cnpj-completo/{cnpj}", (Func<string, CnpjLookupService, CancellationToken, Task<IResult>>)async delegate (string cnpj, CnpjLookupService lookup, CancellationToken token)
                {
                    try
                    {
                        PublicCnpjRawResponse consulta = await lookup.LookupRawAsync(cnpj, token);
                        return Results.Ok(new
                        {
                            ok = true,
                            cnpj = consulta.Cnpj,
                            fonte = consulta.Fonte,
                            retorno = consulta.Retorno
                        });
                    }
                    catch (InvalidDataException ex)
                    {
                        return Results.BadRequest(new { ok = false, erro = ex.Message });
                    }
                    catch (HttpRequestException ex)
                    {
                        return Results.Json(new { ok = false, erro = "Consulta pública indisponível: " + ex.Message }, statusCode: 502);
                    }
                    catch (JsonException)
                    {
                        return Results.Json(new { ok = false, erro = "O serviço público retornou dados inválidos." }, statusCode: 502);
                    }
                })
                .WithTags("Empresas")
                .WithSummary("Consulta CNPJ e retorna o JSON integral da fonte pública")
                .WithDescription("Mantém a consulta normalizada existente e expõe, nesta rota separada, todos os campos do JSON respondido pela BrasilAPI ou CNPJ.ws. Exemplo: GET /api/console/empresas/consultar-cnpj-completo/57202784000158.");
                app.MapPost("/api/console/empresas", (Func<CompanyRequest, ConsoleStore, CancellationToken, Task<IResult>>)(async (CompanyRequest request, ConsoleStore store, CancellationToken token) => Results.Ok(new
                {
                    ok = true,
                    empresa = await store.SaveCompanyAsync(request, token)
                })));
                app.MapPut("/api/console/empresas/{cnpj}", (Func<string, CompanyRequest, ConsoleStore, CancellationToken, Task<IResult>>)(async (string cnpj, CompanyRequest request, ConsoleStore store, CancellationToken token) => (!(OnlyDigits(cnpj) != OnlyDigits(request.Cnpj))) ? Results.Ok(new
                {
                    ok = true,
                    empresa = await store.SaveCompanyAsync(request, token)
                }) : Results.BadRequest(new
                {
                    ok = false,
                    erro = "CNPJ divergente"
                })));
                app.MapGet("/api/console/empresas/{cnpj}/pagamentos", async (string cnpj, HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (context.Items[nameof(AccessTokenValidation)] is not AccessTokenValidation { IsValid: true })
                        return Results.Json(new { ok = false, erro = "token invalido" }, statusCode: 401);
                    CompanyView? company = await store.GetCompanyAsync(cnpj, token);
                    if (company is null) return Results.NotFound(new { ok = false, erro = "Empresa não encontrada." });
                    CompanyPaymentStatus? status = await store.GetCompanyPaymentStatusAsync(company.Id, token);
                    return Results.Ok(new { ok = true, empresa = company, pagamento = status, pagamentos = await store.ListCompanyPaymentsAsync(cnpj, token) });
                })
                    .WithTags("Mensalidades")
                    .WithSummary("Consulta pagamentos e situação da empresa")
                    .WithDescription("Retorna o tipo de pagamento da empresa, histórico, último pagamento e limite de 45 dias. Exige Authorization: Bearer válido.");
                app.MapGet("/api/console/mensalidades", async (HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (context.Items[nameof(AccessTokenValidation)] is not AccessTokenValidation { IsValid: true })
                        return Results.Json(new { ok = false, erro = "token invalido" }, statusCode: 401);
                    return Results.Ok(new { ok = true, mensalidades = await store.ListCompanyPaymentOverviewsAsync(token) });
                })
                    .WithTags("Mensalidades")
                    .WithSummary("Lista a situação de mensalidade das empresas")
                    .WithDescription("Retorna um browse com todas as empresas, modelo vitalício/mensalidade, último pagamento e limite de 45 dias. Exige Authorization: Bearer válido.");
                app.MapPost("/api/console/empresas/{cnpj}/pagamentos", async (string cnpj, CompanyPaymentRequest request, HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (context.Items[nameof(AccessTokenValidation)] is not AccessTokenValidation { IsValid: true })
                        return Results.Json(new { ok = false, erro = "token invalido" }, statusCode: 401);
                    try
                    {
                        CompanyPaymentView payment = await store.RegisterCompanyPaymentAsync(cnpj, request, token);
                        CompanyView? company = await store.GetCompanyAsync(cnpj, token);
                        CompanyPaymentStatus? status = company is null ? null : await store.GetCompanyPaymentStatusAsync(company.Id, token);
                        return Results.Ok(new { ok = true, pagamento = payment, situacao = status });
                    }
                    catch (KeyNotFoundException exception) { return Results.NotFound(new { ok = false, erro = exception.Message }); }
                    catch (InvalidDataException exception) { return Results.BadRequest(new { ok = false, erro = exception.Message }); }
                })
                    .WithTags("Mensalidades")
                    .WithSummary("Registra pagamento de mensalidade")
                    .WithDescription("Grava data_pagamento e valor no histórico da empresa. A partir da data informada, a mensalidade permite login por até 45 dias. Exige Authorization: Bearer válido.");
                /// <summary>Exclui definitivamente uma empresa e seus cadastros dependentes.</summary>
                app.MapDelete("/api/console/empresas/{cnpj}", async (string cnpj, HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo local obrigatório." }, statusCode: 403);
                    }

                    return await store.DeleteCompanyAsync(cnpj, token)
                        ? Results.Ok(new { ok = true })
                        : Results.NotFound(new { ok = false, erro = "Empresa não encontrada." });
                });
                app.MapGet("/api/console/credenciais", (Func<ConsoleStore, CancellationToken, Task<IResult>>)(async (ConsoleStore store, CancellationToken token) => Results.Ok(new
                {
                    ok = true,
                    credenciais = await store.ListCredentialsAsync(token)
                })));
                app.MapGet("/api/console/credenciadores", (Func<HttpContext, ConsoleStore, CancellationToken, Task<IResult>>)async delegate (HttpContext context, ConsoleStore store, CancellationToken token)
                {
                    if (!IsTrustedAdministrator(context, ini)) return Results.StatusCode(403);
                    return Results.Ok(new { ok = true, credenciadores = await store.ListCredentialersAsync(token) });
                });
                app.MapPost("/api/console/credenciais", (Func<ApiCredentialRequest, ConsoleStore, CancellationToken, Task<IResult>>)(async (ApiCredentialRequest request, ConsoleStore store, CancellationToken token) => Results.Ok(new
                {
                    ok = true,
                    resultado = await store.CreateCredentialAsync(request, token)
                })));
                app.MapPut("/api/console/credenciais/{clientId}", (Func<string, ApiCredentialRequest, ConsoleStore, CancellationToken, Task<IResult>>)(async (string clientId, ApiCredentialRequest request, ConsoleStore store, CancellationToken token) =>
                {
                    ApiCredentialView? credencial = await store.UpdateCredentialAsync(clientId, request, token);
                    return credencial is not null
                        ? Results.Ok(new { ok = true, resultado = credencial })
                        : Results.NotFound(new { ok = false, erro = "credencial não encontrada" });
                }));
                app.MapDelete("/api/console/credenciais/{clientId}", (Func<string, ConsoleStore, CancellationToken, Task<IResult>>)(async (string clientId, ConsoleStore store, CancellationToken token) => (await store.RevokeCredentialAsync(clientId, token)) ? Results.Ok(new
                {
                    ok = true,
                    mensagem = "credencial excluída"
                }) : Results.NotFound(new
                {
                    ok = false,
                    erro = "credencial não encontrada"
                })));
                app.MapGet("/api/console/empresas/{empresaId}/usuarios", (Func<string, HttpContext, ConsoleStore, CancellationToken, Task<IResult>>)async delegate (string empresaId, HttpContext context, ConsoleStore store, CancellationToken token)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo local obrigatório." }, statusCode: 403);
                    }

                    return Results.Ok(new
                    {
                        ok = true,
                        usuarios = await store.ListLocalUsersAsync(empresaId, token)
                    });
                });
                /// <summary>Consulta um usuário específico da empresa selecionada.</summary>
                app.MapGet("/api/console/empresas/{empresaId}/usuarios/{id}", async (string empresaId, string id, HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo local obrigatório." }, statusCode: 403);
                    }

                    LocalUserView? usuario = (await store.ListLocalUsersAsync(empresaId, token))
                        .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                    return usuario is null
                        ? Results.NotFound(new { ok = false, erro = "Usuário não encontrado nesta empresa." })
                        : Results.Ok(new { ok = true, usuario });
                });
                app.MapPost("/api/console/empresas/{empresaId}/usuarios", (Func<string, LocalUserRequest, HttpContext, ConsoleStore, CancellationToken, Task<IResult>>)async delegate (string empresaId, LocalUserRequest request, HttpContext context, ConsoleStore store, CancellationToken token)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo local obrigatório." }, statusCode: 403);
                    }

                    request.EmpresaId = empresaId;
                    return Results.Ok(new
                    {
                        ok = true,
                        usuario = await store.CreateLocalUserAsync(request, token)
                    });
                });
                app.MapGet("/api/console/sistemas", async (HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo obrigatório." }, statusCode: 403);
                    }
                    return Results.Ok(new { ok = true, sistemas = await store.ListSystemsAsync(token) });
                });
                app.MapPost("/api/console/sistemas", async (SystemRequest request, HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo obrigatório." }, statusCode: 403);
                    }
                    try { return Results.Ok(new { ok = true, sistema = await store.CreateSystemAsync(request, token) }); }
                    catch (InvalidDataException exception) { return Results.BadRequest(new { ok = false, erro = exception.Message }); }
                });
                app.MapPut("/api/console/sistemas/{id}", async (string id, SystemRequest request, HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo obrigatório." }, statusCode: 403);
                    }
                    return await store.UpdateSystemAsync(id, request, token)
                        ? Results.Ok(new { ok = true })
                        : Results.NotFound(new { ok = false, erro = "Sistema não encontrado." });
                });
                app.MapDelete("/api/console/sistemas/{id}", async (string id, HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo obrigatório." }, statusCode: 403);
                    }
                    return await store.DisableSystemAsync(id, token)
                        ? Results.Ok(new { ok = true, desativado = true })
                        : Results.NotFound(new { ok = false, erro = "Sistema não encontrado." });
                });
                app.MapDelete("/api/console/usuarios/{id}", (Func<string, HttpContext, ConsoleStore, CancellationToken, Task<IResult>>)async delegate (string id, HttpContext context, ConsoleStore store, CancellationToken token)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo local obrigatório." }, statusCode: 403);
                    }

                    return await store.DeleteLocalUserAsync(id, token)
                        ? Results.Ok(new { ok = true })
                        : Results.NotFound(new { ok = false, erro = "Usuário não encontrado." });
                });
                app.MapPut("/api/console/empresas/{empresaId}/usuarios/{id}/senha", (Func<string, string, LocalUserPasswordRequest, HttpContext, ConsoleStore, CancellationToken, Task<IResult>>)async delegate (string empresaId, string id, LocalUserPasswordRequest request, HttpContext context, ConsoleStore store, CancellationToken token)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo local obrigatório." }, statusCode: 403);
                    }

                    return await store.UpdateLocalUserPasswordAsync(empresaId, id, request.Senha, token)
                        ? Results.Ok(new { ok = true })
                        : Results.NotFound(new { ok = false, erro = "Usuário não encontrado nesta empresa." });
                });
                /// <summary>Atualiza nome, login, credencial e senha opcional de um usuário da empresa.</summary>
                app.MapPut("/api/console/empresas/{empresaId}/usuarios/{id}", async (string empresaId, string id, LocalUserRequest request, HttpContext context, ConsoleStore store, AuditFileService audit, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo local obrigatório." }, statusCode: 403);
                    }

                    LocalUserView? anterior = (await store.ListLocalUsersAsync(empresaId, token))
                        .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                    request.EmpresaId = empresaId;
                    bool atualizado = await store.UpdateLocalUserAsync(empresaId, id, request, token);
                    await audit.WriteAsync(new
                    {
                        data_hora = DateTimeOffset.Now,
                        evento = "usuario_alterado",
                        empresa_id = empresaId,
                        usuario_id = id,
                        executado_por = AuditUser(context),
                        sessao = AuditSessionId(context),
                        anterior,
                        atual = atualizado ? new
                        {
                            request.Nome,
                            request.Usuario,
                            request.ClientId,
                            request.MenuData,
                            request.MenuSuperiorData,
                            request.DashBoardData
                        } : null,
                        resultado = atualizado ? "atualizado" : "não encontrado"
                    }, token);
                    return atualizado
                        ? Results.Ok(new { ok = true })
                        : Results.NotFound(new { ok = false, erro = "Usuário não encontrado nesta empresa." });
                });
                /// <summary>Consulta os menus permitidos de um usuário da empresa selecionada.</summary>
                app.MapGet("/api/console/empresas/{empresaId}/usuarios/{id}/menus", async (string empresaId, string id, HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo local obrigatório." }, statusCode: 403);
                    }

                    LocalUserMenuView? menus = await store.GetLocalUserMenusAsync(empresaId, id, token);
                    return menus is null
                        ? Results.NotFound(new { ok = false, erro = "Usuário não encontrado nesta empresa." })
                        : Results.Ok(new { ok = true, menus });
                });
                /// <summary>Grava os menus permitidos de um usuário da empresa selecionada.</summary>
                app.MapPut("/api/console/empresas/{empresaId}/usuarios/{id}/menus", async (string empresaId, string id, LocalUserMenuRequest request, HttpContext context, ConsoleStore store, AuditFileService audit, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new { ok = false, erro = "Acesso administrativo local obrigatório." }, statusCode: 403);
                    }

                    LocalUserMenuView? anterior = await store.GetLocalUserMenusAsync(empresaId, id, token);
                    bool atualizado = await store.UpdateLocalUserMenusAsync(empresaId, id, request, token);
                    object atualMenus = new
                    {
                        menu_data = request.MenuData,
                        menu_superior_data = request.MenuSuperiorData,
                        dashboard_data = request.DashBoardData
                    };
                    await audit.WriteAsync(new
                    {
                        data_hora = DateTimeOffset.Now,
                        evento = "menus_usuario_alterados",
                        empresa_id = empresaId,
                        usuario_id = id,
                        executado_por = AuditUser(context),
                        sessao = AuditSessionId(context),
                        anterior,
                        atual = atualizado ? atualMenus : null,
                        resultado = atualizado ? "atualizado" : "não encontrado"
                    }, token);
                    if (atualizado)
                    {
                        await audit.WriteMenuBackupAsync(
                            empresaId,
                            id,
                            AuditUser(context),
                            AuditSessionId(context),
                            anterior!,
                            atualMenus,
                            token);
                    }
                    return atualizado
                        ? Results.Ok(new { ok = true })
                        : Results.NotFound(new { ok = false, erro = "Usuário não encontrado nesta empresa." });
                });
                /// <summary>
                /// Aplica MenuData, MenuSuperiorData e DashBoardData aos usuários
                /// ativos de um SQLite, sempre com backup anterior à gravação.
                /// </summary>
                app.MapPut("/api/console/menus/aplicar-sqlite", async (
                    MenuProfileUpdateRequest request,
                    HttpContext context,
                    DatabaseService databases,
                    CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(
                            new { ok = false, erro = "Acesso administrativo local obrigatório." },
                            statusCode: 403);
                    }

                    try
                    {
                        object result = await databases.ApplySqliteMenuProfileAsync(
                            request.Banco,
                            request.MenuData,
                            request.MenuSuperiorData,
                            request.DashBoardData,
                            token);
                        return Results.Ok(result);
                    }
                    catch (FileNotFoundException exception)
                    {
                        return Results.NotFound(new { ok = false, erro = exception.Message });
                    }
                    catch (InvalidDataException exception)
                    {
                        return Results.BadRequest(new { ok = false, erro = exception.Message });
                    }
                })
                .WithName("AplicarMenusSQLite")
                .WithSummary("Aplica os três catálogos de menu em um SQLite")
                .WithDescription(
                    "Recebe as listas MenuData, MenuSuperiorData e DashBoardData, " +
                    "cria backup do banco e atualiza todos os usuários ativos. " +
                    "Requer uma sessão administrativa local da console.");
                app.MapGet("/api/console/dashboard", (Func<ConsoleStore, CancellationToken, Task<IResult>>)(async (ConsoleStore store, CancellationToken token) => Results.Ok(new
                {
                    ok = true,
                    dashboard = await store.GetDashboardAsync(token)
                })));
                app.MapGet("/api/console/empresas/{cnpj}/conexoes", (Func<string, ConnectionProfileStore, CancellationToken, Task<IResult>>)(async (string cnpj, ConnectionProfileStore store, CancellationToken token) => Results.Ok(new
                {
                    ok = true,
                    conexoes = await store.ListAsync(cnpj, token)
                })));
                app.MapPost("/api/console/empresas/{cnpj}/conexoes", (Func<string, ConnectionProfileRequest, ConnectionProfileStore, CancellationToken, Task<IResult>>)(async (string cnpj, ConnectionProfileRequest request, ConnectionProfileStore store, CancellationToken token) => Results.Ok(new
                {
                    ok = true,
                    conexao = await store.SaveAsync(cnpj, request, token)
                })));
                app.MapGet("/api/console/configuracoes", (Func<HttpContext, IResult>)delegate (HttpContext context)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = "configuração permitida somente no servidor autorizado."
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)403);
                    }
                    int num = ini.GetInt("ServidorHTTP", "Porta", 5050);
                    string[] source = new string[7] { "sqlserver", "postgresql", "mariadb", "mysql", "oracle", "hfsql", "sqlite" };
                    var bancos = source.Select(delegate (string type)
                    {
                        var (tipo, section) = DatabaseService.NormalizeDatabaseType(type);
                        return new
                        {
                            tipo = tipo,
                            ativo = ini.GetBoolean(section, "Ativo"),
                            servidor = ini.Get(section, "Servidor", "127.0.0.1"),
                            porta = ini.GetInt(section, "Porta", 0),
                            banco = ini.Get(section, "Banco"),
                            usuario = ini.Get(section, "Usuario"),
                            dsn = ini.Get(section, "DSN"),
                            diretorio = ini.Get(section, "Diretorio", tipo == "sqlite" ? "BasesSQLite" : string.Empty),
                            senhaConfigurada = (ini.Get(section, "Senha").Length > 0)
                        };
                    });
                    var geral = new
                    {
                        nomeInstancia = ini.Get("Instancia", "Nome", "FacilApp SQL"),
                        nomeServicoWindows = ini.Get("Servico", "NomeWindows", "FacilAppSQL"),
                        servidorAtivo = ini.GetBoolean("ServidorHTTP", "Ativo", fallback: true),
                        porta = num,
                        timeoutSegundos = ini.GetInt("ServidorHTTP", "TimeoutSegundos", 30),
                        baseUrlLocal = $"http://127.0.0.1:{num}",
                        limiteCorpoKb = ini.GetInt("ServidorHTTP", "LimiteCorpoKB", 1024),
                        maximoConexoes = ini.GetInt("ServidorHTTP", "MaxConexoes", 500),
                        ponteClaudeAtiva = ini.GetBoolean("PonteClaude", "Ativo", fallback: true),
                        portaPonteClaude = ini.GetInt("PonteClaude", "Porta", 3000),
                        baseUrlPonteClaude = $"http://127.0.0.1:{ini.GetInt("PonteClaude", "Porta", 3000)}",
                        tokenPonteClaudeConfigurado = ini.Get("PonteClaude", "Token").Length > 0,
                        exigirToken = ini.GetBoolean("Seguranca", "ExigirToken", fallback: true),
                        consultaLivre = ini.GetBoolean("Seguranca", "ConsultaLivre", fallback: true),
                        tokenConfigurado = (ini.Get("Seguranca", "Token").Length > 0),
                        permitirLocalhost = ini.GetBoolean("Rede", "PermitirLocalhost", fallback: true),
                        permitirRedeLocal = ini.GetBoolean("Rede", "PermitirRedeLocal", fallback: true),
                        permitirTailscale = ini.GetBoolean("Rede", "PermitirTailscale", fallback: true),
                        logAtivo = ini.GetBoolean("Log", "Ativo", fallback: true),
                        nivelLog = ini.Get("Log", "Nivel", "INFO"),
                        retencaoDias = ini.GetInt("Log", "RetencaoDias", 30),
                        arquivosAtivos = ini.GetBoolean("Arquivos", "Ativo", fallback: true),
                        tamanhoMaximoArquivoMb = ini.GetInt("Arquivos", "TamanhoMaximoMB", 15),
                        diretorioBaseArquivos = ini.Get("Arquivos", "DiretorioBase", "Repositorio"),
                        extensoesPermitidas = ini.Get("Arquivos", "ExtensoesPermitidas", "pdf,jpg,jpeg,png,webp,xml,ini,js,json,txt,csv")
                    };
                    var integracoes = new
                    {
                        mapaUserAgent = ini.Get("OPENSTREETMAP", "USER_AGENT", "FacilAppSQL/1.0"),
                        zApiAtiva = ini.GetBoolean("WHATSAPP_ZAPI", "Ativo"),
                        zApiBaseUrl = ini.Get("WHATSAPP_ZAPI", "BASE_URL", "https://api.z-api.io"),
                        zApiInstanceId = ini.Get("WHATSAPP_ZAPI", "INSTANCE_ID"),
                        zApiInstanceTokenConfigurado = (ini.Get("WHATSAPP_ZAPI", "INSTANCE_TOKEN").Length > 0),
                        zApiClientTokenConfigurado = (ini.Get("WHATSAPP_ZAPI", "CLIENT_TOKEN").Length > 0),
                        alertasAtivos = ini.GetBoolean("ALERTAS", "Ativo"),
                        numeroFacilApp = ini.Get("ALERTAS", "NumeroFacilApp")
                    };
                    return Results.Ok(new
                    {
                        ok = true,
                        geral = geral,
                        bancos = bancos,
                        integracoes = integracoes,
                        camposIni = IniFieldCatalog.Fields.Select(field => new
                        {
                            secao = field.Section,
                            chave = field.Key,
                            valorPadrao = field.IsSecret ? "" : field.DefaultValue,
                            segredo = field.IsSecret,
                            exigeReinicio = field.RequiresRestart,
                            descricao = field.Description
                        })
                    });
                });
                app.MapGet("/api/console/configuracoes/campos-ini", (HttpContext context) =>
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = "configuração permitida somente no servidor autorizado."
                        }, statusCode: StatusCodes.Status403Forbidden);
                    }

                    return Results.Ok(new
                    {
                        ok = true,
                        campos = IniFieldCatalog.Fields.Select(field => new
                        {
                            secao = field.Section,
                            chave = field.Key,
                            valorPadrao = field.IsSecret ? "" : field.DefaultValue,
                            segredo = field.IsSecret,
                            exigeReinicio = field.RequiresRestart,
                            descricao = field.Description
                        })
                    });
                })
                    .WithTags("Console - Configuração")
                    .WithSummary("Lista todos os campos reconhecidos do FacilAppSQL.ini")
                    .WithDescription("Retorna metadados e valores padrão não secretos. Senhas, tokens e Client Secrets nunca são devolvidos.");
                app.MapGet("/api/console/configura-vlink", async (HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini)
                        || !context.Request.Cookies.TryGetValue(masterSessionCookie, out string? session)
                        || !await store.IsMasterSessionAsync(session, token))
                    {
                        return Results.StatusCode(403);
                    }

                    return Results.Ok(new
                    {
                        sqlClientId = ini.Get("LoginVlink", "SqlClientId"),
                        sqlConfigurado = ini.Get("LoginVlink", "SqlClientId").Length > 0,
                        nfeConfigurado = ini.Get("LoginVlink", "NfeClientId").Length > 0 && ini.Get("LoginVlink", "NfeClientSecret").Length > 0,
                        sqlTokenUrl = ini.Get("LoginVlink", "SqlTokenUrl"),
                        nfeTokenUrl = ini.Get("LoginVlink", "NfeTokenUrl")
                    });
                }).RequireCors("FacilAppSqlPonteLocal");
                app.MapPut("/api/console/configura-vlink", async (VlinkConfigurationRequest request, HttpContext context, ConsoleStore store, CancellationToken token) =>
                {
                    if (!IsTrustedAdministrator(context, ini)
                        || !context.Request.Cookies.TryGetValue(masterSessionCookie, out string? session)
                        || !await store.IsMasterSessionAsync(session, token))
                    {
                        return Results.StatusCode(403);
                    }

                    if (string.IsNullOrWhiteSpace(request.SqlClientId)
                        || await store.GetActiveCredentialAsync(request.SqlClientId, token) is null)
                    {
                        return Results.BadRequest(new { ok = false, erro = "Selecione uma credencial SQL ativa." });
                    }

                    await ini.UpdateAsync(new[]
                    {
                        ("LoginVlink", "SqlClientId", request.SqlClientId.Trim()),
                        ("LoginVlink", "NfeClientId", request.NfeClientId.Trim()),
                        ("LoginVlink", "NfeClientSecret", request.NfeClientSecret)
                    }, token);
                    return Results.Ok(new { ok = true });
                }).RequireCors("FacilAppSqlPonteLocal");
                app.MapGet("/api/console/configuracoes/arquivos", (Func<HttpContext, ConfigurationFileService, IResult>)((HttpContext context, ConfigurationFileService configurationFiles) => (!IsTrustedAdministrator(context, ini)) ? Results.Json(new
                {
                    ok = false,
                    erro = "configuração permitida somente no servidor autorizado."
                }, (JsonSerializerOptions?)null, (string?)null, (int?)403) : Results.Ok(new
                {
                    ok = true,
                    nomeIni = configurationFiles.IniFileName,
                    ini = configurationFiles.GetIniPreview(),
                    nomeConfigJavaScript = "config.js",
                    configJavaScript = configurationFiles.GetConfigJavaScriptPreview()
                })));
                app.MapGet("/api/console/configuracoes/arquivos/ini", (Func<HttpContext, ConfigurationFileService, IResult>)delegate (HttpContext context, ConfigurationFileService configurationFiles)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Forbid();
                    }
                    context.Response.Headers.CacheControl = "no-store";
                    byte[] bytes = Encoding.UTF8.GetBytes(configurationFiles.GetCompleteIni());
                    return Results.File(bytes, "text/plain; charset=utf-8", configurationFiles.IniFileName);
                });
                app.MapGet("/api/console/configuracoes/arquivos/config-js", (Func<HttpContext, ConfigurationFileService, IResult>)delegate (HttpContext context, ConfigurationFileService configurationFiles)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Forbid();
                    }
                    context.Response.Headers.CacheControl = "no-store";
                    byte[] bytes = Encoding.UTF8.GetBytes(configurationFiles.GetCompleteConfigJavaScript());
                    return Results.File(bytes, "text/javascript; charset=utf-8", "config.js");
                });
                app.MapPost("/api/console/configuracoes/arquivos/aplicar", (Func<ConfigurationFileUpdateRequest, HttpContext, ConfigurationFileService, CancellationToken, Task<IResult>>)async delegate (ConfigurationFileUpdateRequest request, HttpContext context, ConfigurationFileService configurationFiles, CancellationToken cancellationToken)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = "configuração permitida somente no servidor autorizado."
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)403);
                    }
                    if (string.IsNullOrWhiteSpace(request.Ini))
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "Informe o conteúdo do INI."
                        });
                    }
                    await configurationFiles.ApplyRedactedIniAsync(request.Ini, cancellationToken);
                    return Results.Ok(new
                    {
                        ok = true,
                        reinicioNecessario = true,
                        mensagem = "INI gravado na pasta do EXE. Reinicie o serviço para aplicar todas as alterações."
                    });
                });
                app.MapPut("/api/console/configuracoes/geral", (Func<GeneralConfigurationRequest, HttpContext, CancellationToken, Task<IResult>>)async delegate (GeneralConfigurationRequest request, HttpContext context, CancellationToken token)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = "configuração permitida somente no servidor autorizado."
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)403);
                    }
                    int porta = request.Porta;
                    if ((porta < 1 || porta > 65535) ? true : false)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "A porta deve estar entre 1 e 65535."
                        });
                    }
                    porta = request.TimeoutSegundos;
                    if ((porta < 1 || porta > 600) ? true : false)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "O timeout deve estar entre 1 e 600 segundos."
                        });
                    }
                    int portaPonteClaude = request.PortaPonteClaude;
                    if (portaPonteClaude < 1 || portaPonteClaude > 65535)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "A porta da Ponte Claude deve estar entre 1 e 65535."
                        });
                    }
                    if (portaPonteClaude == request.Porta)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "A API SQL e a Ponte Claude devem usar portas diferentes."
                        });
                    }
                    porta = request.LimiteCorpoKb;
                    if ((porta < 1 || porta > 1048576) ? true : false)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "O limite do corpo deve estar entre 1 e 1.048.576 KB."
                        });
                    }
                    porta = request.MaximoConexoes;
                    if ((porta < 1 || porta > 100000) ? true : false)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "O máximo de conexões deve estar entre 1 e 100.000."
                        });
                    }
                    porta = request.RetencaoDias;
                    if ((porta < 1 || porta > 3650) ? true : false)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "A retenção deve estar entre 1 e 3.650 dias."
                        });
                    }
                    porta = request.TamanhoMaximoArquivoMb;
                    if ((porta < 1 || porta > 1024) ? true : false)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "O tamanho máximo do arquivo deve estar entre 1 e 1.024 MB."
                        });
                    }
                    string text = request.DiretorioBaseArquivos?.Trim() ?? string.Empty;
                    if (text.Length == 0 || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "Diretório-base de arquivos inválido."
                        });
                    }
                    string[] array = (from extension in (request.ExtensoesPermitidas ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      select extension.TrimStart('.').ToLowerInvariant()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (array.Length == 0 || array.Any(delegate (string extension)
                    {
                        int length = extension.Length;
                        return ((length < 1 || length > 12) ? true : false) || extension.Any((char character) => !char.IsLetterOrDigit(character));
                    }))
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "Informe extensões válidas separadas por vírgula, sem ponto."
                        });
                    }
                    string text2 = request.NivelLog.Trim().ToUpperInvariant();
                    string[] array2 = new string[6] { "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL" };
                    if (!array2.Contains<string>(text2, StringComparer.Ordinal))
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "Nível de log inválido."
                        });
                    }
                    List<(string, string, string)> list = new List<(string, string, string)>
                    {
                        ("Instancia", "Nome", request.NomeInstancia.Trim()),
                        ("Servico", "NomeWindows", request.NomeServicoWindows.Trim()),
                        ("ServidorHTTP", "Ativo", request.ServidorAtivo ? "1" : "0"),
                        ("ServidorHTTP", "Porta", request.Porta.ToString()),
                        ("ServidorHTTP", "Portas", request.Porta.ToString()),
                        ("ServidorHTTP", "TimeoutSegundos", request.TimeoutSegundos.ToString()),
                        ("ServidorHTTP", "LimiteCorpoKB", request.LimiteCorpoKb.ToString()),
                        ("ServidorHTTP", "MaxConexoes", request.MaximoConexoes.ToString()),
                        ("PonteClaude", "Ativo", request.PonteClaudeAtiva ? "1" : "0"),
                        ("PonteClaude", "Porta", request.PortaPonteClaude.ToString()),
                        ("Seguranca", "ExigirToken", request.ExigirToken ? "1" : "0"),
                        ("Seguranca", "ConsultaLivre", request.ConsultaLivre ? "1" : "0"),
                        ("Rede", "PermitirLocalhost", request.PermitirLocalhost ? "1" : "0"),
                        ("Rede", "PermitirRedeLocal", request.PermitirRedeLocal ? "1" : "0"),
                        ("Rede", "PermitirTailscale", request.PermitirTailscale ? "1" : "0"),
                        ("Log", "Ativo", request.LogAtivo ? "1" : "0"),
                        ("Log", "Nivel", text2),
                        ("Log", "RetencaoDias", request.RetencaoDias.ToString()),
                        ("Arquivos", "Ativo", request.ArquivosAtivos ? "1" : "0"),
                        ("Arquivos", "TamanhoMaximoMB", request.TamanhoMaximoArquivoMb.ToString()),
                        ("Arquivos", "DiretorioBase", text),
                        ("Arquivos", "ExtensoesPermitidas", string.Join(',', array))
                    };
                    if (!string.IsNullOrWhiteSpace(request.TokenPonteClaude))
                    {
                        list.Add(("PonteClaude", "Token", request.TokenPonteClaude.Trim()));
                    }
                    await ini.UpdateAsync(list, token);
                    return Results.Ok(new
                    {
                        ok = true,
                        reinicioNecessario = true,
                        mensagem = "Configuração gravada. Reinicie o serviço para aplicar porta, limites e nome do serviço."
                    });
                });
                app.MapPut("/api/console/configuracoes/bancos/{tipo}", (Func<string, DatabaseConfigurationRequest, HttpContext, CancellationToken, Task<IResult>>)async delegate (string tipo, DatabaseConfigurationRequest request, HttpContext context, CancellationToken token)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = "configuração permitida somente no servidor autorizado."
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)403);
                    }
                    (string, string) tuple = DatabaseService.NormalizeDatabaseType(tipo);
                    string normalizedType = tuple.Item1;
                    string item = tuple.Item2;
                    int porta = request.Porta;
                    if ((porta < 0 || porta > 65535) ? true : false)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = "porta inválida."
                        });
                    }
                    List<(string, string, string)> list = new List<(string, string, string)>
                    {
                        (item, "Ativo", request.Ativo ? "1" : "0"),
                        (item, "Servidor", request.Servidor.Trim()),
                        (item, "Porta", request.Porta.ToString()),
                        (item, "Banco", request.Banco.Trim()),
                        (item, "Usuario", request.Usuario.Trim())
                    };
                    if (normalizedType == "sqlite")
                    {
                        string sqliteDirectory = request.Diretorio?.Trim() ?? string.Empty;
                        if (sqliteDirectory.Length == 0 || sqliteDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                        {
                            return Results.BadRequest(new
                            {
                                ok = false,
                                erro = "diretório-base do SQLite inválido."
                            });
                        }
                        list.Add((item, "Diretorio", sqliteDirectory));
                    }
                    if (!string.IsNullOrWhiteSpace(request.Dsn))
                    {
                        list.Add((item, "DSN", request.Dsn.Trim()));
                    }
                    if (!string.IsNullOrWhiteSpace(request.Senha))
                    {
                        list.Add((item, "Senha", request.Senha));
                    }
                    await ini.UpdateAsync(list, token);
                    return Results.Ok(new
                    {
                        ok = true,
                        tipo = normalizedType
                    });
                });
                app.MapPost("/api/console/configuracoes/bancos/{tipo}/testar", (Func<string, HttpContext, DatabaseService, CancellationToken, Task<IResult>>)async delegate (string tipo, HttpContext context, DatabaseService databases, CancellationToken token)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = "teste permitido somente no servidor autorizado."
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)403);
                    }
                    try
                    {
                        await databases.TestConnectionAsync(tipo, token);
                        return Results.Ok(new
                        {
                            ok = true,
                            mensagem = "Conexão realizada com sucesso."
                        });
                    }
                    catch (Exception ex) when (((ex is DbException || ex is InvalidOperationException || ex is NotSupportedException) ? 1 : 0) != 0)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            erro = ex.Message
                        });
                    }
                });
                app.MapPut("/api/console/configuracoes/integracoes", (Func<IntegrationConfigurationRequest, HttpContext, CancellationToken, Task<IResult>>)async delegate (IntegrationConfigurationRequest request, HttpContext context, CancellationToken token)
                {
                    if (!IsTrustedAdministrator(context, ini))
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            erro = "configuração permitida somente no servidor autorizado."
                        }, (JsonSerializerOptions?)null, (string?)null, (int?)403);
                    }
                    string item = new string(request.NumeroFacilApp.Where(char.IsDigit).ToArray());
                    List<(string, string, string)> list = new List<(string, string, string)>
                    {
                        ("OPENSTREETMAP", "USER_AGENT", request.MapaUserAgent.Trim()),
                        ("WHATSAPP_ZAPI", "Ativo", request.ZApiAtiva ? "1" : "0"),
                        ("WHATSAPP_ZAPI", "BASE_URL", request.ZApiBaseUrl.Trim()),
                        ("WHATSAPP_ZAPI", "INSTANCE_ID", request.ZApiInstanceId.Trim()),
                        ("ALERTAS", "Ativo", request.AlertasAtivos ? "1" : "0"),
                        ("ALERTAS", "NumeroFacilApp", item),
                        ("ALERTAS", "IntervaloMinutos", "5")
                    };
                    if (!string.IsNullOrWhiteSpace(request.ZApiInstanceToken))
                    {
                        list.Add(("WHATSAPP_ZAPI", "INSTANCE_TOKEN", request.ZApiInstanceToken));
                    }
                    if (!string.IsNullOrWhiteSpace(request.ZApiClientToken))
                    {
                        list.Add(("WHATSAPP_ZAPI", "CLIENT_TOKEN", request.ZApiClientToken));
                    }
                    await ini.UpdateAsync(list, token);
                    return Results.Ok(new
                    {
                        ok = true
                    });
                });
                app.Run();
            }
        }
        catch (Exception exception)
        {
            WriteStartupFailure(exception);
            Environment.ExitCode = 1;
        }
        /// <summary>
        /// Mantém a API acessível quando o firewall ou a VPN controlam a origem.
        /// A autenticação da aplicação continua sendo feita pelo login e Bearer.
        /// </summary>
        static bool IsTrustedAdministrator(HttpContext context, IniConfiguration configuration)
        {
            return true;
        }        static string OnlyDigits(string value)
        {
            return new string(value.Where(char.IsDigit).ToArray());
        }

        static LogLevel ParseLogLevel(string value)
        {
            return Enum.TryParse(value?.Trim(), ignoreCase: true, out LogLevel level)
                ? level
                : LogLevel.Information;
        }

        static string AuditUser(HttpContext context)
        {
            return (context.Items[nameof(AccessTokenValidation)] as AccessTokenValidation)?.ClientId
                ?? "não identificado";
        }

        static string AuditSessionId(HttpContext context)
        {
            string authorization = context.Request.Headers.Authorization.ToString();
            string token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization.Substring(7).Trim()
                : string.Empty;
            if (token.Length == 0)
            {
                return "sem sessão";
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(hash.AsSpan(0, 8));
        }

        static async Task<string> ReadAuditRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            try
            {
                request.Body.Position = 0;
                using StreamReader reader = new(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                string body = await reader.ReadToEndAsync(cancellationToken);
                request.Body.Position = 0;
                if (string.IsNullOrWhiteSpace(body) || request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
                {
                    return string.IsNullOrWhiteSpace(body) ? string.Empty : "[corpo não JSON]";
                }

                using JsonDocument document = JsonDocument.Parse(body);
                using MemoryStream buffer = new();
                using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false }))
                {
                    WriteSanitizedAuditJson(document.RootElement, writer, propertyName: null);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch
            {
                try { request.Body.Position = 0; } catch { }
                return "[corpo indisponível]";
            }
        }

        static void WriteSanitizedAuditJson(JsonElement element, Utf8JsonWriter writer, string? propertyName)
        {
            if (propertyName is not null
                && (propertyName.Contains("senha", StringComparison.OrdinalIgnoreCase)
                    || propertyName.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || propertyName.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    || propertyName.Contains("token", StringComparison.OrdinalIgnoreCase)))
            {
                writer.WriteStringValue("***");
                return;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteSanitizedAuditJson(property.Value, writer, property.Name);
                }
                writer.WriteEndObject();
                return;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteSanitizedAuditJson(item, writer, propertyName: null);
                }
                writer.WriteEndArray();
                return;
            }

            element.WriteTo(writer);
        }

        static async Task<bool> IsPublicLoginRequestAsync(
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            if (!HttpMethods.IsPost(request.Method)
                || request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return false;
            }

            request.EnableBuffering();
            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                JsonElement root = document.RootElement;
                string function = ReadJsonString(root, "funcao");
                if (function.Length == 0)
                {
                    function = ReadJsonString(root, "acao");
                }

                return function.Equals("login", StringComparison.OrdinalIgnoreCase)
                    || function.Equals("loginvlink", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
            finally
            {
                request.Body.Position = 0;
            }
        }

        static string ReadJsonString(JsonElement root, string name)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString()?.Trim() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        static Task WriteLocalLoginAuditAsync(
            AuditFileService audit,
            HttpContext context,
            LocalUserAuthentication authentication,
            CancellationToken cancellationToken,
            string eventName = "login_usuario")
        {
            LocalUserView user = authentication.Usuario;
            return audit.WriteAsync(new
            {
                data_hora = DateTimeOffset.Now,
                evento = eventName,
                usuario = user.Usuario,
                nome = user.Nome,
                usuario_id = user.Id,
                empresa_id = user.EmpresaId,
                cnpj = new string((user.Cnpj ?? string.Empty).Where(char.IsDigit).ToArray()),
                sistema = user.SistemaId,
                origem = context.Connection.RemoteIpAddress?.ToString(),
                user_agent = context.Request.Headers.UserAgent.ToString()
            }, cancellationToken);
        }

        static IResult CreateLoginTokenResponse(
            HttpContext context,
            AccessTokenService tokens,
            ApiCredentialView credential,
            string requestedScope)
        {
            AccessTokenResult loginToken = tokens.Create(credential, requestedScope);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            if (string.IsNullOrWhiteSpace(requestedScope))
            {
                return Results.Ok(new
                {
                    access_token = loginToken.AccessToken,
                    expires_in = loginToken.ExpiresIn,
                    token_type = "Bearer"
                });
            }

            return Results.Ok(new
            {
                access_token = loginToken.AccessToken,
                expires_in = loginToken.ExpiresIn,
                token_type = "Bearer",
                scope = loginToken.Scope
            });
        }

        static IResult CreateLocalUserLoginResponse(
            HttpContext context,
            AccessTokenService tokens,
            LocalUserAuthentication authentication,
            bool includePermissions = true)
        {
            AccessTokenResult loginToken = tokens.Create(authentication.Credencial);
            LocalUserView user = authentication.Usuario;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            if (!includePermissions)
            {
                return Results.Ok(new
                {
                    access_token = loginToken.AccessToken,
                    expires_in = loginToken.ExpiresIn,
                    token_type = "Bearer",
                    cnpj = new string((user.Cnpj ?? string.Empty).Where(char.IsDigit).ToArray()),
                    usuario = new
                    {
                        idUsuario = user.Id,
                        idFilial = user.EmpresaId,
                        cnpj = new string((user.Cnpj ?? string.Empty).Where(char.IsDigit).ToArray()),
                        login = user.Usuario,
                        nome = user.Nome
                    }
                });
            }
            return Results.Ok(new
            {
                access_token = loginToken.AccessToken,
                expires_in = loginToken.ExpiresIn,
                token_type = "Bearer",
                cnpj = new string((user.Cnpj ?? string.Empty).Where(char.IsDigit).ToArray()),
                usuario = new
                {
                    idUsuario = user.Id,
                    idFilial = user.EmpresaId,
                    cnpj = new string((user.Cnpj ?? string.Empty).Where(char.IsDigit).ToArray()),
                    login = user.Usuario,
                    nome = user.Nome
                },
                permissoes = new
                {
                    lateral = ReadMenuData(user.MenuData),
                    superior = ReadMenuData(user.MenuSuperiorData),
                    dashboards = ReadMenuData(user.DashBoardData)
                }
            });
        }

        static JsonElement ReadMenuData(string source)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(source);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // Migrações antigas podem conter dados vazios; o login não deve falhar por isso.
            }

            using JsonDocument emptyDocument = JsonDocument.Parse("[]");
            return emptyDocument.RootElement.Clone();
        }

        static bool SecureEquals(string left, string right)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(left);
            byte[] bytes2 = Encoding.UTF8.GetBytes(right);
            if (bytes.Length == bytes2.Length)
            {
                return CryptographicOperations.FixedTimeEquals(bytes, bytes2);
            }
            return false;
        }
        static void WriteStartupFailure(Exception ex)
        {
            try
            {
                string text = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "FacilApp",
                    "API_FACILAPP_SQL",
                    "Logs");
                Directory.CreateDirectory(text);
                string path = Path.Combine(text, "FacilAppSQL-startup.log");
                string contents = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} | {ex}{Environment.NewLine}";
                File.AppendAllText(path, contents);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Prepara o INI gravável usado pelo serviço sem sobrescrever uma configuração
    /// existente durante atualização ou reinstalação.
    /// </summary>
    private static string PrepareIniPath(bool isPackagedService)
    {
        string bundledIniPath = Path.Combine(AppContext.BaseDirectory, "FacilAppSQL.ini");
        if (!isPackagedService)
        {
            return bundledIniPath;
        }

        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "FacilApp",
            "API_FACILAPP_SQL");
        Directory.CreateDirectory(dataDirectory);

        string writableIniPath = Path.Combine(dataDirectory, "FacilAppSQL.ini");
        if (!File.Exists(writableIniPath))
        {
            // O modelo empacotado contém todos os parâmetros operacionais e nenhum segredo.
            File.Copy(bundledIniPath, writableIniPath, overwrite: false);
        }

        return writableIniPath;
    }

    /// <summary>
    /// Garante que instalações antigas recebam a seção da Ponte Claude. Quando o
    /// token ainda não existe, gera um valor aleatório exclusivo para a máquina e
    /// o persiste no INI, onde o administrador poderá substituí-lo posteriormente.
    /// </summary>
    /// <param name="configuration">Configuração INI gravável da instalação.</param>
    private static async Task EnsureLocalHtmlBridgeConfigurationAsync(IniConfiguration configuration)
    {
        List<(string Section, string Key, string Value)> updates = new();
        if (configuration.Get("PonteClaude", "Ativo").Length == 0)
        {
            updates.Add(("PonteClaude", "Ativo", "1"));
        }
        if (configuration.Get("PonteClaude", "Porta").Length == 0)
        {
            updates.Add(("PonteClaude", "Porta", "3000"));
        }
        if (configuration.Get("PonteClaude", "PermitirRede").Length == 0)
        {
            updates.Add(("PonteClaude", "PermitirRede", "1"));
        }
        if (configuration.Get("PonteClaude", "Token").Length == 0)
        {
            string generatedToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            updates.Add(("PonteClaude", "Token", generatedToken));
        }

        if (updates.Count > 0)
        {
            await configuration.UpdateAsync(updates, CancellationToken.None);
        }
    }

    private static ExecuteRequest ToExecuteRequest(MultibankRequestBase body)
    {
        Dictionary<string, object?> values = new()
        {
            ["tipo_banco"] = body.DatabaseType,
            ["servidor"] = body.Server,
            ["banco"] = body.Database,
            ["tabela"] = body.Table
        };
        switch (body)
        {
            case MultibankQueryRequest query:
                values["campos"] = query.Fields;
                values["where"] = query.Where;
                break;
            case MultibankInsertRequest insert:
                values["dados"] = insert.Data;
                break;
            case MultibankUpdateRequest update:
                values["dados"] = update.Data;
                values["where"] = update.Where;
                break;
            case MultibankDeleteRequest delete:
                values["where"] = delete.Where;
                break;
        }

        return new ExecuteRequest
        {
            Arguments = values.ToDictionary(
                item => item.Key,
                item => JsonSerializer.SerializeToElement(item.Value),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static async Task<IResult> ExecuteMultibankEndpointAsync(
        ExecuteRequest request,
        string function,
        bool trustedAdministrator,
        LegacyDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        request.Function = function;
        try
        {
            return Results.Ok(await dispatcher.ExecuteAsync(request, trustedAdministrator, cancellationToken));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Results.Json(new { ok = false, erro = exception.Message }, statusCode: 403);
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { ok = false, erro = exception.Message });
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or NotSupportedException or FileNotFoundException)
        {
            return Results.BadRequest(new { ok = false, erro = exception.Message });
        }
    }
}

