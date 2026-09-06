using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;

namespace FacilApp.Sql.Api.Services;

public sealed class PowerShellScriptService
{
    private readonly IniConfiguration configuration;

    public PowerShellScriptService(IniConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public async Task<object> ExecuteAsync(string requestedFile, CancellationToken cancellationToken)
    {
        configuration.Reload();
        if (!configuration.GetBoolean("Scripts", "Ativo", true))
            throw new InvalidOperationException("Execução de scripts está desativada no INI.");
        if (string.IsNullOrWhiteSpace(requestedFile))
            throw new InvalidDataException("Informe o arquivo PS1.");

        string configuredDirectories = configuration.Get(
            "Scripts", "DiretoriosPermitidos",
            configuration.Get("Scripts", "Diretorio", @"C:\FacilApp\API_FACILAPP_SQL\Scripts"));
        string[] allowedDirectories = configuredDirectories
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToArray();
        if (allowedDirectories.Length == 0)
            throw new InvalidDataException("Configure [Scripts] DiretoriosPermitidos.");
        string baseDirectory = allowedDirectories[0];
        if (!Directory.Exists(baseDirectory)) Directory.CreateDirectory(baseDirectory);
        string fullPath = Path.GetFullPath(Path.IsPathRooted(requestedFile)
            ? requestedFile.Trim()
            : Path.Combine(baseDirectory, requestedFile.Trim()));
        bool allowed = allowedDirectories.Any(directory =>
            fullPath.StartsWith(
                directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
        if (!allowed || !Path.GetExtension(fullPath).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("O PS1 deve estar em uma pasta local ou UNC configurada em [Scripts] DiretoriosPermitidos.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Script PS1 não encontrado.", fullPath);

        int timeoutSeconds = Math.Clamp(configuration.GetInt("Scripts", "TimeoutSegundos", 300), 1, 3600);
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? baseDirectory
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(fullPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Não foi possível iniciar o PowerShell.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await WriteLogAsync(Path.GetFileName(fullPath), null, "timeout", CancellationToken.None);
            throw new TimeoutException($"O script excedeu {timeoutSeconds} segundos.");
        }

        string output = await outputTask;
        string error = await errorTask;
        await WriteLogAsync(Path.GetFileName(fullPath), process.ExitCode, process.ExitCode == 0 ? "ok" : "erro", cancellationToken);
        return new
        {
            ok = process.ExitCode == 0,
            arquivo = fullPath,
            codigo_saida = process.ExitCode,
            saida = output,
            erro = error
        };
    }

    private async Task WriteLogAsync(string fileName, int? exitCode, string result, CancellationToken token)
    {
        string logDirectory = Path.Combine(configuration.DataDirectory, "Logs");
        Directory.CreateDirectory(logDirectory);
        string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] SCRIPT={fileName} RESULTADO={result} CODIGO={exitCode?.ToString() ?? "-"}{Environment.NewLine}";
        await File.AppendAllTextAsync(Path.Combine(logDirectory, "API_FACILAPP_SQL.LOG"), line, new UTF8Encoding(false), token);
    }
}
