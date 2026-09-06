using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;
using Microsoft.Extensions.Logging;

namespace FacilApp.Sql.Api.Services;

public sealed class FailureNotificationService
{
    private readonly IntegrationService integrationService;

    private readonly IniConfiguration configuration;

    private readonly ILogger<FailureNotificationService> logger;

    private readonly SemaphoreSlim synchronization = new SemaphoreSlim(1, 1);

    private DateTimeOffset lastNotification = DateTimeOffset.MinValue;

    public FailureNotificationService(IntegrationService integrationService, IniConfiguration configuration, ILogger<FailureNotificationService> logger)
    {
        this.integrationService = integrationService;
        this.configuration = configuration;
        this.logger = logger;
    }

    public async Task NotifyAsync(string route, Exception exception, CancellationToken cancellationToken)
    {
        if (!configuration.GetBoolean("ALERTAS", "Ativo"))
        {
            return;
        }
        string destination = new string(configuration.Get("ALERTAS", "NumeroFacilApp").Where(char.IsDigit).ToArray());
        bool flag = destination.Length < 10;
        bool flag2 = flag;
        if (!flag2)
        {
            flag2 = !(await synchronization.WaitAsync(0, cancellationToken));
        }
        if (flag2)
        {
            return;
        }
        try
        {
            int num = configuration.GetInt("ALERTAS", "IntervaloMinutos", 5);
            if (!(DateTimeOffset.UtcNow - lastNotification < TimeSpan.FromMinutes(num)))
            {
                lastNotification = DateTimeOffset.UtcNow;
                string message = $"[FACILAPP SQL] Falha na API em {DateTimeOffset.Now:dd/MM/yyyy HH:mm:ss}. Rota: {route}. Tipo: {exception.GetType().Name}. Verifique os logs do servidor.";
                await integrationService.SendZApiTextAsync(destination, message, cancellationToken);
            }
        }
        catch (Exception exception2)
        {
            logger.LogError(exception2, "Falha ao enviar alerta operacional pela Z-API.");
        }
        finally
        {
            synchronization.Release();
        }
    }
}
