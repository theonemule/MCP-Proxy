import { api, el, template } from './shared.js';

const state = {
    conversationId: sessionStorage.getItem('conversationId') || crypto.randomUUID().replace(/-/g, ''),
    messages: [],
    activity: []
};
sessionStorage.setItem('conversationId', state.conversationId);

export function renderChat(view) {
    view.replaceChildren(template('tpl-chat'));

    const messages = view.querySelector('#messages');
    const activity = view.querySelector('#activity');
    const status = view.querySelector('#status');
    const form = view.querySelector('#chat-form');
    const prompt = view.querySelector('#prompt');
    const send = view.querySelector('#send');

    if (state.messages.length === 0) {
        messages.appendChild(el('div', 'text-secondary small',
            'Ask a question. Tools published by the configured MCP servers are called on your behalf using your signed-in credential.'));
    }

    // Replay history so navigating between views does not lose the conversation.
    state.messages.forEach(m => messages.appendChild(bubble(m.role, m.text, m.error)));
    state.activity.forEach(a => activity.appendChild(entry(a)));
    messages.scrollTop = messages.scrollHeight;

    function record(role, text, error) {
        const item = { role, text, error };
        state.messages.push(item);
        const wrapper = bubble(role, text, error);
        messages.appendChild(wrapper);
        messages.scrollTop = messages.scrollHeight;
        return { item, body: wrapper.firstElementChild };
    }

    function log(title, detail, variant = 'secondary') {
        const item = { title, detail, variant };
        state.activity.push(item);
        activity.appendChild(entry(item));
        activity.scrollTop = activity.scrollHeight;
    }

    function logToolCall(server, tool, args, result, isError) {
        const item = {
            title: `${server} → ${tool}`,
            args,
            result,
            variant: isError ? 'danger' : 'info'
        };
        state.activity.push(item);
        activity.appendChild(entry(item));
        activity.scrollTop = activity.scrollHeight;
    }

    function setBusy(busy) {
        send.disabled = busy;
        prompt.disabled = busy;
        status.textContent = busy ? 'working' : 'idle';
        status.className = `badge ${busy ? 'text-bg-warning' : 'text-bg-secondary'}`;
    }

    async function submit(text) {
        record('user', text);
        const pending = record('assistant', '…');
        setBusy(true);

        try {
            const result = await api('/api/chat', {
                method: 'POST',
                body: JSON.stringify({ conversationId: state.conversationId, message: text })
            });

            state.conversationId = result.conversationId;
            sessionStorage.setItem('conversationId', state.conversationId);

            pending.item.text = result.reply || '(no response)';
            pending.body.textContent = pending.item.text;

            if (result.warnings && result.warnings.length > 0) {
                const warnBox = el('div', 'mt-2 pt-2 border-top border-warning text-warning small');
                warnBox.append(el('div', 'fw-semibold mb-1', 'Server Warnings:'));
                for (const warning of result.warnings) {
                    warnBox.append(el('div', 'font-monospace text-wrap', warning));
                    log('Warning', warning, 'warning');
                }
                pending.body.appendChild(warnBox);
            }

            for (const call of result.toolCalls ?? []) {
                logToolCall(call.server, call.tool, call.arguments, call.result, call.isError);
            }
        } catch (error) {
            pending.item.text = error.message;
            pending.item.error = true;
            pending.body.classList.add('border-danger', 'text-danger');
            pending.body.replaceChildren(el('pre', 'mb-0 font-monospace text-wrap small', error.message));
            log('Error', error.message, 'danger');
        } finally {
            setBusy(false);
            prompt.focus();
        }
    }

    form.addEventListener('submit', event => {
        event.preventDefault();
        const text = prompt.value.trim();
        if (!text) return;
        prompt.value = '';
        submit(text);
    });

    prompt.addEventListener('keydown', event => {
        if (event.key === 'Enter' && !event.shiftKey) {
            event.preventDefault();
            form.requestSubmit();
        }
    });

    view.querySelector('#reset').addEventListener('click', async () => {
        await api(`/api/chat/${state.conversationId}/reset`, { method: 'POST' }).catch(() => { });
        state.conversationId = crypto.randomUUID().replace(/-/g, '');
        sessionStorage.setItem('conversationId', state.conversationId);
        state.messages = [];
        state.activity = [];
        renderChat(view);
    });

    prompt.focus();
}

function bubble(role, text, error) {
    const wrapper = el('div', `d-flex mb-3 ${role === 'user' ? 'justify-content-end' : 'justify-content-start'}`);
    const body = el('div', `chat-bubble p-2 px-3 rounded-3 ${role === 'user' ? 'bg-primary text-white' : 'bg-body border'}`);
    if (error) {
        body.classList.add('border-danger', 'text-danger');
        body.appendChild(el('pre', 'mb-0 font-monospace text-wrap small', text));
    } else {
        body.textContent = text;
    }
    wrapper.appendChild(body);
    return wrapper;
}

function prettyFormat(val) {
    if (val === null || val === undefined) return '';
    if (typeof val === 'object') {
        try { return JSON.stringify(val, null, 2); } catch { return String(val); }
    }
    if (typeof val === 'string') {
        const trimmed = val.trim();
        if ((trimmed.startsWith('{') && trimmed.endsWith('}')) || (trimmed.startsWith('[') && trimmed.endsWith(']'))) {
            try {
                return JSON.stringify(JSON.parse(trimmed), null, 2);
            } catch {
                return val;
            }
        }
        return val;
    }
    return String(val);
}

function entry(titleOrObj, detail, variant) {
    let item;
    if (typeof titleOrObj === 'object' && titleOrObj !== null) {
        item = titleOrObj;
    } else {
        item = { title: titleOrObj, detail, variant: variant || 'info' };
    }

    const borderVariant = item.variant || 'info';
    const container = el('div', `card border-0 border-start border-3 border-${borderVariant} bg-body-tertiary mb-3 shadow-sm`);
    const cardBody = el('div', 'card-body p-2');

    const headerRow = el('div', 'd-flex justify-content-between align-items-center mb-1');
    const titleEl = el('span', 'fw-bold small text-body', item.title);
    headerRow.appendChild(titleEl);
    if (item.variant === 'danger') {
        headerRow.appendChild(el('span', 'badge text-bg-danger ms-2', 'ERROR'));
    }
    cardBody.appendChild(headerRow);

    if (item.args !== undefined || item.result !== undefined) {
        if (item.args) {
            const argsLabel = el('div', 'text-uppercase text-secondary fw-semibold mb-1', 'Arguments');
            argsLabel.style.fontSize = '0.7rem';
            const argsPre = el('pre', 'mb-2 font-monospace text-wrap bg-body p-2 rounded border border-secondary-subtle text-body-secondary small', prettyFormat(item.args));
            cardBody.append(argsLabel, argsPre);
        }
        if (item.result) {
            const resLabel = el('div', `text-uppercase ${item.variant === 'danger' ? 'text-danger' : 'text-secondary'} fw-semibold mb-1`, 'Result');
            resLabel.style.fontSize = '0.7rem';
            const resPre = el('pre', `mb-0 font-monospace text-wrap bg-body p-2 rounded border ${item.variant === 'danger' ? 'border-danger-subtle text-danger' : 'border-secondary-subtle text-body-secondary'} small`, prettyFormat(item.result));
            cardBody.append(resLabel, resPre);
        }
    } else if (item.detail) {
        const detailPre = el('pre', 'mb-0 font-monospace text-wrap bg-body p-2 rounded border border-secondary-subtle text-body-secondary small', prettyFormat(item.detail));
        cardBody.appendChild(detailPre);
    }

    container.appendChild(cardBody);
    return container;
}
