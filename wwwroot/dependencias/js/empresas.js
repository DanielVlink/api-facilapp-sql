import { chamarApi, exibirMensagem } from "./api-client.js";

let tabelaEmpresas;

/** Atualiza os comandos que dependem da seleção de uma empresa. */
function atualizarEstadoAcoes(dadosSelecionados) {
    const habilitado = Array.isArray(dadosSelecionados)
        ? dadosSelecionados.length > 0
        : Boolean(document.querySelector(".tabulator-row.tabulator-selected") || tabelaEmpresas?.getSelectedData()[0]);
    ["alterar", "excluir", "login"].forEach((id) => {
        const botao = document.querySelector(`#${id}`);
        if (botao) botao.disabled = !habilitado;
    });
}

/** Cria ou recria o browse de empresas. */
async function carregarEmpresas() {
    try {
        const resposta = await chamarApi("/api/console/empresas");

        if (tabelaEmpresas) {
            tabelaEmpresas.destroy();
        }

        tabelaEmpresas = new Tabulator("#tabela-empresas", {
            data: resposta.empresas,
            index: "cnpj",
            // A grade ocupa somente a largura disponível do iframe. Em telas menores,
            // o Tabulator reduz primeiro as colunas flexíveis sem criar rolagem horizontal.
            layout: "fitColumns",
            responsiveLayout: "collapse",
            height: "100%",
            // O browse trabalha sobre uma única empresa por vez.
            selectableRows: 1,
            pagination: true,
            paginationSize: 25,
            paginationSizeSelector: [25, 50, 100],
            placeholder: "Nenhuma empresa cadastrada.",
            rowSelectionChanged: atualizarEstadoAcoes,
            locale: true,
            langs: { "default": { pagination: { first: "Primeira", last: "Última", prev: "Anterior", next: "Próxima", page_size: "Linhas por página" } } },
            columns: [
                { title: "Nome ou razão social", field: "razaoSocial", minWidth: 210, widthGrow: 3, formatter: (cell) => `<strong>${cell.getValue() || ""}</strong><div style="font-size:9.17px">${cell.getRow().getData().cnpj || ""}</div>` },
                { title: "Cidade - UF", field: "cidade", minWidth: 160, widthGrow: 2, formatter: (cell) => `${cell.getValue() || ""} - ${cell.getRow().getData().uf || ""}` },
                { title: "Criação", field: "criadoEm", minWidth: 125, widthGrow: 1 },
                { title: "Telefone", field: "telefone", minWidth: 100, widthGrow: 1 },
            ],
        });
        atualizarEstadoAcoes();
    } catch (erro) {
        exibirMensagem(`Falha ao carregar empresas: ${erro.message}`);
    }
}

/** Retorna a empresa selecionada ou avisa o usuário quando nada foi escolhido. */
function obterEmpresaSelecionada() {
    const empresa = tabelaEmpresas?.getSelectedData()[0];

    if (!empresa) {
        exibirMensagem("Selecione uma empresa.");
    }

    return empresa;
}

/** Abre uma tela de Update sobre o Browse, preservando a listagem ao fundo. */
function abrirUpdate(url, empresa) {
    const overlay = document.querySelector("#modal-overlay");
    const frame = overlay?.querySelector("iframe");
    if (!overlay || !frame) return;
    const enviarEmpresa = () => {
        if (empresa && frame.contentWindow) {
            frame.contentWindow.postMessage({ tipo: "preencher-update", empresa }, "*");
        }
    };
    frame.addEventListener("load", () => {
        enviarEmpresa();
        // Reenvia após a inicialização do formulário para evitar que uma
        // abertura em cache perca a mensagem de preenchimento.
        window.setTimeout(enviarEmpresa, 150);
    }, { once: true });
    frame.src = url;
    overlay.classList.add("aberto");
    overlay.setAttribute("aria-hidden", "false");
}

window.addEventListener("message", (evento) => {
    if (evento.data?.tipo === "abrir-login-update" && evento.data.url) {
        const overlay = document.querySelector("#modal-overlay");
        const frame = overlay?.querySelector("iframe");
        if (overlay && frame) {
            frame.src = evento.data.url;
            overlay.classList.add("aberto");
            overlay.setAttribute("aria-hidden", "false");
        }
        return;
    }
    if (evento.data?.tipo === "colunas-apply") {
        (evento.data.visiveis || []).forEach(({ campo, visivel }) => {
            const coluna = tabelaEmpresas?.getColumn(campo);
            if (coluna) visivel ? coluna.show() : coluna.hide();
        });
        document.querySelector("#modal-overlay")?.classList.remove("aberto");
        document.querySelector("#modal-overlay")?.setAttribute("aria-hidden", "true");
        return;
    }
    if (evento.data?.tipo !== "fechar-modal") return;
    const overlay = document.querySelector("#modal-overlay");
    if (overlay) {
        overlay.classList.remove("aberto");
        overlay.setAttribute("aria-hidden", "true");
        // O Update grava no banco antes de enviar este evento. Recarregamos
        // a listagem ao fechar para refletir imediatamente os dados novos.
        void carregarEmpresas();
    }
});

document.querySelector("#atualizar").addEventListener("click", carregarEmpresas);
document.querySelector("#novo").addEventListener("click", () => {
    abrirUpdate("UpdateEmpresa.html");
});
document.querySelector("#alterar").addEventListener("click", async () => {
    const empresa = obterEmpresaSelecionada();
    if (empresa) {
        let empresaCompleta = empresa;
        try {
            const resposta = await chamarApi(`/api/console/empresas/${encodeURIComponent(empresa.cnpj)}`);
            empresaCompleta = resposta.empresa || empresa;
        } catch {
            // Mantém os dados da linha caso a consulta detalhada não esteja disponível.
        }
        const campos = [
            "cnpj", "razaoSocial", "nomeFantasia", "inscricaoEstadual", "inscricaoMunicipal",
            "email", "telefone", "cep", "codigoIbge", "uf", "cidade", "endereco", "numero",
            "complemento", "bairro", "tipoPagamento",
        ];
        const parametros = new URLSearchParams();
        campos.forEach((campo) => parametros.set(campo, empresaCompleta[campo] || ""));
        abrirUpdate(`UpdateEmpresa.html?${parametros.toString()}`, empresaCompleta);
    }
});
document.querySelector("#excluir").addEventListener("click", async () => {
    const empresa = obterEmpresaSelecionada();
    if (!empresa) return;
    if (!window.confirm(`Excluir definitivamente a empresa ${empresa.razaoSocial || empresa.cnpj}?`)) return;
    try {
        await chamarApi(`/api/console/empresas/${encodeURIComponent(empresa.cnpj)}`, { method: "DELETE" });
        await carregarEmpresas();
    } catch (erro) {
        exibirMensagem(`Não foi possível excluir a empresa: ${erro.message}`);
    }
});
document.querySelector("#login").addEventListener("click", () => {
    const empresa = obterEmpresaSelecionada();
    if (!empresa) return;
    const parametros = new URLSearchParams({
        empresa: empresa.razaoSocial || "",
        cnpj: empresa.cnpj || "",
        empresaId: empresa.id || "",
    });
    abrirUpdate(`LoginEmpresa.html?${parametros.toString()}`);
});
document.querySelector("#imprimir").addEventListener("click", () => {
    if (!tabelaEmpresas) return;
    const overlay = document.querySelector("#modal-overlay");
    const frame = overlay?.querySelector("iframe");
    if (!overlay || !frame) return;
    sessionStorage.setItem("facilapp-empresas-relatorio", JSON.stringify(tabelaEmpresas.getData("active")));
    frame.src = "RpEmpresa.html";
    overlay.classList.add("aberto");
    overlay.setAttribute("aria-hidden", "false");
});
document.querySelector("#excel").addEventListener("click", () => {
    tabelaEmpresas?.download("xlsx", "empresas-facilapp-sql.xlsx", {
        sheetName: "Empresas",
    });
});
document.querySelector("#csv").addEventListener("click", () => {
    tabelaEmpresas?.download("csv", "empresas-facilapp-sql.csv", { bom: true });
});
document.querySelector("#colunas").addEventListener("click", () => {
    const overlay = document.querySelector("#modal-overlay");
    const frame = overlay?.querySelector("iframe");
    if (!overlay || !frame || !tabelaEmpresas) return;
    const colunas = tabelaEmpresas.getColumns().map((coluna) => {
        const definicao = coluna.getDefinition();
        return { campo: definicao.field, titulo: definicao.title, visivel: coluna.isVisible() };
    }).filter((coluna) => coluna.campo);
    const itens = colunas.map((coluna) => `<label><input type="checkbox" data-campo="${coluna.campo}" ${coluna.visivel ? "checked" : ""}>${coluna.titulo}</label>`).join("");
    frame.srcdoc = `<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><style>html,body{height:100%;margin:0;font:11px Arial;color:#171a10;background:transparent}.modal{width:min(420px,calc(100% - 28px));margin:20vh auto 0;padding:0 14px 14px;border:1px solid #9caf77;border-radius:7px;background:#d8e9c5;box-shadow:0 8px 24px #171a1066}.cabecalho{display:flex;justify-content:space-between;align-items:center;margin:0 -14px 10px;padding:8px 14px;background:#bfd5a5;border-bottom:1px solid #9caf77;font-size:16px;font-weight:700}.cabecalho button,.acoes button{height:25px;padding:3px 9px;border:0;border-radius:5px;background:#59651f;color:#fff;font-weight:700;cursor:pointer}.cabecalho button{font-size:16px;width:28px}.lista{display:grid;gap:7px;padding:7px}.lista label{display:flex;gap:7px;align-items:center}.acoes{display:flex;justify-content:flex-end;gap:7px;margin-top:10px;padding-top:10px;border-top:1px solid #9caf77}</style></head><body><section class="modal"><div class="cabecalho"><span>Colunas visíveis</span><button type="button" onclick="parent.postMessage({tipo:'fechar-modal'},'*')">×</button></div><div class="lista">${itens}</div><div class="acoes"><button type="button" id="cancelar">Cancelar</button><button type="button" id="aplicar">Aplicar</button></div></section><script>document.querySelector('#cancelar').onclick=()=>parent.postMessage({tipo:'fechar-modal'},'*');document.querySelector('#aplicar').onclick=()=>parent.postMessage({tipo:'colunas-apply',visiveis:[...document.querySelectorAll('input')].map(e=>({campo:e.dataset.campo,visivel:e.checked}))},'*');<\/script></body></html>`;
    overlay.classList.add("aberto");
    overlay.setAttribute("aria-hidden", "false");
});
document.querySelector("#pesquisa").addEventListener("input", (evento) => {
    tabelaEmpresas?.setFilter("razaoSocial", "like", evento.target.value);
});
document.querySelector("#tabela-empresas")?.addEventListener("click", () => {
    window.setTimeout(() => atualizarEstadoAcoes(), 0);
});

carregarEmpresas();
