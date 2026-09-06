using System.Collections.Generic;

namespace FacilApp.Sql.Api.Services;

public sealed record ApiCredentialView(string ClientId, string Nome, string Tipo, IReadOnlyList<string> Permissoes, bool Ativo, string CriadoEm);
