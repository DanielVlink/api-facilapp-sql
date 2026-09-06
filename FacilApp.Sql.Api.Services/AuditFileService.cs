using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Trilhas de auditoria persistentes para alterações administrativas.
/// Cada linha é um JSON independente, adequado para consulta posterior.
/// </summary>
public sealed class AuditFileService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "Logs");

    public async Task WriteAsync(object entry, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, $"AUDITORIA_{DateTime.Now:yyyyMMdd}.jsonl");
            string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            await Gate.WaitAsync(cancellationToken);
            try
            {
                await File.AppendAllTextAsync(path, line, new UTF8Encoding(false), cancellationToken);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch
        {
            // Auditoria não pode interromper a operação da API.
        }
    }

    /// <summary>
    /// Mantém uma cópia recuperável dos três catálogos de acesso antes/depois
    /// da mudança de menus de um usuário.
    /// </summary>
    public async Task WriteMenuBackupAsync(
        string companyId,
        string userId,
        string actor,
        string session,
        object previous,
        object current,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            string safeCompany = SafeFilePart(companyId);
            string safeUser = SafeFilePart(userId);
            string path = Path.Combine(
                _directory,
                $"MenuData_BKP_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{safeCompany}_{safeUser}.json");
            string content = JsonSerializer.Serialize(new
            {
                data_hora = DateTimeOffset.Now,
                empresa_id = companyId,
                usuario_id = userId,
                executado_por = actor,
                sessao = session,
                anterior = previous,
                atual = current
            }, new JsonSerializerOptions { WriteIndented = true });

            await Gate.WaitAsync(cancellationToken);
            try
            {
                string temporary = path + ".tmp";
                await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch
        {
            // Auditoria não pode interromper a operação da API.
        }
    }

    private static string SafeFilePart(string value)
    {
        string result = string.Concat((value ?? string.Empty)
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        return string.IsNullOrWhiteSpace(result) ? "sem_id" : result;
    }
}
