using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

public sealed class PowerShellScriptRequest
{
    [JsonPropertyName("arquivo")]
    public string Arquivo { get; set; } = string.Empty;
}
