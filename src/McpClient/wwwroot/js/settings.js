import { api, el, template } from './shared.js';

export function renderSettings(view) {
    const tpl = template('tpl-settings');
    view.replaceChildren(tpl);

    const alertBox = view.querySelector('#settings-alert');
    const form = view.querySelector('#settings-form');

    const sourceInputs = [...view.querySelectorAll('input[name="llm-source"]')];
    const sourceValue = () => sourceInputs.find(input => input.checked)?.value ?? 'Hosted';

    function updateActiveState() {
        const source = sourceValue();
        view.querySelector('#llm-proxy-active').classList.toggle('d-none', source !== 'Proxy');
        view.querySelector('#llm-hosted-active').classList.toggle('d-none', source !== 'Hosted');
        view.querySelector('#llm-proxy-card').classList.toggle('border-primary', source === 'Proxy');
        view.querySelector('#llm-hosted-card').classList.toggle('border-primary', source === 'Hosted');

        const endpoint = source === 'Proxy'
            ? view.querySelector('#llm-proxy-endpoint').value
            : view.querySelector('#llm-hosted-endpoint').value;
        const model = source === 'Proxy'
            ? view.querySelector('#llm-proxy-model').value
            : view.querySelector('#llm-hosted-model').value;

        view.querySelector('#llm-active-summary').textContent =
            source + ': ' + (model || '(model not selected)') + ' @ ' + (endpoint || '(endpoint not set)');
    }

    function connectionSettings(kind) {
        const prefix = kind.toLowerCase();
        const apiKeyInput = view.querySelector('#llm-' + prefix + '-apikey').value;
        return {
            endpoint: view.querySelector('#llm-' + prefix + '-endpoint').value,
            model: view.querySelector('#llm-' + prefix + '-model').value,
            apiKey: apiKeyInput || ''
        };
    }

    function buildSettings() {
        const temperature = view.querySelector('#llm-temperature').value;
        const maxOutput = view.querySelector('#llm-max-output').value;

        return {
            source: sourceValue(),
            proxy: connectionSettings('Proxy'),
            hosted: connectionSettings('Hosted'),
            systemPrompt: view.querySelector('#llm-system-prompt').value || null,
            temperature: temperature === '' ? null : Number(temperature),
            maxOutputTokens: maxOutput === '' ? null : Number(maxOutput),
            maxToolIterations: Number(view.querySelector('#llm-max-iterations').value)
        };
    }

    async function load() {
        try {
            const settings = (await api('/api/llm-settings')) || {};
            const proxy = settings.proxy ?? {};
            const hosted = settings.hosted ?? {};

            const source = settings.source ?? 'Hosted';
            const sourceInput = view.querySelector('#llm-source-' + source.toLowerCase());
            if (sourceInput) sourceInput.checked = true;

            view.querySelector('#llm-proxy-endpoint').value = proxy.endpoint ?? 'http://localhost:5105/v1';
            view.querySelector('#llm-proxy-model').value = proxy.model ?? '';
            view.querySelector('#llm-proxy-apikey').value = '';
            view.querySelector('#llm-proxy-apikey').placeholder = proxy.apiKeyConfigured
                ? 'Configured - leave blank to keep'
                : 'Blank uses your signed-in access token';

            view.querySelector('#llm-hosted-endpoint').value = hosted.endpoint ?? '';
            view.querySelector('#llm-hosted-model').value = hosted.model ?? '';
            view.querySelector('#llm-hosted-apikey').value = '';
            view.querySelector('#llm-hosted-apikey').placeholder = hosted.apiKeyConfigured
                ? 'Configured - leave blank to keep'
                : 'Enter hosted API key';

            view.querySelector('#llm-system-prompt').value = settings.systemPrompt ?? '';
            view.querySelector('#llm-temperature').value = settings.temperature ?? '';
            view.querySelector('#llm-max-output').value = settings.maxOutputTokens ?? '';
            view.querySelector('#llm-max-iterations').value = settings.maxToolIterations ?? 10;
            updateActiveState();
        } catch (error) {
            alertBox.replaceChildren(el('div', 'alert alert-danger', error.message));
        }
    }

    async function loadModels(kind) {
        const prefix = kind.toLowerCase();
        const button = view.querySelector('#load-' + prefix + '-models');
        const connection = connectionSettings(kind);

        button.disabled = true;
        alertBox.replaceChildren();

        try {
            const result = await api('/api/llm-models', {
                method: 'POST',
                body: JSON.stringify({
                    source: kind,
                    endpoint: connection.endpoint,
                    apiKey: view.querySelector('#llm-' + prefix + '-apikey').value || null
                })
            });

            const datalist = view.querySelector('#llm-' + prefix + '-model-list');
            datalist.replaceChildren(...(result.models ?? []).map(model => {
                const option = document.createElement('option');
                option.value = model;
                return option;
            }));

            const auth = result.authMode === 'signed-in-access-token'
                ? 'signed-in access token'
                : 'API key';
            const count = result.models?.length ?? 0;
            alertBox.replaceChildren(el(
                'div',
                'alert alert-success',
                kind + ' connection succeeded using ' + auth + '. ' + count + ' model' + (count === 1 ? '' : 's') + ' returned.'
            ));

            if (!view.querySelector('#llm-' + prefix + '-model').value && count === 1) {
                view.querySelector('#llm-' + prefix + '-model').value = result.models[0];
            }
            updateActiveState();
        } catch (error) {
            alertBox.replaceChildren(el('div', 'alert alert-danger', error.message));
        } finally {
            button.disabled = false;
        }
    }

    sourceInputs.forEach(input => input.addEventListener('change', updateActiveState));
    [
        '#llm-proxy-endpoint',
        '#llm-proxy-model',
        '#llm-hosted-endpoint',
        '#llm-hosted-model'
    ].forEach(selector => view.querySelector(selector).addEventListener('input', updateActiveState));

    view.querySelector('#load-proxy-models').addEventListener('click', () => loadModels('Proxy'));
    view.querySelector('#load-hosted-models').addEventListener('click', () => loadModels('Hosted'));

    form.addEventListener('submit', async event => {
        event.preventDefault();
        const save = view.querySelector('#save-settings');
        save.disabled = true;
        alertBox.replaceChildren();

        try {
            await api('/api/llm-settings', {
                method: 'PUT',
                body: JSON.stringify(buildSettings())
            });
            alertBox.replaceChildren(el(
                'div',
                'alert alert-success',
                sourceValue() + ' model selected. New chat turns will use this profile.'
            ));
            await load();
        } catch (error) {
            alertBox.replaceChildren(el('div', 'alert alert-danger', error.message));
        } finally {
            save.disabled = false;
        }
    });

    load();
}
