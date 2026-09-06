using System.Text.Json;

namespace FacilApp.Sql.Api.Services;

public sealed record ConnectionProfileRequest(string? Id, string Nome, string TipoBanco, JsonElement Configuracao, string? Senha);
