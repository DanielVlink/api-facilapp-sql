using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.RegularExpressions.Generated;
using System.Threading;
using System.Threading.Tasks;
using FacilApp.Sql.Api.Configuration;

namespace FacilApp.Sql.Api.Services;

public sealed partial class RepositoryService
{
    private readonly IniConfiguration configuration;

    public RepositoryService(IniConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public async Task<object> SaveAsync(string category, string developer, string user, string fileName, string base64, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        string path = Resolve(category, developer, user, fileName);
        byte[] content;
        try
        {
            content = Convert.FromBase64String(base64);
        }
        catch (FormatException innerException)
        {
            throw new InvalidDataException("conteudo_base64 inválido.", innerException);
        }
        int num = configuration.GetInt("Arquivos", "TamanhoMaximoMB", 15) * 1024 * 1024;
        if (content.Length > num)
        {
            throw new InvalidDataException("Arquivo excede o tamanho máximo.");
        }
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidDataException("diretório de destino inválido.");
        }
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(path, content, cancellationToken);
        return new
        {
            nome_arquivo = Path.GetFileName(path),
            tamanho = content.Length
        };
    }

    public async Task<object> ReadAsync(string category, string developer, string user, string fileName, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        string path = Resolve(category, developer, user, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("arquivo não encontrado.");
        }
        byte[] inArray = await File.ReadAllBytesAsync(path, cancellationToken);
        return new
        {
            nome_arquivo = Path.GetFileName(path),
            conteudo_base64 = Convert.ToBase64String(inArray)
        };
    }

    public object List(string category, string developer, string user)
    {
        EnsureEnabled();
        string path = ResolveDirectory(category, developer, user);
        if (!Directory.Exists(path))
        {
            return Array.Empty<object>();
        }
        return (from text in Directory.EnumerateFiles(path)
                select new
                {
                    nome_arquivo = Path.GetFileName(text),
                    tamanho = new FileInfo(text).Length
                }).ToArray();
    }

    public object Delete(string category, string developer, string user, string fileName)
    {
        EnsureEnabled();
        string path = Resolve(category, developer, user, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("arquivo não encontrado.");
        }
        File.Delete(path);
        return new
        {
            excluido = true,
            nome_arquivo = Path.GetFileName(path)
        };
    }

    private string Resolve(string category, string developer, string user, string fileName)
    {
        string requestedPath = (fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(requestedPath) || requestedPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new InvalidDataException("caminho de arquivo inválido.");
        }

        string value = Path.GetExtension(requestedPath).TrimStart('.').ToLowerInvariant();
        string[] array = configuration.Get("Arquivos", "ExtensoesPermitidas").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!array.Contains<string>(value, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("extensão não permitida.");
        }

        // Caminho absoluto informado pelo aplicativo: grava exatamente no destino solicitado.
        // Caminhos relativos mantêm o comportamento anterior no DiretórioBase configurado.
        if (Path.IsPathRooted(requestedPath))
        {
            string absolutePath = Path.GetFullPath(requestedPath);
            if (string.IsNullOrWhiteSpace(Path.GetFileName(absolutePath)))
            {
                throw new InvalidDataException("nome de arquivo inválido.");
            }
            return absolutePath;
        }

        string text = Safe(requestedPath);
        string text2 = ResolveDirectory(category, developer, user);
        string fullPath = Path.GetFullPath(Path.Combine(text2, text));
        if (!fullPath.StartsWith(text2 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("caminho inválido.");
        }
        return fullPath;
    }

    private string ResolveDirectory(string category, string developer, string user)
    {
        string text = configuration.Get("Arquivos", "DiretorioBase", "Repositorio");
        string text2 = (Path.IsPathRooted(text) ? text : Path.Combine(configuration.DataDirectory, text));
        string fullPath = Path.GetFullPath(Path.Combine(text2, Safe(category), Safe(developer), Safe(user)));
        string fullPath2 = Path.GetFullPath(text2);
        if (!fullPath.StartsWith(fullPath2 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("caminho inválido.");
        }
        return fullPath;
    }

    private void EnsureEnabled()
    {
        if (!configuration.GetBoolean("Arquivos", "Ativo", fallback: true))
        {
            throw new InvalidOperationException("O envio e a consulta de anexos estão desativados.");
        }
    }

    private static string Safe(string value)
    {
        string fileName = Path.GetFileName(value.Trim());
        if (string.IsNullOrWhiteSpace(fileName) || !SafeNameRegex().IsMatch(fileName))
        {
            throw new InvalidDataException("nome de arquivo ou diretório inválido.");
        }
        return fileName;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeNameRegex();
}
