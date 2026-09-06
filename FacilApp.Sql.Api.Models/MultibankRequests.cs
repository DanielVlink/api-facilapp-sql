using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FacilApp.Sql.Api.Models;

public abstract class MultibankRequestBase
{
    [JsonPropertyName("tipo_banco")]
    public string DatabaseType { get; set; } = "sqlite";

    [JsonPropertyName("servidor")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("banco")]
    public string Database { get; set; } = string.Empty;

    [JsonPropertyName("tabela")]
    public string Table { get; set; } = string.Empty;
}

public sealed class MultibankQueryRequest : MultibankRequestBase
{
    [JsonPropertyName("campos")]
    public string Fields { get; set; } = "*";

    [JsonPropertyName("where")]
    public Dictionary<string, object?> Where { get; set; } = new();
}

public sealed class MultibankInsertRequest : MultibankRequestBase
{
    [JsonPropertyName("dados")]
    public Dictionary<string, object?> Data { get; set; } = new();
}

public sealed class MultibankUpdateRequest : MultibankRequestBase
{
    [JsonPropertyName("dados")]
    public Dictionary<string, object?> Data { get; set; } = new();

    [JsonPropertyName("where")]
    public Dictionary<string, object?> Where { get; set; } = new();
}

public sealed class MultibankDeleteRequest : MultibankRequestBase
{
    [JsonPropertyName("where")]
    public Dictionary<string, object?> Where { get; set; } = new();
}

public sealed class MultibankSchemaRequest : MultibankRequestBase
{
    [JsonPropertyName("campos")]
    public List<MultibankColumnDefinition> Columns { get; set; } = new();
}

/// <summary>
/// Solicita a publicação da estrutura cadastral atualmente instalada na API
/// para outro banco suportado. A origem é sempre Dados\facilapp_sql.db.
/// </summary>
public sealed class MultibankApiSchemaRequest : MultibankRequestBase
{
    /// <summary>
    /// Lista opcional de tabelas da API. Vazia significa todas as tabelas do
    /// SQLite cadastral instalado.
    /// </summary>
    [JsonPropertyName("tabelas")]
    public List<string> Tables { get; set; } = new();
}

public sealed class MultibankColumnDefinition
{
    [JsonPropertyName("nome")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("tipo")]
    public string Type { get; set; } = "texto";

    [JsonPropertyName("tamanho")]
    public int? Length { get; set; }

    [JsonPropertyName("nulo")]
    public bool Nullable { get; set; } = true;

    [JsonPropertyName("chave_primaria")]
    public bool PrimaryKey { get; set; }

    [JsonPropertyName("auto_incremento")]
    public bool AutoIncrement { get; set; }
}
