namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Menus e dashboards associados a um usuário de uma empresa.
/// </summary>
/// <param name="IdUsuario">Identificador interno do usuário.</param>
/// <param name="IdFilial">Identificador interno da empresa proprietária.</param>
/// <param name="MenuData">Lista JSON do menu lateral.</param>
/// <param name="MenuSuperiorData">Lista JSON do menu superior.</param>
/// <param name="DashBoardData">Lista JSON dos dashboards.</param>
public sealed record LocalUserMenuView(
    string IdUsuario,
    string IdFilial,
    string MenuData,
    string MenuSuperiorData,
    string DashBoardData);
