using System.Collections.Generic;

namespace FacilApp.Sql.Api.Services;

public sealed record ApiCredentialRequest(string Nome, string Tipo, IReadOnlyList<string> Permissoes);
