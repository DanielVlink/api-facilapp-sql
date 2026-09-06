/**
 * Funções compartilhadas para comunicação com a API FacilApp SQL.
 * Este arquivo não contém regras específicas de nenhuma tela.
 */

/**
 * Executa uma chamada JSON aos endpoints locais do console.
 * O endpoint público /executar possui cliente OAuth próprio na tela de testes.
 * @param {string} caminho Caminho relativo do endpoint.
 * @param {RequestInit} opcoes Opções aceitas pela função fetch.
 * @returns {Promise<object>} Corpo JSON devolvido pela API.
 */
export async function chamarApi(caminho, opcoes = {}) {
    const parametros = new URLSearchParams(window.location.search);
    // Usa a mesma origem que serviu a tela. Assim, a console acompanha a porta
    // configurada no INI (por exemplo, 5050) sem depender de um endereço fixo.
    const apiBase = parametros.get("api") || window.location.origin;
    const url = new URL(caminho, apiBase.endsWith("/") ? apiBase : `${apiBase}/`);
    const token = window.facilAppAccessToken
        || (() => { try { return window.parent?.facilAppAccessToken || ""; } catch { return ""; } })()
        || localStorage.getItem("facilapp_access_token")
        || "";
    const resposta = await fetch(url, {
        ...opcoes,
        headers: {
            "Content-Type": "application/json",
            ...(token ? { Authorization: `Bearer ${token}` } : {}),
            ...(opcoes.headers || {}),
        },
    });

    const texto = await resposta.text();
    let conteudo;
    try {
        conteudo = texto ? JSON.parse(texto) : {};
    } catch {
        throw new Error("A API retornou uma resposta que não é JSON.");
    }

    if (!resposta.ok) {
        throw new Error(
            conteudo.erro || conteudo.error || `Erro HTTP ${resposta.status}`,
        );
    }

    return conteudo;
}

/** Exibe uma mensagem temporária no rodapé da tela. */
export function exibirMensagem(mensagem) {
    const elemento = document.querySelector("#mensagem");

    if (!elemento) {
        return;
    }

    elemento.textContent = mensagem;
    elemento.classList.add("visivel");
    window.setTimeout(() => elemento.classList.remove("visivel"), 3000);
}

/** Normaliza CNPJ ou CPF, mantendo somente algarismos. */
export function somenteDigitos(valor) {
    return String(valor || "").replace(/\D/g, "");
}
