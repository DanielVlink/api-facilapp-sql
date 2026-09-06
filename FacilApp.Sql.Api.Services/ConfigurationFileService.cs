using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;

namespace FacilApp.Sql.Api.Services;

public sealed class ConfigurationFileService
{
    private readonly IniConfiguration configuration;

    public string IniFileName => Path.GetFileName(configuration.Path);

    public ConfigurationFileService(IniConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public string GetIniPreview()
    {
        return configuration.ReadRedacted();
    }

    public string GetCompleteIni()
    {
        return configuration.ReadComplete();
    }

    public string GetConfigJavaScriptPreview()
    {
        return BuildConfigJavaScript("********");
    }

    public string GetCompleteConfigJavaScript()
    {
        return BuildConfigJavaScript(configuration.Get("Seguranca", "Token"));
    }

    public Task ApplyRedactedIniAsync(string content, CancellationToken cancellationToken)
    {
        return configuration.ReplaceRedactedAsync(content, cancellationToken);
    }

    private string BuildConfigJavaScript(string token)
    {
        int value = configuration.GetInt("ServidorHTTP", "Porta", 5050);
        int value2 = configuration.GetInt("ServidorHTTP", "TimeoutSegundos", 15) * 1000;
        string value3 = $"http://127.0.0.1:{value}";
        return $"window.SERVIDOR_API_CONFIG = Object.freeze({{\n    baseURL: {JsonSerializer.Serialize(value3)},\n    token: {JsonSerializer.Serialize(token)},\n    timeoutMs: {value2}\n}});";
    }
}
