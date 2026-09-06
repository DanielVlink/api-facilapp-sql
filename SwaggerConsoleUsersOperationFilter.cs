using System;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

/// <summary>
/// Documenta as rotas de usuários da API e seus menus no Swagger.
/// </summary>
public sealed class SwaggerConsoleUsersOperationFilter : IOperationFilter
{
    /// <summary>
    /// Acrescenta descrição operacional às rotas de usuários do console.
    /// </summary>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        string path = context.ApiDescription.RelativePath ?? string.Empty;
        string method = context.ApiDescription.HttpMethod ?? string.Empty;
        if (!path.StartsWith("api/console/empresas/{empresaId}/usuarios", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("api/console/usuarios/{id}", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        operation.Tags = new[] { new OpenApiTag { Name = "Usuários" } };
        operation.Description = "Endpoint da API única, acessível por qualquer origem autorizada com access token: localhost, LAN, internet ou Tailscale. O usuário permanece vinculado à empresa indicada por `empresaId`; senhas, hashes e Client Secrets nunca são retornados.";
        operation.Security = new System.Collections.Generic.List<OpenApiSecurityRequirement>
        {
            new()
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                }] = Array.Empty<string>()
            }
        };

        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/usuarios", StringComparison.OrdinalIgnoreCase))
        {
            operation.Summary = "Lista os usuários ativos de uma empresa";
            return;
        }

        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/menus", StringComparison.OrdinalIgnoreCase))
        {
            operation.Summary = "Consulta menus e dashboards de um usuário";
            operation.Description += " Retorna `menu_data`, `menu_superior_data` e `dashboard_data` como listas JSON.";
            return;
        }

        if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/menus", StringComparison.OrdinalIgnoreCase))
        {
            operation.Summary = "Grava menus e dashboards de um usuário";
            operation.Description += " O corpo contém as listas JSON `menu_data`, `menu_superior_data` e `dashboard_data`.";
            return;
        }

        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            operation.Summary = "Consulta um usuário da empresa";
            return;
        }

        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            operation.Summary = "Cadastra usuário da empresa";
            operation.Description += " Informe nome, usuário, senha, `client_id` e, opcionalmente, os três JSONs de menu.";
            return;
        }

        if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            operation.Summary = "Exclui usuário";
            operation.Description += " A exclusão impede novos logins imediatamente.";
            return;
        }

        if (path.EndsWith("/senha", StringComparison.OrdinalIgnoreCase))
        {
            operation.Summary = "Altera somente a senha de um usuário";
            return;
        }

        operation.Summary = "Altera dados e menus de um usuário";
    }
}
