using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

public sealed class ExecuteRequest
{
    [JsonPropertyName("funcao")]
    public string Function { get; set; } = string.Empty;

    /// <summary>
    /// Nome alternativo aceito para compatibilidade com integrações que enviam
    /// <c>acao: Login</c> em vez de <c>funcao: Login</c>.
    /// </summary>
    [JsonPropertyName("acao")]
    public string Action { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Arguments { get; set; } = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a função efetiva dando prioridade a <c>funcao</c>. Essa prioridade
    /// impede que uma ação Login paralela seja usada para contornar a autenticação.
    /// </summary>
    public string EffectiveFunction => string.IsNullOrWhiteSpace(Function)
        ? Action
        : Function;

    public string GetString(string name)
    {
        if (Arguments.TryGetValue(name, out var value))
        {
            JsonValueKind valueKind = value.ValueKind;
            if (valueKind != JsonValueKind.Null && valueKind != JsonValueKind.Undefined)
            {
                return value.ToString();
            }
        }
        return string.Empty;
    }

    public int GetInt(string name, int fallback = 0)
    {
        if (!Arguments.TryGetValue(name, out var value) || !value.TryGetInt32(out var value2))
        {
            return fallback;
        }
        return value2;
    }

    public bool GetBoolean(string name, bool fallback = false)
    {
        bool flag = Arguments.TryGetValue(name, out var value);
        bool flag2 = flag;
        if (flag2)
        {
            JsonValueKind valueKind = value.ValueKind;
            bool flag3 = valueKind - 5 <= JsonValueKind.Object;
            flag2 = flag3;
        }
        if (!flag2)
        {
            return fallback;
        }
        return value.GetBoolean();
    }
}
