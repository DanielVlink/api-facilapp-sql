using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Publica arquivos locais pela porta exclusiva da Ponte Claude.
/// A ponte apenas transporta os arquivos originais para HTTP; ela não executa
/// consultas SQL nem altera o conteúdo servido.
/// </summary>
public sealed class LocalHtmlBridgeService
{
    private const string AuthenticationCookieName = "ClaudeBridgeAuth";
    private const string ConfigurationSection = "PonteClaude";
    private readonly IniConfiguration configuration;
    private readonly FileExtensionContentTypeProvider contentTypeProvider = new();
    private readonly ILogger<LocalHtmlBridgeService> logger;
    private readonly SemaphoreSlim logSynchronization = new(1, 1);

    /// <summary>
    /// Inicializa o serviço HTTP local da Ponte Claude.
    /// </summary>
    /// <param name="configuration">Configuração INI ativa da API SQL.</param>
    /// <param name="logger">Logger estruturado da aplicação.</param>
    public LocalHtmlBridgeService(
        IniConfiguration configuration,
        ILogger<LocalHtmlBridgeService> logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    /// <summary>
    /// Processa uma requisição recebida pela porta da Bridge.
    /// </summary>
    /// <param name="context">Contexto HTTP atual.</param>
    /// <param name="cancellationToken">Cancelamento da requisição.</param>
    /// <returns>Uma tarefa concluída depois que a resposta for enviada.</returns>
    public async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        int statusCode = StatusCodes.Status500InternalServerError;
        bool authorized = false;

        try
        {
            // A releitura permite trocar o token no INI sem reiniciar a API.
            configuration.Reload();
            string configuredToken = configuration.Get(ConfigurationSection, "Token").Trim();
            if (configuredToken.Length == 0)
            {
                statusCode = StatusCodes.Status503ServiceUnavailable;
                await WriteJsonAsync(
                    context,
                    statusCode,
                    "{\"status\":\"erro\",\"mensagem\":\"Token da Bridge nao configurado\"}",
                    cancellationToken);
                return;
            }

            string queryToken = context.Request.Query["token"].ToString();
            string cookieToken = context.Request.Cookies.TryGetValue(AuthenticationCookieName, out string? cookieValue)
                ? cookieValue
                : string.Empty;
            authorized = SecureEquals(queryToken, configuredToken)
                || SecureEquals(cookieToken, configuredToken);

            if (!authorized)
            {
                statusCode = StatusCodes.Status401Unauthorized;
                await WriteJsonAsync(
                    context,
                    statusCode,
                    "{\"status\":\"erro\",\"mensagem\":\"Token invalido ou ausente\"}",
                    cancellationToken);
                return;
            }

            if (SecureEquals(queryToken, configuredToken))
            {
                context.Response.Cookies.Append(AuthenticationCookieName, configuredToken, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = false,
                    Path = "/",
                    MaxAge = TimeSpan.FromDays(30)
                });
            }

            string path = context.Request.Path.Value ?? "/";
            if (path.Equals("/API/status", StringComparison.OrdinalIgnoreCase))
            {
                statusCode = StatusCodes.Status200OK;
                await WriteJsonAsync(
                    context,
                    statusCode,
                    "{\"status\":\"ok\",\"servidor\":\"Claude Bridge C#\"}",
                    cancellationToken);
                return;
            }

            if (path.StartsWith("/HTML/", StringComparison.OrdinalIgnoreCase))
            {
                await ServeLocalFileAsync(context, cancellationToken);
                statusCode = context.Response.StatusCode;
                return;
            }

            if (path.Equals("/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/lancador.html", StringComparison.OrdinalIgnoreCase))
            {
                await ServeLauncherAsync(context, cancellationToken);
                statusCode = context.Response.StatusCode;
                return;
            }

            statusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Rota da Bridge nao encontrada.", cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falha ao processar a Ponte Claude na rota {Route}.", context.Request.Path);
            if (!context.Response.HasStarted)
            {
                statusCode = StatusCodes.Status500InternalServerError;
                await WriteJsonAsync(
                    context,
                    statusCode,
                    "{\"status\":\"erro\",\"mensagem\":\"Falha interna da Bridge\"}",
                    cancellationToken);
            }
        }
        finally
        {
            await WriteAccessLogAsync(context, authorized, statusCode, cancellationToken);
        }
    }

    /// <summary>
    /// Serve o lançador empacotado sem publicá-lo pela porta principal da API.
    /// O HTML contém a função <c>converteUtf8</c> para caminhos com acentos.
    /// </summary>
    private static async Task ServeLauncherAsync(HttpContext context, CancellationToken cancellationToken)
    {
        string launcherPath = Path.Combine(AppContext.BaseDirectory, "Bridge", "lancador.html");
        if (!File.Exists(launcherPath))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Lancador da Bridge nao encontrado.", cancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        context.Response.ContentLength = new FileInfo(launcherPath).Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.SendFileAsync(launcherPath, cancellationToken);
        }
    }

    /// <summary>
    /// Localiza o caminho absoluto codificado depois de /HTML/ e transmite o
    /// arquivo sem convertê-lo em texto, preservando imagens, fontes e binários.
    /// </summary>
    private async Task ServeLocalFileAsync(HttpContext context, CancellationToken cancellationToken)
    {
        string rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget
            ?? context.Request.Path.Value
            ?? string.Empty;
        int queryStart = rawTarget.IndexOf('?');
        string rawPath = queryStart >= 0 ? rawTarget[..queryStart] : rawTarget;
        const string routePrefix = "/HTML/";
        string encodedPath = rawPath.Length >= routePrefix.Length
            ? rawPath[routePrefix.Length..]
            : string.Empty;

        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(encodedPath);
        }
        catch (UriFormatException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Caminho codificado invalido.", cancellationToken);
            return;
        }

        string normalizedPath = NormalizeWindowsPath(decodedPath);
        if (normalizedPath.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(normalizedPath))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Informe um caminho absoluto valido.", cancellationToken);
            return;
        }

        string absolutePath;
        try
        {
            absolutePath = Path.GetFullPath(normalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Caminho local invalido.", cancellationToken);
            return;
        }

        // URLs antigas codificavam todas as barras invertidas como %5C. Nesse
        // formato o navegador enxerga o caminho inteiro como um único segmento
        // e resolve CSS, JS, imagens e páginas relativas fora da pasta correta.
        // Redireciona uma vez para a forma canônica, com cada pasta como um
        // segmento real da URL, sem alterar o arquivo solicitado nem a query.
        if (encodedPath.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || encodedPath.Contains('\\'))
        {
            string canonicalPath = string.Join(
                '/',
                absolutePath
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.EscapeDataString));
            context.Response.Redirect(
                routePrefix + canonicalPath + context.Request.QueryString,
                permanent: false);
            return;
        }

        if (!File.Exists(absolutePath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Arquivo nao encontrado.", cancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentTypeProvider.TryGetContentType(absolutePath, out string? contentType)
            ? contentType
            : "application/octet-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        context.Response.ContentLength = new FileInfo(absolutePath).Length;

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.SendFileAsync(absolutePath, cancellationToken);
        }
    }

    /// <summary>
    /// Converte tanto barras de URL (<c>/</c>) quanto barras de caminho Windows
    /// (<c>\</c>) para o separador nativo. Isso permite usar, por exemplo,
    /// <c>C:/Curso/index.html</c> e <c>C:\Curso\index.html</c> na mesma rota.
    /// </summary>
    /// <param name="decodedPath">Caminho já decodificado da URL.</param>
    /// <returns>Caminho normalizado para o sistema operacional Windows.</returns>
    private static string NormalizeWindowsPath(string decodedPath)
    {
        return decodedPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
    }

    /// <summary>Grava uma resposta JSON UTF-8 sem incluir informações secretas.</summary>
    private static async Task WriteJsonAsync(
        HttpContext context,
        int statusCode,
        string json,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(json, Encoding.UTF8, cancellationToken);
    }

    /// <summary>Compara tokens em tempo constante quando possuem o mesmo tamanho.</summary>
    private static bool SecureEquals(string candidate, string configuredToken)
    {
        if (candidate.Length == 0 || configuredToken.Length == 0)
        {
            return false;
        }

        byte[] candidateBytes = Encoding.UTF8.GetBytes(candidate);
        byte[] configuredBytes = Encoding.UTF8.GetBytes(configuredToken);
        return candidateBytes.Length == configuredBytes.Length
            && CryptographicOperations.FixedTimeEquals(candidateBytes, configuredBytes);
    }

    /// <summary>Registra acessos sem gravar query string, cookie ou token.</summary>
    private async Task WriteAccessLogAsync(
        HttpContext context,
        bool authorized,
        int statusCode,
        CancellationToken cancellationToken)
    {
        string logDirectory = Path.Combine(configuration.DataDirectory, "Logs");
        string logPath = Path.Combine(logDirectory, "ClaudeBridge-acesso.log");
        string remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
        string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] "
            + $"IP={remoteAddress} METODO={context.Request.Method} "
            + $"CAMINHO={context.Request.Path} AUTORIZADO={authorized} STATUS={statusCode}";

        bool synchronizationAcquired = false;
        try
        {
            await logSynchronization.WaitAsync(cancellationToken);
            synchronizationAcquired = true;
            Directory.CreateDirectory(logDirectory);
            await File.AppendAllTextAsync(
                logPath,
                line + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Não foi possível gravar o log de acesso da Ponte Claude.");
        }
        finally
        {
            if (synchronizationAcquired)
            {
                logSynchronization.Release();
            }
        }
    }
}
