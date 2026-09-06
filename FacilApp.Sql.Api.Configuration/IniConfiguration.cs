using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FacilApp.Sql.Api.Configuration;

public sealed class IniConfiguration
{
    private readonly SemaphoreSlim synchronization = new SemaphoreSlim(1, 1);

    private volatile IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> values = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Obtém o caminho absoluto do arquivo INI ativo.</summary>
    public string Path { get; }

    /// <summary>
    /// Obtém o diretório gravável da instalação, usado por bancos internos,
    /// segredos, logs e caminhos relativos configurados no INI.
    /// </summary>
    public string DataDirectory { get; }

    /// <summary>Carrega a configuração de um arquivo INI existente.</summary>
    /// <param name="path">Caminho do arquivo INI ativo.</param>
    /// <exception cref="FileNotFoundException">O arquivo informado não existe.</exception>
    public IniConfiguration(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        DataDirectory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidDataException("O caminho do INI não possui diretório válido.");
        Reload();
    }

    public void Reload()
    {
        if (!File.Exists(Path))
        {
            throw new FileNotFoundException("FacilAppSQL.ini não encontrado.", Path);
        }
        Dictionary<string, Dictionary<string, string>> dictionary = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string text = string.Empty;
        string[] array = File.ReadAllLines(Path, Encoding.UTF8);
        foreach (string text2 in array)
        {
            string text3 = text2.Trim();
            if (text3.Length == 0 || text3.StartsWith(';') || text3.StartsWith('#'))
            {
                continue;
            }
            if (text3.StartsWith('[') && text3.EndsWith(']'))
            {
                string text4 = text3;
                text = text4.Substring(1, text4.Length - 1 - 1).Trim();
                dictionary.TryAdd(text, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                continue;
            }
            int num = text3.IndexOf('=');
            if (num > 0 && text.Length != 0)
            {
                dictionary[text][text3.Substring(0, num).Trim()] = text3.Substring(num + 1).Trim();
            }
        }
        values = dictionary.ToDictionary<KeyValuePair<string, Dictionary<string, string>>, string, IReadOnlyDictionary<string, string>>((KeyValuePair<string, Dictionary<string, string>> pair) => pair.Key, (KeyValuePair<string, Dictionary<string, string>> pair) => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public string Get(string section, string key, string fallback = "")
    {
        if (!values.TryGetValue(section, out IReadOnlyDictionary<string, string> value) || !value.TryGetValue(key, out var value2))
        {
            return fallback;
        }
        return value2;
    }

    public int GetInt(string section, string key, int fallback)
    {
        if (!int.TryParse(Get(section, key), out var result))
        {
            return fallback;
        }
        return result;
    }

    public bool GetBoolean(string section, string key, bool fallback = false)
    {
        string text = Get(section, key, fallback ? "1" : "0");
        if (!text.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            return text.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    public string ReadRedacted()
    {
        string input = File.ReadAllText(Path, Encoding.UTF8);
        return Regex.Replace(input, "(?im)^(Senha|Token|.*_TOKEN|.*_SECRET|OPENAI_API_KEY)\\s*=.*$", "$1=********");
    }

    public string ReadComplete()
    {
        return File.ReadAllText(Path, Encoding.UTF8);
    }

    public Task ReplaceRedactedAsync(string content, CancellationToken cancellationToken)
    {
        string content2 = RestoreRedactedSecrets(content);
        return ReplaceAsync(content2, cancellationToken);
    }

    public async Task ReplaceAsync(string content, CancellationToken cancellationToken)
    {
        if (!content.Contains("Porta=", StringComparison.OrdinalIgnoreCase) || !content.Contains("Token=", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("O INI deve conter Porta e Token.");
        }
        await synchronization.WaitAsync(cancellationToken);
        try
        {
            string temporaryPath = $"{Path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            File.Move(temporaryPath, Path, overwrite: true);
            Reload();
        }
        finally
        {
            synchronization.Release();
        }
    }

    public async Task UpdateAsync(IEnumerable<(string Section, string Key, string Value)> updates, CancellationToken cancellationToken)
    {
        await synchronization.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, Dictionary<string, string>> dictionary = values.ToDictionary<KeyValuePair<string, IReadOnlyDictionary<string, string>>, string, Dictionary<string, string>>((KeyValuePair<string, IReadOnlyDictionary<string, string>> section) => section.Key, (KeyValuePair<string, IReadOnlyDictionary<string, string>> section) => section.Value.ToDictionary<KeyValuePair<string, string>, string, string>((KeyValuePair<string, string> item) => item.Key, (KeyValuePair<string, string> item) => item.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            foreach (var (key, key2, value) in updates)
            {
                if (!dictionary.TryGetValue(key, out var value2))
                {
                    value2 = (dictionary[key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                }
                value2[key2] = value;
            }
            StringBuilder stringBuilder = new StringBuilder();
            foreach (KeyValuePair<string, Dictionary<string, string>> item in dictionary)
            {
                item.Deconstruct(out var key3, out var value3);
                string value4 = key3;
                Dictionary<string, string> dictionary3 = value3;
                stringBuilder.Append('[').Append(value4).AppendLine("]");
                foreach (KeyValuePair<string, string> item2 in dictionary3)
                {
                    item2.Deconstruct(out key3, out var value5);
                    string value6 = key3;
                    string value7 = value5;
                    stringBuilder.Append(value6).Append('=').AppendLine(value7);
                }
                stringBuilder.AppendLine();
            }
            string temporaryPath = $"{Path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporaryPath, stringBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            File.Move(temporaryPath, Path, overwrite: true);
            Reload();
        }
        finally
        {
            synchronization.Release();
        }
    }

    private string RestoreRedactedSecrets(string content)
    {
        string text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        bool flag = text.EndsWith('\n');
        string[] array = text.Split('\n');
        string section = string.Empty;
        for (int j = 0; j < array.Length; j++)
        {
            string text2 = array[j].Trim();
            if (text2.StartsWith('[') && text2.EndsWith(']'))
            {
                string text3 = text2;
                section = text3.Substring(1, text3.Length - 1 - 1).Trim();
                continue;
            }
            int num = array[j].IndexOf('=');
            if (num > 0)
            {
                string key = array[j].Substring(0, num).Trim();
                string text4 = array[j].Substring(num + 1).Trim();
                if (text4.Equals("********", StringComparison.Ordinal) && IsSecretKey(key))
                {
                    string text5 = Get(section, key);
                    array[j] = array[j].Substring(0, num + 1) + text5;
                }
            }
        }
        string text6 = string.Join(Environment.NewLine, array);
        if (flag && !text6.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            text6 += Environment.NewLine;
        }
        return text6;
    }

    private static bool IsSecretKey(string key)
    {
        if (!key.Equals("Senha", StringComparison.OrdinalIgnoreCase) && !key.Equals("Token", StringComparison.OrdinalIgnoreCase) && !key.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase) && !key.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase))
        {
            return key.Equals("OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }
}
