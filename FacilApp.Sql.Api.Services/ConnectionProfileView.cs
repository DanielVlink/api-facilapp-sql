using System.Text.Json;

namespace FacilApp.Sql.Api.Services;

public sealed record ConnectionProfileView(string Id, string Nome, string TipoBanco, JsonElement Configuracao, bool SenhaConfigurada, bool Ativo, string AtualizadoEm);
