import { api, el, template } from './shared.js';

export function renderSettings(view) {
    const tpl = template('tpl-settings');
    view.replaceChildren(tpl);

    const alertBox = view.querySelector('#settings-alert');
    const form = view.querySelector('#settings-form');

    async function load() {
        try {
            const settings = (await api('/api/llm-settings')) || {};
            view.querySelector('#llm-endpoint').value = settings.endpoint ?? '';
            view.querySelector('#llm-model').value = settings.model ?? '';
            view.querySelector('#llm-apikey').value = '';
            view.querySelector('#llm-system-prompt').value = settings.systemPrompt ?? '';
            view.querySelector('#llm-temperature').value = settings.temperature ?? '';
            view.querySelector('#llm-max-output').value = settings.maxOutputTokens ?? '';
            view.querySelector('#llm-max-iterations').value = settings.maxToolIterations ?? 10;
            form.dataset.existingApiKey = settings.apiKey ?? '';
        } catch (error) {
            alertBox.replaceChildren(el('div', 'alert alert-danger', error.message));
        }
    }

    form.addEventListener('submit', async event => {
        event.preventDefault();
        const save = view.querySelector('#save-settings');
        const temperature = view.querySelector('#llm-temperature').value;
        const maxOutput = view.querySelector('#llm-max-output').value;
        const apiKeyInput = view.querySelector('#llm-apikey').value;

        const settings = {
            endpoint: view.querySelector('#llm-endpoint').value,
            model: view.querySelector('#llm-model').value,
            // A blank field keeps whatever key was already saved, so re-saving other fields never erases it.
            apiKey: apiKeyInput || form.dataset.existingApiKey || null,
            systemPrompt: view.querySelector('#llm-system-prompt').value || null,
            temperature: temperature === '' ? null : Number(temperature),
            maxOutputTokens: maxOutput === '' ? null : Number(maxOutput),
            maxToolIterations: Number(view.querySelector('#llm-max-iterations').value)
        };

        save.disabled = true;
        alertBox.replaceChildren();
        try {
            await api('/api/llm-settings', { method: 'PUT', body: JSON.stringify(settings) });
            alertBox.replaceChildren(el('div', 'alert alert-success', 'Settings saved.'));
            await load();
        } catch (error) {
            alertBox.replaceChildren(el('div', 'alert alert-danger', error.message));
        } finally {
            save.disabled = false;
        }
    });

    load();
}
