import { chamarApi, exibirMensagem } from "./api-client.js";

let tabelaEmpresas;

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
            layout: "fitDataStretch",
            height: "520px",
            selectableRows: 1,
            pagination: true,
            paginationSize: 25,
            paginationSizeSelector: [25, 50, 100],
            placeholder: "Nenhuma empresa cadastrada.",
            columns: [
                { title: "CNPJ", field: "cnpj", width: 155 },
                { title: "Razão social", field: "razaoSocial", minWidth: 240 },
                { title: "Nome fantasia", field: "nomeFantasia", minWidth: 180 },
                { title: "Município", field: "cidade", minWidth: 150 },
                { title: "UF", field: "uf", width: 65 },
                { title: "E-mail", field: "email", minWidth: 220 },
            ],
        });
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

document.querySelector("#atualizar").addEventListener("click", carregarEmpresas);
document.querySelector("#novo").addEventListener("click", () => {
    window.location.href = "UpdateClientes.html";
});
document.querySelector("#alterar").addEventListener("click", () => {
    const empresa = obterEmpresaSelecionada();
    if (empresa) window.location.href = `UpdateClientes.html?cnpj=${empresa.cnpj}`;
});
document.querySelector("#imprimir").addEventListener("click", () => {
    const empresa = obterEmpresaSelecionada();
    if (empresa) window.open(`PrintClientes.html?cnpj=${empresa.cnpj}`, "_blank");
});
document.querySelector("#excel").addEventListener("click", () => {
    tabelaEmpresas?.download("xlsx", "empresas-facilapp-sql.xlsx", {
        sheetName: "Empresas",
    });
});
document.querySelector("#pesquisa").addEventListener("input", (evento) => {
    tabelaEmpresas?.setFilter("razaoSocial", "like", evento.target.value);
});

carregarEmpresas();
