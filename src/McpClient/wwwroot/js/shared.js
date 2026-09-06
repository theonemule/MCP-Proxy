const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
};

export function template(id) {
    return document.getElementById(id).content.cloneNode(true);
}

export function signIn() {
    window.location.href = '/account/login?returnUrl=' + encodeURIComponent(window.location.pathname + window.location.hash);
}

export async function api(path, options = {}) {
    const response = await fetch(path, {
        ...options,
        headers: { 'Content-Type': 'application/json', ...(options.headers ?? {}) }
    });

    if (response.status === 401) {
        signIn();
        throw new Error('Not signed in.');
    }

    if (!response.ok) {
        let detail = `Request failed (${response.status})`;
        try {
            const body = await response.json();
            if (body?.detail && body?.error) {
                detail = `${body.error}\n\n${body.detail}`;
            } else if (body?.error) {
                detail = body.error;
            }
        } catch {
            // Response had no JSON body; keep the status-based message.
        }
        throw new Error(detail);
    }

    return response.status === 204 ? null : response.json();
}

export { el };
