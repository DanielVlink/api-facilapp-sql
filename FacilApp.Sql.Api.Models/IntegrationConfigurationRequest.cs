namespace FacilApp.Sql.Api.Models;

public sealed class IntegrationConfigurationRequest
{
    public string MapaUserAgent { get; set; } = string.Empty;

    public bool ZApiAtiva { get; set; }

    public string ZApiBaseUrl { get; set; } = string.Empty;

    public string ZApiInstanceId { get; set; } = string.Empty;

    public string ZApiInstanceToken { get; set; } = string.Empty;

    public string ZApiClientToken { get; set; } = string.Empty;

    public bool AlertasAtivos { get; set; }

    public string NumeroFacilApp { get; set; } = string.Empty;
}
