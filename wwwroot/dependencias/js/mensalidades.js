import { chamarApi, exibirMensagem } from "./api-client.js";

let tabela;
const $ = selector => document.querySelector(selector);

function selecionar(dados) {
    const ativo = Array.isArray(dados) && dados.length === 1;
    $("#lancar").disabled = !ativo;
    $("#historico").disabled = !ativo;
}

function dataBr(valor) {
    if (!valor) return "—";
    const data = new Date(valor);
    return Number.isNaN(data.valueOf()) ? valor : data.toLocaleDateString("pt-BR");
}

async function carregar() {
    try {
        const resposta = await chamarApi("/api/console/mensalidades");
        const dados = (resposta.mensalidades || []).map(item => ({
            empresaId: item.empresaId, cnpj: item.cnpj, empresa: item.nomeFantasia || item.razaoSocial,
            tipo: item.pagamento?.tipoPagamento || "mensalidade", situacao: item.pagamento?.situacao || "sem_pagamento",
            ultimoPagamento: item.pagamento?.ultimoPagamento || "", limiteAcesso: item.pagamento?.limiteAcesso || "",
            diasRestantes: item.pagamento?.diasRestantes ?? 0
        }));
        tabela?.destroy();
        tabela = new Tabulator("#tabela-mensalidades", {
            data: dados, index: "cnpj", layout: "fitColumns", responsiveLayout: "collapse", height: "100%", selectableRows: 1,
            pagination: true, paginationSize: 25, paginationSizeSelector: [25, 50, 100],
            placeholder: "Nenhuma empresa cadastrada.", rowSelectionChanged: selecionar,
            columns: [
                { title: "Empresa", field: "empresa", minWidth: 220, widthGrow: 3, formatter: cell => `<strong>${cell.getValue() || ""}</strong><div style="font-size:9px">${cell.getRow().getData().cnpj || ""}</div>` },
                { title: "Modelo", field: "tipo", minWidth: 110 },
                { title: "Situação", field: "situacao", minWidth: 120, formatter: cell => cell.getValue() === "em_dia" || cell.getValue() === "vitalicio" ? "LIBERADO" : "FALTA PAGAMENTO" },
                { title: "Último pagamento", field: "ultimoPagamento", minWidth: 125, formatter: cell => dataBr(cell.getValue()) },
                { title: "Limite", field: "limiteAcesso", minWidth: 125, formatter: cell => dataBr(cell.getValue()) },
                { title: "Dias", field: "diasRestantes", minWidth: 65, hozAlign: "right" }
            ]
        });
        selecionar([]);
    } catch (erro) { exibirMensagem(`Falha ao carregar mensalidades: ${erro.message}`); }
}

function empresaSelecionada() {
    const empresa = tabela?.getSelectedData()[0];
    if (!empresa) exibirMensagem("Selecione uma empresa.");
    return empresa;
}

function abrirUpdate() {
    const empresa = empresaSelecionada();
    if (!empresa) return;
    const overlay = $("#modal-overlay"), frame = overlay.querySelector("iframe");
    frame.onload = () => frame.contentWindow?.postMessage({ tipo: "mensalidade", empresa }, "*");
    frame.src = `UpdateMensalidade.html?cnpj=${encodeURIComponent(empresa.cnpj)}&empresa=${encodeURIComponent(empresa.empresa)}`;
    overlay.classList.add("aberto"); overlay.setAttribute("aria-hidden", "false");
}

window.addEventListener("message", event => {
    if (event.data?.tipo !== "fechar-mensalidade") return;
    $("#modal-overlay").classList.remove("aberto"); $("#modal-overlay").setAttribute("aria-hidden", "true"); void carregar();
});
$("#atualizar").onclick = carregar;
$("#lancar").onclick = abrirUpdate;
$("#historico").onclick = abrirUpdate;
$("#csv").onclick = () => tabela?.download("csv", "mensalidades-facilapp.csv", { bom: true });
$("#excel").onclick = () => tabela?.download("xlsx", "mensalidades-facilapp.xlsx", { sheetName: "Mensalidades" });
$("#imprimir").onclick = () => window.print();
$("#sair").onclick = () => window.parent?.postMessage({ tipo: "fechar-modal" }, "*");
$("#pesquisa").oninput = event => tabela?.setFilter([["empresa", "like", event.target.value], ["cnpj", "like", event.target.value], ["situacao", "like", event.target.value]]);
carregar();
