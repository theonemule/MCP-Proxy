import { el, signIn, template } from './shared.js?v=5';
import { renderChat } from './chat.js?v=5';
import { renderServers } from './servers.js?v=5';
import { renderSettings } from './settings.js?v=5';

const view = document.getElementById('view');
const sessionBar = document.getElementById('session');

const routes = {
    '/chat': renderChat,
    '/servers': renderServers,
    '/settings': renderSettings
};

let session = { authenticated: false, name: null };

async function loadSession() {
    try {
        const response = await fetch('/api/session');
        session = response.ok ? await response.json() : { authenticated: false, name: null };
    } catch {
        session = { authenticated: false, name: null };
    }
    renderSessionBar();
}

function renderSessionBar() {
    sessionBar.replaceChildren();

    if (!session.authenticated) {
        const link = el('a', 'btn btn-sm btn-primary', 'Sign in');
        link.href = '#';
        link.addEventListener('click', event => { event.preventDefault(); signIn(); });
        sessionBar.appendChild(link);
        return;
    }

    const name = el('span', 'navbar-text small text-truncate', session.name ?? 'Signed in');
    name.style.maxWidth = '20rem';

    // A real form post so the browser follows the identity provider's sign-out redirects.
    const form = el('form', 'm-0');
    form.method = 'post';
    form.action = '/account/logout';
    const button = el('button', 'btn btn-sm btn-outline-secondary', 'Sign out');
    button.type = 'submit';
    form.appendChild(button);

    sessionBar.append(name, form);
}

function router() {
    const path = (window.location.hash.replace(/^#/, '') || '/chat').split('?')[0];
    const render = routes[path];

    if (!render) {
        window.location.hash = '#/chat';
        return;
    }

    document.querySelectorAll('[data-route]').forEach(link =>
        link.classList.toggle('active', link.dataset.route === path));

    if (!session.authenticated) {
        view.replaceChildren(template('tpl-signin'));
        view.querySelector('#signin').addEventListener('click', event => { event.preventDefault(); signIn(); });
        return;
    }

    try {
        render(view);
    } catch (error) {
        console.error('Router render error:', error);
        view.replaceChildren(el('div', 'alert alert-danger m-3', `Error rendering view: ${error.message}`));
    }
}

window.addEventListener('hashchange', router);

await loadSession();
router();
