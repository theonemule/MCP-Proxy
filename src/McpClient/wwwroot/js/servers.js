import { api, el, template } from './shared.js';

export function renderServers(view) {
    view.replaceChildren(template('tpl-servers'));

    const configurationList = view.querySelector('#configuration-list');
    const configurationAlert = view.querySelector('#configuration-alert');
    const capabilities = view.querySelector('#capabilities');
    const list = view.querySelector('#server-list');
    const discover = view.querySelector('#discover');
    const editorElement = view.querySelector('#server-editor');
    const editor = new bootstrap.Modal(editorElement);
    const resultModal = new bootstrap.Modal(view.querySelector('#capability-result'));
    const form = view.querySelector('#server-form');
    let configurations = [];
    let editingServer = null;

    async function loadConfigurations() {
        configurationAlert.replaceChildren();
        configurationList.replaceChildren(el('div', 'py-3 text-secondary', 'Loading…'));

        try {
            configurations = await api('/api/server-configurations');
            renderConfigurations();
        } catch (error) {
            configurationList.replaceChildren();
            configurationAlert.replaceChildren(el('div', 'alert alert-danger', error.message));
        }
    }

    function renderConfigurations() {
        configurationList.replaceChildren();
        discover.disabled = !configurations.some(server => server.enabled);

        if (configurations.length === 0) {
            configurationList.appendChild(el('div', 'py-4 text-secondary', 'No MCP servers configured.'));
            return;
        }

        configurations.forEach(server => configurationList.appendChild(configurationRow(server)));
    }

    function configurationRow(server) {
        const row = el('div', 'server-configuration d-flex flex-column flex-lg-row gap-3 py-3 align-items-lg-center');
        const details = el('div', 'flex-grow-1 overflow-hidden');
        const title = el('div', 'd-flex align-items-center gap-2');
        title.append(
            el('strong', null, server.name),
            el('span', `badge ${server.enabled ? 'text-bg-success' : 'text-bg-secondary'}`, server.enabled ? 'enabled' : 'disabled'));
        details.append(title, el('div', 'small text-secondary text-break', server.endpoint));
        if (server.description) details.appendChild(el('div', 'small mt-1', server.description));

        const actions = el('div', 'd-flex align-items-center gap-2 flex-shrink-0');
        const toggle = el('input', 'form-check-input mt-0');
        toggle.type = 'checkbox';
        toggle.checked = server.enabled;
        toggle.title = server.enabled ? 'Disable server' : 'Enable server';
        toggle.addEventListener('change', async () => {
            toggle.disabled = true;
            try {
                const updated = await saveConfiguration({ ...server, enabled: toggle.checked });
                configurations = configurations.map(item => item.id === updated.id ? updated : item);
                renderConfigurations();
            } catch (error) {
                toggle.checked = !toggle.checked;
                configurationAlert.replaceChildren(el('div', 'alert alert-danger', error.message));
            } finally {
                toggle.disabled = false;
            }
        });

        const edit = el('button', 'btn btn-sm btn-outline-secondary', 'Edit');
        edit.type = 'button';
        edit.addEventListener('click', () => openEditor(server));

        const remove = el('button', 'btn btn-sm btn-outline-danger', 'Delete');
        remove.type = 'button';
        remove.addEventListener('click', async () => {
            if (!window.confirm(`Delete MCP server “${server.name}”?`)) return;
            try {
                await api(`/api/server-configurations/${encodeURIComponent(server.id)}`, { method: 'DELETE' });
                configurations = configurations.filter(item => item.id !== server.id);
                capabilities.classList.add('d-none');
                renderConfigurations();
            } catch (error) {
                configurationAlert.replaceChildren(el('div', 'alert alert-danger', error.message));
            }
        });

        actions.append(toggle, edit, remove);
        row.append(details, actions);
        return row;
    }

    async function loadCapabilities() {
        discover.disabled = true;
        capabilities.classList.remove('d-none');
        list.replaceChildren(el('div', 'text-secondary', 'Discovering…'));

        try {
            const reports = await api('/api/servers');
            list.replaceChildren();

            if (reports.length === 0) {
                list.appendChild(el('div', 'alert alert-warning', 'No enabled MCP servers.'));
                return;
            }

            const accordion = el('div', 'accordion');
            accordion.id = 'servers';
            reports.forEach((report, index) => accordion.appendChild(capabilityCard(report, index, showResult)));
            list.appendChild(accordion);
        } catch (error) {
            list.replaceChildren(el('div', 'alert alert-danger', error.message));
        } finally {
            discover.disabled = !configurations.some(server => server.enabled);
        }
    }

    function showResult(title, value) {
        view.querySelector('#capability-result-title').textContent = title;
        view.querySelector('#capability-result-body').textContent =
            typeof value === 'string' ? value : JSON.stringify(value, null, 2);
        resultModal.show();
    }

    function openEditor(server = null) {
        form.reset();
        editingServer = server;
        view.querySelector('#server-editor-title').textContent = server ? 'Edit MCP server' : 'Add MCP server';
        view.querySelector('#server-form-error').classList.add('d-none');
        view.querySelector('#server-id').value = server?.id ?? '';
        view.querySelector('#server-name').value = server?.name ?? '';
        view.querySelector('#server-endpoint').value = server?.endpoint ?? '';
        view.querySelector('#server-description').value = server?.description ?? '';
        view.querySelector('#server-transport').value = server?.transportMode ?? 'AutoDetect';

        const tokenMap = { 0: 'None', 1: 'AccessToken', 2: 'IdToken', 3: 'ApiKey', '0': 'None', '1': 'AccessToken', '2': 'IdToken', '3': 'ApiKey' };
        const rawToken = server?.forwardToken;
        const tokenVal = tokenMap[rawToken] ?? rawToken ?? 'AccessToken';
        view.querySelector('#server-token').value = tokenVal;

        view.querySelector('#server-timeout').value = server?.connectionTimeoutSeconds ?? 30;
        view.querySelector('#server-scheme').value = server?.authorizationScheme ?? 'Bearer';
        view.querySelector('#server-apikey-header').value = server?.apiKeyHeaderName ?? 'X-Api-Key';
        view.querySelector('#server-apikey').value = '';
        view.querySelector('#server-enabled').checked = server?.enabled ?? true;
        updateCredentialFieldVisibility();

        const headers = view.querySelector('#server-headers');
        headers.replaceChildren();
        Object.entries(server?.additionalHeaders ?? {}).forEach(([name, value]) =>
            headers.appendChild(headerRow(name, value)));
        editor.show();
    }

    function updateCredentialFieldVisibility() {
        const tokenSelect = view.querySelector('#server-token');
        const mode = tokenSelect ? tokenSelect.value : 'AccessToken';
        const isApiKey = mode === 'ApiKey' || mode === '3' || mode === 3;
        const isNone = mode === 'None' || mode === '0' || mode === 0;

        const schemeGrp = view.querySelector('#server-scheme-group');
        const keyGrp = view.querySelector('#server-apikey-group');
        const valGrp = view.querySelector('#server-apikey-value-group');

        if (schemeGrp) schemeGrp.classList.toggle('d-none', isNone || isApiKey);
        if (keyGrp) keyGrp.classList.toggle('d-none', !isApiKey);
        if (valGrp) valGrp.classList.toggle('d-none', !isApiKey);
    }

    function headerRow(name = '', value = '') {
        const row = el('div', 'server-header row g-2');
        const nameColumn = el('div', 'col-sm-4');
        const nameInput = el('input', 'form-control form-control-sm header-name');
        nameInput.placeholder = 'Header name';
        nameInput.value = name;
        const valueColumn = el('div', 'col-sm-6');
        const valueInput = el('input', 'form-control form-control-sm header-value');
        valueInput.placeholder = 'Value';
        valueInput.value = value;
        valueInput.autocomplete = 'off';
        const removeColumn = el('div', 'col-sm-2 d-grid');
        const remove = el('button', 'btn btn-sm btn-outline-danger', 'Remove');
        remove.type = 'button';
        remove.addEventListener('click', () => row.remove());
        nameColumn.appendChild(nameInput);
        valueColumn.appendChild(valueInput);
        removeColumn.appendChild(remove);
        row.append(nameColumn, valueColumn, removeColumn);
        return row;
    }

    async function saveConfiguration(server) {
        return api(`/api/server-configurations${server.id ? `/${encodeURIComponent(server.id)}` : ''}`, {
            method: server.id ? 'PUT' : 'POST',
            body: JSON.stringify(server)
        });
    }

    view.querySelector('#add-server').addEventListener('click', () => openEditor());
    view.querySelector('#add-header').addEventListener('click', () =>
        view.querySelector('#server-headers').appendChild(headerRow()));
    view.querySelector('#server-token').addEventListener('change', updateCredentialFieldVisibility);
    discover.addEventListener('click', loadCapabilities);

    form.addEventListener('submit', async event => {
        event.preventDefault();
        const error = view.querySelector('#server-form-error');
        const save = view.querySelector('#save-server');
        const additionalHeaders = {};
        view.querySelectorAll('.server-header').forEach(row => {
            const name = row.querySelector('.header-name').value.trim();
            if (name) additionalHeaders[name] = row.querySelector('.header-value').value;
        });

        const server = {
            id: view.querySelector('#server-id').value,
            name: view.querySelector('#server-name').value,
            endpoint: view.querySelector('#server-endpoint').value,
            description: view.querySelector('#server-description').value || null,
            transportMode: view.querySelector('#server-transport').value,
            forwardToken: view.querySelector('#server-token').value,
            connectionTimeoutSeconds: Number(view.querySelector('#server-timeout').value),
            authorizationScheme: view.querySelector('#server-scheme').value,
            apiKeyHeaderName: view.querySelector('#server-apikey-header').value || 'X-Api-Key',
            // A blank field keeps whatever key was already saved, so re-saving other fields never erases it.
            apiKey: view.querySelector('#server-apikey').value || editingServer?.apiKey || null,
            enabled: view.querySelector('#server-enabled').checked,
            additionalHeaders
        };

        save.disabled = true;
        error.classList.add('d-none');
        try {
            await saveConfiguration(server);
            editor.hide();
            capabilities.classList.add('d-none');
            await loadConfigurations();
        } catch (submitError) {
            error.textContent = submitError.message;
            error.classList.remove('d-none');
        } finally {
            save.disabled = false;
        }
    });

    loadConfigurations();
}

function capabilityCard(report, index, showResult) {
    const item = el('div', 'accordion-item');

    const button = el('button', `accordion-button ${index === 0 ? '' : 'collapsed'}`);
    button.type = 'button';
    button.dataset.bsToggle = 'collapse';
    button.dataset.bsTarget = `#srv-${index}`;
    button.append(
        el('span', `me-2 badge ${report.connected ? 'text-bg-success' : 'text-bg-danger'}`, report.connected ? 'connected' : 'error'),
        el('strong', null, report.server),
        el('span', 'ms-2 text-secondary small', report.endpoint));

    const header = el('h2', 'accordion-header');
    header.appendChild(button);

    const collapse = el('div', `accordion-collapse collapse ${index === 0 ? 'show' : ''}`);
    collapse.id = `srv-${index}`;
    collapse.dataset.bsParent = '#servers';

    const body = el('div', 'accordion-body');
    if (!report.connected) {
        body.appendChild(el('div', 'alert alert-danger mb-0', report.error));
    } else {
        body.append(
            summary(report),
            section('Tools', report.tools, t => [t.name, t.description]),
            promptSection(report, showResult),
            resourceSection(report, showResult));
    }

    collapse.appendChild(body);
    item.append(header, collapse);
    return item;
}

function promptSection(report, showResult) {
    return actionSection('Prompts', report.prompts, prompt => [prompt.name, prompt.description], async prompt => {
        const args = {};
        for (const argument of prompt.arguments ?? []) {
            const value = window.prompt(
                `${argument.name}${argument.required ? ' (required)' : ''}${argument.description ? `\n${argument.description}` : ''}`,
                '');
            if (value === null) return;
            if (value || argument.required) args[argument.name] = value;
        }
        const result = await api(`/api/servers/${encodeURIComponent(report.server)}/prompts/get`, {
            method: 'POST',
            body: JSON.stringify({ name: prompt.name, arguments: args })
        });
        showResult(prompt.name, result);
    }, 'Use');
}

function resourceSection(report, showResult) {
    const resources = [...report.resources, ...report.resourceTemplates];
    return actionSection('Resources', resources, resource => [
        resource.uri,
        [resource.name, resource.description].filter(Boolean).join(' — ')
    ], async resource => {
        let uri = resource.uri;
        if (uri.includes('{')) {
            const supplied = window.prompt('Enter the concrete resource URI to read:', uri);
            if (!supplied) return;
            uri = supplied;
        }
        const result = await api(`/api/servers/${encodeURIComponent(report.server)}/resources/read`, {
            method: 'POST',
            body: JSON.stringify({ uri })
        });
        showResult(resource.name || uri, result);
    }, 'Read');
}

function actionSection(title, items, project, action, actionLabel) {
    const fragment = document.createDocumentFragment();
    fragment.appendChild(el('h3', 'h6 mt-3', `${title} (${items.length})`));
    const list = el('div', 'list-group list-group-flush small');
    if (items.length === 0) list.appendChild(el('div', 'list-group-item bg-transparent px-0 text-secondary', 'None'));
    for (const item of items) {
        const [name, description] = project(item);
        const row = el('div', 'list-group-item bg-transparent px-0 d-flex gap-3 align-items-start');
        const details = el('div', 'flex-grow-1 overflow-hidden');
        details.append(el('code', 'text-break', name), el('div', 'text-secondary', description ?? ''));
        const button = el('button', 'btn btn-sm btn-outline-primary flex-shrink-0', actionLabel);
        button.type = 'button';
        button.addEventListener('click', async () => {
            button.disabled = true;
            try { await action(item); }
            catch (error) { showResult(`${actionLabel} failed`, error.message); }
            finally { button.disabled = false; }
        });
        row.append(details, button);
        list.appendChild(row);
    }
    fragment.appendChild(list);
    return fragment;
}

function summary(report) {
    const dl = el('dl', 'row small');
    const add = (term, value) => {
        if (!value) return;
        dl.append(el('dt', 'col-sm-3', term), el('dd', 'col-sm-9', value));
    };
    add('Server', [report.serverName, report.serverVersion].filter(Boolean).join(' '));
    add('Credential forwarded', report.credentialForwarded);
    add('Instructions', report.instructions);
    return dl;
}

function section(title, items, project) {
    const fragment = document.createDocumentFragment();
    fragment.appendChild(el('h3', 'h6 mt-3', `${title} (${items.length})`));

    const ul = el('ul', 'list-group list-group-flush small mb-0');
    if (items.length === 0) {
        ul.appendChild(el('li', 'list-group-item bg-transparent px-0 text-secondary', 'None'));
    }

    for (const item of items) {
        const [name, description] = project(item);
        const li = el('li', 'list-group-item bg-transparent px-0');
        li.append(el('code', null, name), el('div', 'text-secondary', description ?? ''));
        ul.appendChild(li);
    }

    fragment.appendChild(ul);
    return fragment;
}
