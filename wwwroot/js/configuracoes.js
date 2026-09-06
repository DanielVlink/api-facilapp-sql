/** Human-readable names for the database providers supported by the API. */
const databaseNames = {
    sqlserver: "SQL Server",
    postgresql: "PostgreSQL",
    mariadb: "MariaDB",
    mysql: "MySQL",
    oracle: "Oracle",
    hfsql: "HFSQL",
    sqlite: "SQLite"
};

// Pela ponte de visualização, os arquivos HTML usam a API real na porta 5000.
const apiBaseUrl = window.location.port === "3000" ? "http://127.0.0.1:5000" : "";

/** Returns the first element that matches the informed CSS selector. */
const $ = (selector) => document.querySelector(selector);

if (new URLSearchParams(window.location.search).get("embed") === "1") {
    document.body.classList.add("embedded");
}

/** Sends an HTTP request to the local API and converts its response to JSON. */
async function api(path, options = {}) {
    options.headers = {
        ...(options.headers || {}),
        "Content-Type": "application/json"
    };

    const response = await fetch(`${apiBaseUrl}${path}`, options);
    const data = await response.json();

    if (!response.ok) {
        throw new Error(data.erro || `HTTP ${response.status}`);
    }

    return data;
}

/** Downloads a protected configuration artifact as text from the trusted local API. */
async function getConfigurationFile(fileType) {
    const paths = {
        ini: "/api/console/configuracoes/arquivos/ini",
        "config-js": "/api/console/configuracoes/arquivos/config-js"
    };
    const response = await fetch(`${apiBaseUrl}${paths[fileType]}`, { cache: "no-store" });

    if (!response.ok) {
        throw new Error(`Não foi possível obter o arquivo: HTTP ${response.status}`);
    }

    return response.text();
}

/** Displays a temporary non-blocking notification. */
function toast(message) {
    const element = $("#toast");
    element.textContent = message;
    element.classList.add("show");
    setTimeout(() => element.classList.remove("show"), 3000);
}

/** Escapes external text before inserting it into generated HTML. */
function escapeHtml(value) {
    const element = document.createElement("div");
    element.textContent = value || "";
    return element.innerHTML;
}

/** Reads the database requested by the tray from the page query string. */
function getRequestedDatabaseType() {
    const databaseType = new URLSearchParams(window.location.search).get("banco");

    return Object.hasOwn(databaseNames, databaseType)
        ? databaseType
        : null;
}

/** Activates one database tab and its corresponding configuration form. */
function selectDatabasePanel(databaseType) {
    // Only known identifiers may become dynamic CSS selectors.
    if (!Object.hasOwn(databaseNames, databaseType)) {
        return;
    }

    document
        .querySelectorAll("[data-target], .database-panel")
        .forEach((item) => item.classList.remove("active"));

    document
        .querySelector(`[data-target="${databaseType}"]`)
        ?.classList.add("active");
    document
        .querySelector(`[data-database="${databaseType}"]`)
        ?.classList.add("active");
}

/** Builds one independent database form with save and connection-test actions. */
function databasePanel(database, activeDatabaseType) {
    const name = databaseNames[database.tipo] || database.tipo;
    const isActive = database.tipo === activeDatabaseType;
    const passwordPlaceholder = database.senhaConfigurada
        ? "Senha já configurada"
        : "Informe a senha";

    if (database.tipo === "sqlite") {
        return `
            <article
                class="card database-panel ${isActive ? "active" : ""}"
                data-database="${database.tipo}">
                <form class="settings-grid database-form">
                    <label class="check wide">
                        <input
                            name="ativo"
                            type="checkbox"
                            ${database.ativo ? "checked" : ""}>
                        Ativar ${name}
                    </label>
                    <label>
                        Diretório-base autorizado
                        <input
                            name="diretorio"
                            value="${escapeHtml(database.diretorio || "BasesSQLite")}">
                    </label>
                    <label>
                        Arquivo padrão
                        <input
                            name="banco"
                            value="${escapeHtml(database.banco || "facilapp.db")}">
                    </label>
                    <p class="wide">
                        A chamada pode informar outro arquivo em <strong>banco</strong>, desde que
                        permaneça dentro do diretório-base e use .db, .sqlite ou .sqlite3.
                    </p>
                    <div class="actions wide">
                        <button class="primary" type="submit">Salvar ${name}</button>
                        <button class="soft test-database" type="button">Testar conexão</button>
                        <span class="test-result"></span>
                    </div>
                </form>
            </article>`;
    }

    return `
        <article
            class="card database-panel ${isActive ? "active" : ""}"
            data-database="${database.tipo}">
            <form class="settings-grid database-form">
                <label class="check wide">
                    <input
                        name="ativo"
                        type="checkbox"
                        ${database.ativo ? "checked" : ""}>
                    Ativar ${name}
                </label>
                <label>
                    Servidor
                    <input name="servidor" value="${escapeHtml(database.servidor)}">
                </label>
                <label>
                    Porta
                    <input
                        name="porta"
                        type="number"
                        min="0"
                        max="65535"
                        value="${database.porta || ""}">
                </label>
                <label>
                    Banco/Schema
                    <input name="banco" value="${escapeHtml(database.banco)}">
                </label>
                <label>
                    Usuário
                    <input name="usuario" value="${escapeHtml(database.usuario)}">
                </label>
                <label>
                    Senha
                    <input
                        name="senha"
                        type="password"
                        placeholder="${passwordPlaceholder}">
                </label>
                <label>
                    DSN opcional
                    <input name="dsn" value="${escapeHtml(database.dsn || "")}">
                </label>
                <div class="actions wide">
                    <button class="primary" type="submit">Salvar ${name}</button>
                    <button class="soft test-database" type="button">Testar conexão</button>
                    <span class="test-result"></span>
                </div>
            </form>
        </article>`;
}

/** Collects the map, Z-API and alert settings currently displayed. */
function integrationBody() {
    const form = new FormData($("#integration-form"));

    return {
        mapaUserAgent: $("#map-form").elements.mapaUserAgent.value,
        zApiAtiva: form.get("zApiAtiva") === "on",
        zApiBaseUrl: form.get("zApiBaseUrl"),
        zApiInstanceId: form.get("zApiInstanceId"),
        zApiInstanceToken: form.get("zApiInstanceToken"),
        zApiClientToken: form.get("zApiClientToken"),
        alertasAtivos: form.get("alertasAtivos") === "on",
        numeroFacilApp: form.get("numeroFacilApp")
    };
}

/** Fills the general configuration form without ever exposing the saved API token. */
function populateGeneralForm(general) {
    const form = $("#general-form");

    form.elements.nomeInstancia.value = general.nomeInstancia;
    form.elements.nomeServicoWindows.value = general.nomeServicoWindows;
    form.elements.servidorAtivo.checked = general.servidorAtivo;
    form.elements.porta.value = general.porta;
    form.elements.timeoutSegundos.value = general.timeoutSegundos;
    form.elements.baseUrlLocal.value = general.baseUrlLocal;
    form.elements.limiteCorpoKb.value = general.limiteCorpoKb;
    form.elements.maximoConexoes.value = general.maximoConexoes;
    form.elements.ponteClaudeAtiva.checked = general.ponteClaudeAtiva;
    form.elements.portaPonteClaude.value = general.portaPonteClaude;
    form.elements.baseUrlPonteClaude.value = general.baseUrlPonteClaude;
    form.elements.exigirToken.checked = general.exigirToken;
    form.elements.consultaLivre.checked = general.consultaLivre;
    form.elements.permitirLocalhost.checked = general.permitirLocalhost;
    form.elements.permitirRedeLocal.checked = general.permitirRedeLocal;
    form.elements.permitirTailscale.checked = general.permitirTailscale;
    form.elements.logAtivo.checked = general.logAtivo;
    form.elements.nivelLog.value = general.nivelLog;
    form.elements.retencaoDias.value = general.retencaoDias;
    form.elements.arquivosAtivos.checked = general.arquivosAtivos;
    form.elements.tamanhoMaximoArquivoMb.value = general.tamanhoMaximoArquivoMb;
    form.elements.diretorioBaseArquivos.value = general.diretorioBaseArquivos;
    form.elements.extensoesPermitidas.value = general.extensoesPermitidas;

    if (general.tokenConfigurado) {
        form.elements.token.placeholder = "Token já configurado; em branco mantém o atual";
    }

    if (general.tokenPonteClaudeConfigurado) {
        form.elements.tokenPonteClaude.placeholder = "Token da Ponte já configurado; em branco mantém o atual";
    }
}

/** Collects and normalizes the general server settings displayed in the form. */
function generalConfigurationBody() {
    const form = $("#general-form");
    const values = new FormData(form);

    return {
        nomeInstancia: values.get("nomeInstancia"),
        nomeServicoWindows: values.get("nomeServicoWindows"),
        servidorAtivo: values.get("servidorAtivo") === "on",
        porta: Number(values.get("porta")),
        timeoutSegundos: Number(values.get("timeoutSegundos")),
        limiteCorpoKb: Number(values.get("limiteCorpoKb")),
        maximoConexoes: Number(values.get("maximoConexoes")),
        ponteClaudeAtiva: values.get("ponteClaudeAtiva") === "on",
        portaPonteClaude: Number(values.get("portaPonteClaude")),
        tokenPonteClaude: values.get("tokenPonteClaude"),
        token: values.get("token"),
        exigirToken: values.get("exigirToken") === "on",
        consultaLivre: values.get("consultaLivre") === "on",
        permitirLocalhost: values.get("permitirLocalhost") === "on",
        permitirRedeLocal: values.get("permitirRedeLocal") === "on",
        permitirTailscale: values.get("permitirTailscale") === "on",
        logAtivo: values.get("logAtivo") === "on",
        nivelLog: values.get("nivelLog"),
        retencaoDias: Number(values.get("retencaoDias")),
        arquivosAtivos: values.get("arquivosAtivos") === "on",
        tamanhoMaximoArquivoMb: Number(values.get("tamanhoMaximoArquivoMb")),
        diretorioBaseArquivos: values.get("diretorioBaseArquivos"),
        extensoesPermitidas: values.get("extensoesPermitidas")
    };
}

/** Loads masked previews without placing installed secrets in the page DOM. */
async function loadConfigurationArtifacts() {
    const files = await api("/api/console/configuracoes/arquivos", { cache: "no-store" });

    $("#ini-file-name").textContent = files.nomeIni;
    $("#ini-preview").value = files.ini;
    $("#config-js-file-name").textContent = files.nomeConfigJavaScript;
    $("#config-js-preview").value = files.configJavaScript;
    $("#configuration-file-status").textContent = "INI carregado da raiz do EXE.";
}

/** Copies one complete local configuration artifact after an explicit administrator action. */
async function copyConfigurationFile(fileType) {
    const content = await getConfigurationFile(fileType);
    await navigator.clipboard.writeText(content);
    toast("Arquivo completo copiado");
}

/** Downloads one complete local configuration artifact without navigating away from the screen. */
async function downloadConfigurationFile(fileType) {
    const content = await getConfigurationFile(fileType);
    const fileName = fileType === "ini"
        ? $("#ini-file-name").textContent
        : "config.js";
    const mimeType = fileType === "ini"
        ? "text/plain;charset=utf-8"
        : "text/javascript;charset=utf-8";
    const url = URL.createObjectURL(new Blob([content], { type: mimeType }));
    const link = document.createElement("a");

    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
    toast(`${fileName} baixado`);
}

/** Persists one database form without exposing its stored password. */
async function saveDatabase(databaseForm, databaseType) {
    const values = new FormData(databaseForm);

    await api(`/api/console/configuracoes/bancos/${databaseType}`, {
        method: "PUT",
        body: JSON.stringify({
            tipo: databaseType,
            ativo: values.get("ativo") === "on",
            servidor: values.get("servidor") || "",
            porta: Number(values.get("porta") || 0),
            banco: values.get("banco") || "",
            usuario: values.get("usuario") || "",
            senha: values.get("senha") || "",
            dsn: values.get("dsn") || "",
            diretorio: values.get("diretorio") || ""
        })
    });

    databaseForm.elements.senha.value = "";
}

/** Connects save and connection-test actions to every rendered database form. */
function connectDatabaseForms() {
    document.querySelectorAll(".database-form").forEach((databaseForm) => {
        const databaseType = databaseForm.closest("[data-database]").dataset.database;

        databaseForm.onsubmit = async (event) => {
            event.preventDefault();
            await saveDatabase(databaseForm, databaseType);
            toast(`${databaseNames[databaseType]} salvo`);
        };

        databaseForm.querySelector(".test-database").onclick = async () => {
            const result = databaseForm.querySelector(".test-result");
            result.textContent = "Testando...";

            try {
                await saveDatabase(databaseForm, databaseType);
                const response = await api(
                    `/api/console/configuracoes/bancos/${databaseType}/testar`,
                    { method: "POST" }
                );
                result.textContent = `✓ ${response.mensagem}`;
            } catch (error) {
                result.textContent = `✕ ${error.message}`;
            }
        };
    });
}

/** Loads redacted configuration and connects all form actions. */
async function loadSettings() {
    const data = await api("/api/console/configuracoes");
    const requestedDatabaseType = getRequestedDatabaseType();
    const availableDatabaseTypes = data.bancos.map((database) => database.tipo);
    const activeDatabaseType = availableDatabaseTypes.includes(requestedDatabaseType)
        ? requestedDatabaseType
        : availableDatabaseTypes[0];

    populateGeneralForm(data.geral);

    $("#database-tabs").innerHTML = data.bancos
        .map((database) => `
            <button
                class="${database.tipo === activeDatabaseType ? "active" : ""}"
                data-target="${database.tipo}">
                ${databaseNames[database.tipo]}
            </button>`)
        .join("");

    $("#database-panels").innerHTML = data.bancos
        .map((database) => databasePanel(database, activeDatabaseType))
        .join("");

    const integration = data.integracoes;
    $("#map-form").elements.mapaUserAgent.value = integration.mapaUserAgent;

    const form = $("#integration-form");
    form.elements.zApiAtiva.checked = integration.zApiAtiva;
    form.elements.zApiBaseUrl.value = integration.zApiBaseUrl;
    form.elements.zApiInstanceId.value = integration.zApiInstanceId;
    form.elements.alertasAtivos.checked = integration.alertasAtivos;
    form.elements.numeroFacilApp.value = integration.numeroFacilApp;

    if (integration.zApiInstanceTokenConfigurado) {
        form.elements.zApiInstanceToken.placeholder = "Token já configurado";
    }

    if (integration.zApiClientTokenConfigurado) {
        form.elements.zApiClientToken.placeholder = "Token já configurado";
    }

    document.querySelectorAll("[data-target]").forEach((button) => {
        button.onclick = () => selectDatabasePanel(button.dataset.target);
    });

    connectDatabaseForms();
    selectDatabasePanel(activeDatabaseType);
    await loadConfigurationArtifacts();
}

$("#general-form").onsubmit = async (event) => {
    event.preventDefault();

    const response = await api("/api/console/configuracoes/geral", {
        method: "PUT",
        body: JSON.stringify(generalConfigurationBody())
    });

    event.target.elements.token.value = "";
    event.target.elements.tokenPonteClaude.value = "";

    const warning = $("#restart-warning");
    warning.textContent = response.mensagem;
    warning.classList.add("changed");
    toast("Configuração geral salva");
};

// The local URL preview follows the port while the administrator edits it.
$("#general-form").elements.porta.addEventListener("input", (event) => {
    $("#general-form").elements.baseUrlLocal.value = `http://127.0.0.1:${event.target.value}`;
});

// A visualização da Ponte acompanha a porta antes mesmo de gravar o formulário.
$("#general-form").elements.portaPonteClaude.addEventListener("input", (event) => {
    $("#general-form").elements.baseUrlPonteClaude.value = `http://127.0.0.1:${event.target.value}`;
});

document.querySelectorAll("[data-copy-file]").forEach((button) => {
    button.onclick = async () => {
        try {
            await copyConfigurationFile(button.dataset.copyFile);
        } catch (error) {
            toast(error.message);
        }
    };
});

document.querySelectorAll("[data-download-file]").forEach((button) => {
    button.onclick = async () => {
        try {
            await downloadConfigurationFile(button.dataset.downloadFile);
        } catch (error) {
            toast(error.message);
        }
    };
});

$("#reload-installed-ini").onclick = async () => {
    try {
        await loadConfigurationArtifacts();
        toast("INI instalado recarregado");
    } catch (error) {
        toast(error.message);
    }
};

$("#apply-installed-ini").onclick = async () => {
    const status = $("#configuration-file-status");

    try {
        status.textContent = "Gravando INI...";
        const response = await api("/api/console/configuracoes/arquivos/aplicar", {
            method: "POST",
            body: JSON.stringify({ ini: $("#ini-preview").value })
        });
        await loadConfigurationArtifacts();
        status.textContent = response.mensagem;
        toast("INI gravado no servidor local");
    } catch (error) {
        status.textContent = error.message;
        toast(error.message);
    }
};

$("#map-form").onsubmit = async (event) => {
    event.preventDefault();
    await api("/api/console/configuracoes/integracoes", {
        method: "PUT",
        body: JSON.stringify(integrationBody())
    });
    toast("Mapa salvo");
};

$("#integration-form").onsubmit = async (event) => {
    event.preventDefault();
    await api("/api/console/configuracoes/integracoes", {
        method: "PUT",
        body: JSON.stringify(integrationBody())
    });
    event.target.elements.zApiInstanceToken.value = "";
    event.target.elements.zApiClientToken.value = "";
    toast("Z-API e alertas salvos");
};

api("/status")
    .then(() => {
        $("#health-dot").classList.add("ok");
        $("#health-text").textContent = "API online";
    })
    .catch(() => {
        $("#health-text").textContent = "API indisponível";
    });

loadSettings().catch((error) => toast(error.message));
