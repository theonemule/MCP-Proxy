"use strict";

const state = {
  token: sessionStorage.getItem("mcpproxy_token") || null,
  username: sessionStorage.getItem("mcpproxy_username") || null,
  roles: [],
  servers: [],
  modelProviders: [],
  modelRoutes: [],
  modelPermissions: [],
  users: [],
  apiKeys: [],
  mappings: [],
  permissions: [],
  catalogCache: {},
  serverCatalogExpanded: new Set(),
  // Permissions tab: a role's grants are edited as a bulk checkbox tree, not one at a time.
  permTree: { roleId: "", expanded: new Set() }
};

function el(id) { return document.getElementById(id); }

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
  }[c]));
}

function showToast(message, isError) {
  const area = el("toast-area");
  const toast = document.createElement("div");
  toast.className = `toast align-items-center text-bg-${isError ? "danger" : "success"} border-0 show mb-2`;
  toast.style.pointerEvents = "auto";
  toast.innerHTML = `<div class="d-flex"><div class="toast-body">${escapeHtml(message)}</div>
    <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button></div>`;
  area.appendChild(toast);
  setTimeout(() => toast.remove(), 5000);
}

async function api(method, path, body) {
  const headers = { "Content-Type": "application/json" };
  if (state.token) {
    headers["Authorization"] = "Bearer " + state.token;
  }
  const res = await fetch(path, { method, headers, body: body !== undefined ? JSON.stringify(body) : undefined });
  if (res.status === 401) {
    signOut();
    throw new Error("Session expired. Please sign in again.");
  }
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    let message = text;
    try {
      const parsed = JSON.parse(text);
      message = parsed.error || parsed.title || text;
    } catch { /* not JSON - use raw text */ }
    throw new Error(message || `${method} ${path} failed with ${res.status}`);
  }
  if (res.status === 204) {
    return null;
  }
  return res.json();
}

function signOut() {
  state.token = null;
  state.username = null;
  sessionStorage.removeItem("mcpproxy_token");
  sessionStorage.removeItem("mcpproxy_username");
  fetch("/account/logout", { method: "POST" }).catch(() => {});
  el("app-view").classList.remove("visible");
  el("login-view").style.display = "flex";
}

function switchTab(name) {
  document.querySelectorAll("#main-tabs a").forEach((a) => a.classList.toggle("active", a.dataset.tab === name));
  document.querySelectorAll(".tab-pane").forEach((pane) => pane.classList.toggle("active", pane.id === `tab-${name}`));
}

function openModal(title, bodyHtml) {
  el("generic-modal-title").innerHTML = title;
  el("generic-modal-body").innerHTML = bodyHtml;
  bootstrap.Modal.getOrCreateInstance(el("generic-modal")).show();
}

function closeModal() {
  bootstrap.Modal.getInstance(el("generic-modal"))?.hide();
}

function pageHeader(icon, title, actionHtml) {
  return `<div class="ph"><h5><i class="bi ${icon} me-2"></i>${escapeHtml(title)}</h5>${actionHtml || ""}</div>`;
}

function emptyState(icon, message) {
  return `<div class="empty-state"><i class="bi ${icon}"></i>${escapeHtml(message)}</div>`;
}

// ---- data loading ----

async function loadAll() {
  const [roles, servers, modelProviders, modelRoutes, modelPermissions, users, apiKeys, mappings, permissions] = await Promise.all([
    api("GET", "/admin/roles"),
    api("GET", "/admin/servers"),
    api("GET", "/admin/model-providers"),
    api("GET", "/admin/model-routes"),
    api("GET", "/admin/model-permissions"),
    api("GET", "/admin/users"),
    api("GET", "/admin/apikeys"),
    api("GET", "/admin/claim-mappings"),
    api("GET", "/admin/permissions")
  ]);
  state.roles = roles;
  state.servers = servers;
  state.modelProviders = modelProviders;
  state.modelRoutes = modelRoutes;
  state.modelPermissions = modelPermissions;
  state.users = users;
  state.apiKeys = apiKeys;
  state.mappings = mappings;
  state.permissions = permissions;
  renderAll();
}

async function fetchCatalog(serverId) {
  if (!serverId) {
    return { tools: [], resources: [], prompts: [] };
  }
  if (!state.catalogCache[serverId]) {
    state.catalogCache[serverId] = await api("GET", `/admin/servers/${serverId}/catalog`);
  }
  return state.catalogCache[serverId];
}

function renderAll() {
  renderServers();
  renderModels();
  renderUsers();
  renderRoles();
  renderApiKeys();
  renderPermissions();
}

// ---- Servers ----

const KIND_NAMES = { 1: "Tools", 2: "Resources", 3: "Prompts" };
const KIND_CATALOG_KEY = { 1: "tools", 2: "resources", 3: "prompts" };

function renderServers() {
  const rowsHtml = state.servers.map((s) => renderServerRow(s)).join("");

  el("tab-servers").innerHTML =
    pageHeader("bi-hdd-network", "Servers", '<button class="btn btn-sm btn-primary" data-action="open-add-server"><i class="bi bi-plus-lg me-1"></i>Register Server</button>') +
    (state.servers.length ? `
      <table class="table table-sm align-middle bg-white">
        <thead><tr><th></th><th>Name</th><th>Namespace</th><th>Proxy endpoint</th><th>Status</th><th>Last sync</th><th></th></tr></thead>
        <tbody>${rowsHtml}</tbody>
      </table>` : emptyState("bi-hdd-network", "No servers registered yet."));

  state.serverCatalogExpanded.forEach((id) => populateServerCatalogRow(id));
}

function renderServerRow(s) {
  const expanded = state.serverCatalogExpanded.has(s.id);
  return `
    <tr>
      <td><button type="button" class="btn btn-sm btn-link text-decoration-none px-2" data-action="toggle-server-catalog" data-id="${s.id}">
        <i class="bi ${expanded ? "bi-chevron-down" : "bi-chevron-right"}"></i></button></td>
      <td class="fw-semibold">${escapeHtml(s.name)}</td>
      <td><code>${escapeHtml(s.namespacePrefix)}</code></td>
      <td class="text-break"><code>${escapeHtml(location.origin)}/servers/${escapeHtml(s.namespacePrefix)}/mcp</code></td>
      <td>${s.enabled ? '<span class="badge text-bg-success">Enabled</span>' : '<span class="badge text-bg-secondary">Disabled</span>'}</td>
      <td class="small">${s.lastSyncedAt ? new Date(s.lastSyncedAt).toLocaleString() : "never"}${s.lastError ? `<br><span class="text-danger">${escapeHtml(s.lastError)}</span>` : ""}</td>
      <td class="text-nowrap">
        <button class="btn btn-sm btn-outline-secondary ab" data-action="sync-server" data-id="${s.id}" title="Sync catalog"><i class="bi bi-arrow-repeat"></i></button>
        <button class="btn btn-sm btn-outline-secondary ab" data-action="open-edit-server" data-id="${s.id}" title="Edit"><i class="bi bi-pencil"></i></button>
        <button class="btn btn-sm btn-outline-primary ab" data-action="manage-server-permissions" data-id="${s.id}" title="Permissions"><i class="bi bi-shield-check"></i></button>
        <button class="btn btn-sm btn-outline-danger ab" data-action="delete-server" data-id="${s.id}" title="Delete"><i class="bi bi-trash"></i></button>
      </td>
    </tr>
    <tr id="catalog-row-${s.id}" class="${expanded ? "" : "d-none"}">
      <td></td>
      <td colspan="6" class="bg-light"><div id="catalog-body-${s.id}" class="py-2">${expanded ? '<span class="text-muted small">Loading...</span>' : ""}</div></td>
    </tr>`;
}

async function populateServerCatalogRow(serverId) {
  const container = el(`catalog-body-${serverId}`);
  if (!container) return;
  const catalog = await fetchCatalog(serverId);

  const section = (kind, items, labelOf) => {
    if (!items.length) return "";
    return `
      <div class="mb-2">
        <div class="fw-semibold small text-muted">${KIND_NAMES[kind]}</div>
        <div class="list-group">
          ${items.map((item, index) => `
            <button type="button" class="list-group-item list-group-item-action text-break"
              data-action="view-primitive" data-id="${serverId}" data-kind="${kind}" data-index="${index}">
              ${escapeHtml(labelOf(item))}
            </button>`).join("")}
        </div>
      </div>`;
  };

  const html = section(1, catalog.tools, (t) => t.name) + section(2, catalog.resources, (r) => r.uri) + section(3, catalog.prompts, (p) => p.name);
  container.innerHTML = html || '<p class="text-muted small mb-0">No cached catalog - try Sync.</p>';
}

function primitiveMetadataHtml(kind, item) {
  const rows = [];
  const blocks = [];
  if (kind === 1) {
    rows.push(["Name", `<code>${escapeHtml(item.name)}</code>`]);
    if (item.title) rows.push(["Title", escapeHtml(item.title)]);
    if (item.description) rows.push(["Description", escapeHtml(item.description)]);
    if (item.annotations) {
      const badges = Object.entries(item.annotations)
        .filter(([, v]) => typeof v === "boolean")
        .map(([k, v]) => `<span class="badge text-bg-${v ? "info" : "secondary"} badge-scope">${escapeHtml(k)}: ${v}</span>`).join(" ");
      if (badges) rows.push(["Annotations", badges]);
    }
    if (item.inputSchema) blocks.push(["Input schema", item.inputSchema]);
    if (item.outputSchema) blocks.push(["Output schema", item.outputSchema]);
  } else if (kind === 2) {
    rows.push(["URI", `<code>${escapeHtml(item.uri)}</code>`]);
    if (item.name) rows.push(["Name", escapeHtml(item.name)]);
    if (item.description) rows.push(["Description", escapeHtml(item.description)]);
    if (item.mimeType) rows.push(["MIME type", `<code>${escapeHtml(item.mimeType)}</code>`]);
  } else {
    rows.push(["Name", `<code>${escapeHtml(item.name)}</code>`]);
    if (item.description) rows.push(["Description", escapeHtml(item.description)]);
    if (item.arguments?.length) {
      rows.push(["Arguments", `<ul class="mb-0">${item.arguments.map((a) =>
        `<li><code>${escapeHtml(a.name)}</code>${a.required ? ' <span class="badge text-bg-warning">required</span>' : ""}${a.description ? " - " + escapeHtml(a.description) : ""}</li>`).join("")}</ul>`]);
    }
  }

  const tableHtml = `<table class="table table-sm metadata-table"><tbody>${rows.map(([label, value]) =>
    `<tr><th class="text-muted small">${escapeHtml(label)}</th><td>${value}</td></tr>`).join("")}</tbody></table>`;

  // Schemas render outside the table entirely so their own scrollbar can never widen the table.
  const blocksHtml = blocks.map(([label, schema]) => `
    <div class="mb-3">
      <div class="text-muted small mb-1">${escapeHtml(label)}</div>
      <pre class="code-block">${escapeHtml(JSON.stringify(schema, null, 2))}</pre>
    </div>`).join("");

  return tableHtml + blocksHtml;
}


function addServerModalHtml(server) {
  return `
    <form id="server-form">
      <div class="row g-3">
        <div class="col-md-6"><label class="form-label">Name <span class="text-danger">*</span></label>
          <input class="form-control" name="name" value="${escapeHtml(server?.name || "")}" placeholder="github-tools" required></div>
        <div class="col-md-6"><label class="form-label">Namespace prefix <span class="text-danger">*</span></label>
          <input class="form-control" name="namespacePrefix" value="${escapeHtml(server?.namespacePrefix || "")}" placeholder="gh" required>
          <div class="form-text">Tool names become: gh__create_issue</div></div>
        <div class="col-12"><label class="form-label">Source endpoint URL <span class="text-danger">*</span></label>
          <input type="url" class="form-control" name="endpoint" value="${escapeHtml(server?.endpoint || "")}" placeholder="https://backend.example/mcp" required>
          <div class="form-text">The downstream server's own MCP endpoint - not the proxy's endpoint for it.</div></div>
        <div class="col-12"><hr class="my-1"><p class="text-muted small mb-1">Static credential <span class="text-muted">(optional)</span></p></div>
        <div class="col-md-6"><label class="form-label">Credential reference</label>
          <input class="form-control" name="credentialReference" value="${escapeHtml(server?.credentialReference || "")}" placeholder="env:MY_SECRET_VAR"></div>
        <div class="col-md-4"><label class="form-label">Header name</label>
          <input class="form-control" name="credentialHeader" value="${escapeHtml(server?.credentialHeader || "Authorization")}" placeholder="Authorization"></div>
        <div class="col-md-2"><label class="form-label">Prefix</label>
          <input class="form-control" name="credentialPrefix" value="${escapeHtml(server?.credentialPrefix ?? "Bearer ")}" placeholder="Bearer "></div>
      </div>
      <div class="mt-4 text-end">
        <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button>
        <button type="submit" class="btn btn-primary">${server ? "Save" : "Register"}</button>
      </div>
    </form>`;
}


// ---- Models ----

const MODEL_PROVIDER_KIND = {
  0: "OpenAI compatible",
  1: "Ollama",
  2: "AWS Bedrock",
  3: "Generic HTTP"
};

function renderModels() {
  const providerRows = state.modelProviders.map((p) => `
    <tr>
      <td class="fw-semibold">${escapeHtml(p.name)}</td>
      <td><code>${escapeHtml(p.slug)}</code></td>
      <td>${escapeHtml(MODEL_PROVIDER_KIND[p.kind] || p.kind)}</td>
      <td class="text-break small">${p.baseEndpoint ? `<code>${escapeHtml(p.baseEndpoint)}</code>` : '<span class="text-muted">regional default</span>'}</td>
      <td><code>${escapeHtml(location.origin)}/models/native/${escapeHtml(p.slug)}/...</code></td>
      <td>${p.enabled ? '<span class="badge text-bg-success">Enabled</span>' : '<span class="badge text-bg-secondary">Disabled</span>'}</td>
      <td class="text-nowrap">
        <button class="btn btn-sm btn-outline-secondary ab" data-action="open-edit-model-provider" data-id="${p.id}" title="Edit"><i class="bi bi-pencil"></i></button>
        <button class="btn btn-sm btn-outline-danger ab" data-action="delete-model-provider" data-id="${p.id}" title="Delete"><i class="bi bi-trash"></i></button>
      </td>
    </tr>`).join("");

  const routeRows = state.modelRoutes.map((r) => `
    <tr>
      <td class="fw-semibold"><code>${escapeHtml(r.publicName)}</code></td>
      <td>${escapeHtml(r.providerName)}</td>
      <td><code>${escapeHtml(r.downstreamModel)}</code></td>
      <td>${r.enabled ? '<span class="badge text-bg-success">Enabled</span>' : '<span class="badge text-bg-secondary">Disabled</span>'}</td>
      <td class="text-nowrap">
        <button class="btn btn-sm btn-outline-secondary ab" data-action="open-edit-model-route" data-id="${r.id}" title="Edit"><i class="bi bi-pencil"></i></button>
        <button class="btn btn-sm btn-outline-danger ab" data-action="delete-model-route" data-id="${r.id}" title="Delete"><i class="bi bi-trash"></i></button>
      </td>
    </tr>`).join("");

  const grantRows = state.modelPermissions.map((g) => `
    <tr>
      <td>${escapeHtml(g.roleName)}</td>
      <td>${g.scope === 0 ? '<span class="badge text-bg-primary">Provider</span>' : '<span class="badge text-bg-info">Model</span>'}</td>
      <td>${escapeHtml(g.scope === 0 ? g.providerName : g.modelName)}</td>
      <td><button class="btn btn-sm btn-outline-danger ab" data-action="delete-model-permission" data-id="${g.id}" title="Revoke"><i class="bi bi-trash"></i></button></td>
    </tr>`).join("");

  el("tab-models").innerHTML =
    pageHeader("bi-cpu", "Model Router",
      '<div class="d-flex gap-2"><button class="btn btn-sm btn-outline-primary" data-action="open-add-model-route"><i class="bi bi-signpost me-1"></i>Add Model Route</button><button class="btn btn-sm btn-primary" data-action="open-add-model-provider"><i class="bi bi-plus-lg me-1"></i>Add Provider</button></div>') +
    `<p class="text-muted small">Unified API: <code>POST ${escapeHtml(location.origin)}/models/chat</code>. Set <code>stream: true</code> for normalized SSE, or use <code>/models/chat/stream</code>. Native provider APIs are exposed under <code>/models/native/{provider}/...</code> with the provider-specific path, method, query string, and streaming response preserved.</p>
    <h6 class="mt-3">Providers</h6>` +
    (state.modelProviders.length ? `
      <table class="table table-sm align-middle bg-white">
        <thead><tr><th>Name</th><th>Slug</th><th>Type</th><th>Base endpoint</th><th>Native proxy</th><th>Status</th><th></th></tr></thead>
        <tbody>${providerRows}</tbody>
      </table>` : emptyState("bi-cpu", "No model providers registered yet.")) +
    `<h6 class="mt-4">Public model routes</h6>` +
    (state.modelRoutes.length ? `
      <table class="table table-sm align-middle bg-white">
        <thead><tr><th>Public model</th><th>Provider</th><th>Downstream model</th><th>Status</th><th></th></tr></thead>
        <tbody>${routeRows}</tbody>
      </table>` : emptyState("bi-signpost", "No model routes registered yet.")) +
    `<div class="d-flex align-items-center justify-content-between mt-4 mb-2">
       <h6 class="mb-0">Model access grants</h6>
       <button class="btn btn-sm btn-outline-primary" data-action="open-add-model-permission"><i class="bi bi-shield-plus me-1"></i>Add Grant</button>
     </div>
     <p class="text-muted small">Provider grants allow native proxy access and every model route on that provider. Model grants allow only the selected alias through the unified API.</p>` +
    (state.modelPermissions.length ? `
      <table class="table table-sm align-middle bg-white">
        <thead><tr><th>Role</th><th>Scope</th><th>Target</th><th></th></tr></thead>
        <tbody>${grantRows}</tbody>
      </table>` : emptyState("bi-shield-check", "No model access grants yet."));
}

function modelProviderModalHtml(provider) {
  const kindOptions = Object.entries(MODEL_PROVIDER_KIND).map(([value, label]) =>
    `<option value="${value}" ${Number(value) === provider?.kind ? "selected" : ""}>${escapeHtml(label)}</option>`).join("");

  return `
    <form id="model-provider-form">
      <div class="row g-3">
        <div class="col-md-6"><label class="form-label">Name <span class="text-danger">*</span></label>
          <input class="form-control" name="name" value="${escapeHtml(provider?.name || "")}" required></div>
        <div class="col-md-3"><label class="form-label">Slug <span class="text-danger">*</span></label>
          <input class="form-control" name="slug" value="${escapeHtml(provider?.slug || "")}" placeholder="foundry-prod" required></div>
        <div class="col-md-3"><label class="form-label">Type</label>
          <select class="form-select" name="kind">${kindOptions}</select></div>

        <div class="col-md-8"><label class="form-label">Base endpoint</label>
          <input class="form-control" name="baseEndpoint" value="${escapeHtml(provider?.baseEndpoint || "")}" placeholder="https://host.example"></div>
        <div class="col-md-4"><label class="form-label">Unified chat path</label>
          <input class="form-control" name="chatPath" value="${escapeHtml(provider?.chatPath || "")}" placeholder="/v1/chat/completions"></div>

        <div class="col-12"><hr class="my-1"><div class="text-muted small">Static HTTP credential. For Bedrock this can be a bearer API key; leave it blank to use SigV4</div></div>
        <div class="col-md-4"><label class="form-label">Secret reference</label>
          <input class="form-control" name="credentialReference" value="${escapeHtml(provider?.credentialReference || "")}" placeholder="env:MODEL_API_KEY"></div>
        <div class="col-md-4"><label class="form-label">Header</label>
          <input class="form-control" name="credentialHeader" value="${escapeHtml(provider?.credentialHeader || "Authorization")}"></div>
        <div class="col-md-4"><label class="form-label">Prefix</label>
          <input class="form-control" name="credentialPrefix" value="${escapeHtml(provider?.credentialPrefix ?? "Bearer ")}" placeholder="Bearer "></div>

        <div class="col-12"><hr class="my-1"><div class="text-muted small">AWS Bedrock. Explicit environment references are optional when standard AWS environment variables are set.</div></div>
        <div class="col-md-3"><label class="form-label">Region</label>
          <input class="form-control" name="awsRegion" value="${escapeHtml(provider?.awsRegion || "")}" placeholder="us-east-1"></div>
        <div class="col-md-3"><label class="form-label">Access key ref</label>
          <input class="form-control" name="awsAccessKeyReference" value="${escapeHtml(provider?.awsAccessKeyReference || "")}" placeholder="env:AWS_ACCESS_KEY_ID"></div>
        <div class="col-md-3"><label class="form-label">Secret key ref</label>
          <input class="form-control" name="awsSecretKeyReference" value="${escapeHtml(provider?.awsSecretKeyReference || "")}" placeholder="env:AWS_SECRET_ACCESS_KEY"></div>
        <div class="col-md-3"><label class="form-label">Session token ref</label>
          <input class="form-control" name="awsSessionTokenReference" value="${escapeHtml(provider?.awsSessionTokenReference || "")}" placeholder="env:AWS_SESSION_TOKEN"></div>

        <div class="col-12"><div class="form-check">
          <input class="form-check-input" type="checkbox" name="enabled" id="model-provider-enabled" ${provider?.enabled !== false ? "checked" : ""}>
          <label class="form-check-label" for="model-provider-enabled">Enabled</label>
        </div></div>
      </div>
      <div class="mt-4 text-end">
        <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button>
        <button type="submit" class="btn btn-primary">${provider ? "Save" : "Add Provider"}</button>
      </div>
    </form>`;
}

function modelRouteModalHtml(route) {
  const providers = state.modelProviders.map((p) =>
    `<option value="${p.id}" ${route?.providerId === p.id ? "selected" : ""}>${escapeHtml(p.name)} (${escapeHtml(p.slug)})</option>`).join("");
  return `
    <form id="model-route-form">
      <div class="row g-3">
        <div class="col-md-5"><label class="form-label">Provider <span class="text-danger">*</span></label>
          <select class="form-select" name="providerId" required><option value="">Choose...</option>${providers}</select></div>
        <div class="col-md-3"><label class="form-label">Public model name <span class="text-danger">*</span></label>
          <input class="form-control" name="publicName" value="${escapeHtml(route?.publicName || "")}" placeholder="fast-coder" required></div>
        <div class="col-md-4"><label class="form-label">Downstream model ID <span class="text-danger">*</span></label>
          <input class="form-control" name="downstreamModel" value="${escapeHtml(route?.downstreamModel || "")}" required></div>
        <div class="col-12"><div class="form-check">
          <input class="form-check-input" type="checkbox" name="enabled" id="model-route-enabled" ${route?.enabled !== false ? "checked" : ""}>
          <label class="form-check-label" for="model-route-enabled">Enabled</label>
        </div></div>
      </div>
      <div class="mt-4 text-end">
        <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button>
        <button type="submit" class="btn btn-primary">${route ? "Save" : "Add Route"}</button>
      </div>
    </form>`;
}

function modelPermissionModalHtml() {
  const roles = state.roles.map((r) => `<option value="${r.id}">${escapeHtml(r.name)}</option>`).join("");
  const providers = state.modelProviders.map((p) => `<option value="${p.id}">${escapeHtml(p.name)}</option>`).join("");
  const routes = state.modelRoutes.map((r) => `<option value="${r.id}">${escapeHtml(r.publicName)} - ${escapeHtml(r.providerName)}</option>`).join("");

  return `
    <form id="model-permission-form">
      <div class="row g-3">
        <div class="col-md-4"><label class="form-label">Role</label>
          <select class="form-select" name="roleId" required><option value="">Choose...</option>${roles}</select></div>
        <div class="col-md-4"><label class="form-label">Scope</label>
          <select class="form-select" name="scope" id="model-permission-scope">
            <option value="1">Model route</option>
            <option value="0">Entire provider + native API</option>
          </select></div>
        <div class="col-md-4" id="model-permission-route-wrap"><label class="form-label">Model route</label>
          <select class="form-select" name="modelRouteId"><option value="">Choose...</option>${routes}</select></div>
        <div class="col-md-4 d-none" id="model-permission-provider-wrap"><label class="form-label">Provider</label>
          <select class="form-select" name="providerId"><option value="">Choose...</option>${providers}</select></div>
      </div>
      <div class="mt-4 text-end">
        <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button>
        <button type="submit" class="btn btn-primary">Grant Access</button>
      </div>
    </form>`;
}

function modelProviderPayload(form) {
  return {
    name: form.get("name"),
    slug: form.get("slug"),
    kind: Number(form.get("kind")),
    baseEndpoint: form.get("baseEndpoint") || null,
    chatPath: form.get("chatPath") || null,
    enabled: form.get("enabled") === "on",
    credentialReference: form.get("credentialReference") || null,
    credentialHeader: form.get("credentialHeader") || "Authorization",
    credentialPrefix: form.get("credentialPrefix") ?? "Bearer ",
    awsRegion: form.get("awsRegion") || null,
    awsAccessKeyReference: form.get("awsAccessKeyReference") || null,
    awsSecretKeyReference: form.get("awsSecretKeyReference") || null,
    awsSessionTokenReference: form.get("awsSessionTokenReference") || null
  };
}


// ---- Users ----

function renderUsers() {
  const rows = state.users.map((u) => `
    <tr>
      <td class="fw-semibold">${escapeHtml(u.username)}</td>
      <td>${u.enabled ? '<span class="badge text-bg-success">Enabled</span>' : '<span class="badge text-bg-secondary">Disabled</span>'}</td>
      <td class="small text-break">${escapeHtml(u.externalSubject || "\u2014")}</td>
      <td>${u.roles.map((r) => `<span class="badge text-bg-info badge-scope">${escapeHtml(r.roleName)}</span>`).join(" ") || "\u2014"}</td>
      <td class="text-nowrap">
        <button class="btn btn-sm btn-outline-secondary ab" data-action="manage-user-roles" data-id="${u.id}" title="Manage roles"><i class="bi bi-person-gear"></i></button>
        <button class="btn btn-sm btn-outline-danger ab" data-action="delete-user" data-id="${u.id}" title="Delete"><i class="bi bi-trash"></i></button>
      </td>
    </tr>`).join("");

  el("tab-users").innerHTML =
    pageHeader("bi-people", "Users", '<button class="btn btn-sm btn-primary" data-action="open-add-user"><i class="bi bi-plus-lg me-1"></i>Add User</button>') +
    (state.users.length ? `
      <table class="table table-sm align-middle bg-white">
        <thead><tr><th>Username</th><th>Status</th><th>External subject</th><th>Roles</th><th></th></tr></thead>
        <tbody>${rows}</tbody>
      </table>` : emptyState("bi-people", "No users yet."));
}

function addUserModalHtml() {
  return `
    <p class="text-muted small">Provide a password for an internal login, an external subject (the OIDC "sub" claim) to link an external identity, or both.</p>
    <form id="user-form">
      <div class="row g-3">
        <div class="col-md-4"><label class="form-label">Username <span class="text-danger">*</span></label>
          <input class="form-control" name="username" required></div>
        <div class="col-md-4"><label class="form-label">Password</label>
          <input class="form-control" name="password" type="password" placeholder="Internal login"></div>
        <div class="col-md-4"><label class="form-label">External subject</label>
          <input class="form-control" name="externalSubject" placeholder="OIDC sub"></div>
      </div>
      <div class="mt-4 text-end">
        <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button>
        <button type="submit" class="btn btn-primary">Add</button>
      </div>
    </form>`;
}

// ---- Roles ----

function renderRoles() {
  const rows = state.roles.map((r) => `
    <tr>
      <td class="fw-semibold">${escapeHtml(r.name)}</td>
      <td class="small">${escapeHtml(r.description || "")}</td>
      <td>
        ${r.isGlobalAdmin ? '<span class="badge text-bg-danger badge-scope">Global Admin</span>' : ""}
        ${r.isUserAdmin ? '<span class="badge text-bg-warning badge-scope">User Admin</span>' : ""}
        ${r.isServerAdmin ? '<span class="badge text-bg-warning badge-scope">MCP Admin</span>' : ""}
      </td>
      <td class="text-nowrap">
        <button class="btn btn-sm btn-outline-secondary ab" data-action="open-edit-role" data-id="${r.id}" title="Edit"><i class="bi bi-pencil"></i></button>
        <button class="btn btn-sm btn-outline-primary ab" data-action="manage-role-permissions" data-id="${r.id}" title="Permissions"><i class="bi bi-shield-check"></i></button>
        <button class="btn btn-sm btn-outline-danger ab" data-action="delete-role" data-id="${r.id}" title="Delete"><i class="bi bi-trash"></i></button>
      </td>
    </tr>`).join("");

  el("tab-roles").innerHTML =
    pageHeader("bi-person-badge", "Roles", '<button class="btn btn-sm btn-primary" data-action="open-add-role"><i class="bi bi-plus-lg me-1"></i>Add Role</button>') +
    (state.roles.length ? `
      <table class="table table-sm align-middle bg-white">
        <thead><tr><th>Name</th><th>Description</th><th>Admin scope</th><th></th></tr></thead>
        <tbody>${rows}</tbody>
      </table>` : emptyState("bi-person-badge", "No roles yet.")) +
    `<button class="btn btn-sm btn-outline-secondary mt-2" data-action="open-claim-mappings">
      <i class="bi bi-diagram-3 me-1"></i>Advanced: claim-based role mapping (OIDC)</button>`;
}

function addRoleModalHtml(role) {
  return `
    <form id="role-form">
      <div class="row g-3">
        <div class="col-md-5"><label class="form-label">Name <span class="text-danger">*</span></label>
          <input class="form-control" name="name" value="${escapeHtml(role?.name || "")}" required></div>
        <div class="col-md-7"><label class="form-label">Description</label>
          <input class="form-control" name="description" value="${escapeHtml(role?.description || "")}"></div>
        <div class="col-12"><hr class="my-1"><p class="text-muted small mb-1">Admin scope <span class="text-muted">(optional; independent of resource permissions below)</span></p>
          <div class="d-flex gap-4">
            <div class="form-check"><input class="form-check-input" type="checkbox" name="isGlobalAdmin" id="chk-global" ${role?.isGlobalAdmin ? "checked" : ""}><label class="form-check-label" for="chk-global">Global Admin</label></div>
            <div class="form-check"><input class="form-check-input" type="checkbox" name="isUserAdmin" id="chk-user" ${role?.isUserAdmin ? "checked" : ""}><label class="form-check-label" for="chk-user">User Admin</label></div>
            <div class="form-check"><input class="form-check-input" type="checkbox" name="isServerAdmin" id="chk-server" ${role?.isServerAdmin ? "checked" : ""}><label class="form-check-label" for="chk-server">MCP Admin</label></div>
          </div>
        </div>
      </div>
      <div class="mt-4 text-end">
        <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button>
        <button type="submit" class="btn btn-primary">${role ? "Save" : "Add"}</button>
      </div>
    </form>`;
}

function claimMappingsModalHtml() {
  const roleOptions = state.roles.map((r) => `<option value="${r.id}">${escapeHtml(r.name)}</option>`).join("");
  const rows = state.mappings.map((m) => `
    <tr>
      <td>${escapeHtml(m.claimType)}</td>
      <td>${escapeHtml(m.claimValue)}</td>
      <td>${escapeHtml(m.roleName)}</td>
      <td><button class="btn btn-sm btn-outline-danger ab" data-action="delete-mapping" data-id="${m.id}" title="Delete"><i class="bi bi-trash"></i></button></td>
    </tr>`).join("");

  return `
    <p class="text-muted small">Any authenticated caller presenting this exact claim type/value is granted the role, in addition to any directly assigned roles.</p>
    <form id="mapping-form" class="row g-2 mb-3">
      <div class="col-md-3"><input class="form-control" name="claimType" placeholder="Claim type (e.g. groups)" required></div>
      <div class="col-md-3"><input class="form-control" name="claimValue" placeholder="Claim value" required></div>
      <div class="col-md-4"><select class="form-select" name="roleId" required><option value="">Role...</option>${roleOptions}</select></div>
      <div class="col-md-2"><button class="btn btn-primary w-100" type="submit">Add</button></div>
    </form>
    <table class="table table-sm align-middle">
      <thead><tr><th>Claim type</th><th>Claim value</th><th>Role</th><th></th></tr></thead>
      <tbody>${rows || '<tr><td colspan="4" class="text-muted">No claim mappings yet.</td></tr>'}</tbody>
    </table>`;
}

// ---- API keys ----

function renderApiKeys() {
  const rows = state.apiKeys.map((k) => `
    <tr>
      <td class="fw-semibold">${escapeHtml(k.name)}</td>
      <td><code>${escapeHtml(k.keyPrefix)}.***</code></td>
      <td>${k.enabled ? '<span class="badge text-bg-success">Enabled</span>' : '<span class="badge text-bg-secondary">Disabled</span>'}</td>
      <td class="small">${k.lastUsedAt ? new Date(k.lastUsedAt).toLocaleString() : "never"}</td>
      <td>${k.roles.map((r) => `<span class="badge text-bg-info badge-scope">${escapeHtml(r.roleName)}</span>`).join(" ") || "\u2014"}</td>
      <td class="text-nowrap">
        <button class="btn btn-sm btn-outline-secondary ab" data-action="manage-apikey-roles" data-id="${k.id}" title="Manage roles"><i class="bi bi-person-gear"></i></button>
        <button class="btn btn-sm btn-outline-danger ab" data-action="delete-apikey" data-id="${k.id}" title="Delete"><i class="bi bi-trash"></i></button>
      </td>
    </tr>`).join("");

  el("tab-apikeys").innerHTML =
    pageHeader("bi-key", "API Keys", '<button class="btn btn-sm btn-primary" data-action="open-add-apikey"><i class="bi bi-plus-lg me-1"></i>Create API Key</button>') +
    (state.apiKeys.length ? `
      <table class="table table-sm align-middle bg-white">
        <thead><tr><th>Name</th><th>Prefix</th><th>Status</th><th>Last used</th><th>Roles</th><th></th></tr></thead>
        <tbody>${rows}</tbody>
      </table>` : emptyState("bi-key", "No API keys yet."));
}

function addApiKeyModalHtml() {
  return `
    <form id="apikey-form">
      <div class="row g-3">
        <div class="col-md-8"><label class="form-label">Name <span class="text-danger">*</span></label>
          <input class="form-control" name="name" required></div>
        <div class="col-md-4"><label class="form-label">Expires</label>
          <input class="form-control" name="expiresAt" type="date"></div>
      </div>
      <div class="mt-4 text-end">
        <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button>
        <button type="submit" class="btn btn-primary">Create</button>
      </div>
    </form>`;
}

// ---- Role assignment modal (shared by Users and API keys) ----

function openRoleAssignmentModal(kind, principal) {
  const assignedIds = new Set(principal.roles.map((r) => r.roleId));
  const rows = state.roles.map((r) => `
    <div class="form-check">
      <input class="form-check-input" type="checkbox" value="${r.id}" id="ra-${r.id}" ${assignedIds.has(r.id) ? "checked" : ""}>
      <label class="form-check-label" for="ra-${r.id}">${escapeHtml(r.name)}</label>
    </div>`).join("") || '<p class="text-muted">No roles exist yet - create one first.</p>';

  openModal(`<i class="bi bi-person-gear me-2"></i>Manage roles \u2014 ${escapeHtml(principal.name)}`, `
    <div class="list-group-flush mb-3">${rows}</div>
    <div class="text-end"><button class="btn btn-primary" id="ra-save-btn">Save</button></div>`);

  el("ra-save-btn").addEventListener("click", async () => {
    const basePath = kind === "user" ? `/admin/users/${principal.id}/roles` : `/admin/apikeys/${principal.id}/roles`;
    const checked = new Set([...document.querySelectorAll("#generic-modal-body input[type=checkbox]:checked")].map((cb) => cb.value));
    const toAdd = [...checked].filter((id) => !assignedIds.has(id));
    const toRemove = [...assignedIds].filter((id) => !checked.has(id));
    try {
      await Promise.all([
        ...toAdd.map((roleId) => api("POST", basePath, { roleId })),
        ...toRemove.map((roleId) => api("DELETE", `${basePath}/${roleId}`))
      ]);
      showToast("Roles updated.");
      closeModal();
      await loadAll();
    } catch (err) {
      showToast(err.message, true);
    }
  });
}

// ---- Permissions (hierarchical tree: role -> server -> tools/resources/prompts, bulk-applied) ----

async function renderPermissions() {
  const roleOptions = state.roles
    .map((r) => `<option value="${r.id}" ${state.permTree.roleId === r.id ? "selected" : ""}>${escapeHtml(r.name)}</option>`)
    .join("");

  let body;
  if (!state.permTree.roleId) {
    body = emptyState("bi-shield-check", "Choose a role above to view and bulk-assign its permissions.");
  } else {
    const grants = state.permissions.filter((p) => p.roleId === state.permTree.roleId);
    const serverWideIds = new Map();
    const granularIds = new Map();
    grants.forEach((g) => {
      if (g.kind === 0) serverWideIds.set(g.serverId, g.id);
      else granularIds.set(`${g.serverId}|${g.kind}|${g.itemName}`, g.id);
    });

    if (!state.servers.length) {
      body = emptyState("bi-hdd-network", "No servers registered yet.");
    } else {
      const nodes = await Promise.all(state.servers.map((s) => renderServerNode(s, serverWideIds, granularIds)));
      body = `<div class="mb-3">${nodes.join("")}</div>
        <button class="btn btn-primary" id="perm-apply-btn">Apply changes</button>`;
    }
  }

  el("tab-permissions").innerHTML =
    pageHeader("bi-shield-check", "Permissions") +
    '<p class="text-muted small">Choose a role, then check the servers, tools, resources, and prompts it should access - checking a server grants everything in it. Apply once to save all changes at once.</p>' +
    `<div class="row g-2 mb-3"><div class="col-md-4">
      <select class="form-select" id="perm-role-select"><option value="">Select a role...</option>${roleOptions}</select>
    </div></div>${body}`;

  el("perm-role-select").addEventListener("change", (e) => {
    state.permTree.roleId = e.target.value;
    renderPermissions();
  });

  el("perm-apply-btn")?.addEventListener("click", applyPermissionChanges);
}

async function renderServerNode(server, serverWideIds, granularIds) {
  const expanded = state.permTree.expanded.has(server.id);
  const wholeChecked = serverWideIds.has(server.id);

  let childrenHtml = "";
  if (expanded) {
    const catalog = await fetchCatalog(server.id);
    const group = (title, items, kind, labelOf) => items.length ? `
      <div class="mb-2">
        <div class="fw-semibold small text-muted">${title}</div>
        ${items.map((item) => {
          const name = labelOf(item);
          const checked = wholeChecked || granularIds.has(`${server.id}|${kind}|${name}`);
          return `
            <div class="form-check">
              <input class="form-check-input perm-item" type="checkbox" data-server="${server.id}" data-kind="${kind}"
                data-name="${escapeHtml(name)}" ${checked ? "checked" : ""} ${wholeChecked ? "disabled" : ""}>
              <label class="form-check-label">${escapeHtml(name)}</label>
            </div>`;
        }).join("")}
      </div>` : "";
    childrenHtml = group("Tools", catalog.tools, 1, (t) => t.name) + group("Resources", catalog.resources, 2, (r) => r.uri) + group("Prompts", catalog.prompts, 3, (p) => p.name);
    if (!childrenHtml) {
      childrenHtml = '<p class="text-muted small mb-0">No cached catalog - sync the server first.</p>';
    }
  }

  return `
    <div class="border rounded mb-2 perm-tree-node">
      <div class="d-flex align-items-center p-2">
        <button type="button" class="btn btn-sm btn-link text-decoration-none px-2"
          data-action="toggle-permission-node" data-id="${server.id}"><i class="bi ${expanded ? "bi-chevron-down" : "bi-chevron-right"}"></i></button>
        <div class="form-check mb-0 flex-grow-1">
          <input class="form-check-input perm-server" type="checkbox" data-server="${server.id}" ${wholeChecked ? "checked" : ""}>
          <label class="form-check-label fw-semibold">${escapeHtml(server.name)}
            <span class="text-muted small">(${escapeHtml(server.namespacePrefix)})</span>
          </label>
        </div>
      </div>
      ${expanded ? `<div class="perm-tree-children ps-4 pb-2">${childrenHtml}</div>` : ""}
    </div>`;
}

async function applyPermissionChanges() {
  const roleId = state.permTree.roleId;
  if (!roleId) return;

  const grants = state.permissions.filter((p) => p.roleId === roleId);
  const serverWideIds = new Map();
  const granularIds = new Map();
  grants.forEach((g) => {
    if (g.kind === 0) serverWideIds.set(g.serverId, g.id);
    else granularIds.set(`${g.serverId}|${g.kind}|${g.itemName}`, g.id);
  });

  const toCreate = [];
  const toDelete = [];

  document.querySelectorAll(".perm-server").forEach((cb) => {
    const serverId = cb.dataset.server;
    const already = serverWideIds.has(serverId);
    if (cb.checked && !already) toCreate.push({ serverId, kind: 0, itemName: null });
    if (!cb.checked && already) toDelete.push(serverWideIds.get(serverId));
  });

  // Item checkboxes disabled by a checked whole-server box are implied, not independent grants.
  document.querySelectorAll(".perm-item:not(:disabled)").forEach((cb) => {
    const serverId = cb.dataset.server;
    const kind = Number(cb.dataset.kind);
    const name = cb.dataset.name;
    const key = `${serverId}|${kind}|${name}`;
    const already = granularIds.has(key);
    if (cb.checked && !already) toCreate.push({ serverId, kind, itemName: name });
    if (!cb.checked && already) toDelete.push(granularIds.get(key));
  });

  if (!toCreate.length && !toDelete.length) {
    showToast("No changes to apply.");
    return;
  }

  try {
    await Promise.all([
      ...toCreate.map((p) => api("POST", `/admin/roles/${roleId}/permissions`, { roleId, ...p })),
      ...toDelete.map((id) => api("DELETE", `/admin/permissions/${id}`))
    ]);
    showToast(`Applied ${toCreate.length} grant(s) and ${toDelete.length} revocation(s).`);
    await loadAll();
  } catch (err) {
    showToast(err.message, true);
  }
}

// ---- Action map: every data-action button in the app routes through here ----

const ACTION_HANDLERS = {
  "open-add-server": () => {
    openModal('<i class="bi bi-plus-circle me-2"></i>Register MCP Server', addServerModalHtml());
    el("server-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const form = new FormData(e.target);
      try {
        await api("POST", "/admin/servers", {
          name: form.get("name"),
          namespacePrefix: form.get("namespacePrefix"),
          endpoint: form.get("endpoint"),
          credentialReference: form.get("credentialReference") || null,
          credentialHeader: form.get("credentialHeader") || "Authorization",
          credentialPrefix: form.get("credentialPrefix") || "Bearer "
        });
        showToast("Server registered.");
        closeModal();
        await loadAll();
      } catch (err) {
        showToast(err.message, true);
      }
    });
  },
  "open-edit-server": (id) => {
    const server = state.servers.find((s) => s.id === id);
    openModal('<i class="bi bi-pencil me-2"></i>Edit MCP Server', addServerModalHtml(server));
    el("server-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const form = new FormData(e.target);
      try {
        await api("PUT", `/admin/servers/${id}`, {
          name: form.get("name"),
          namespacePrefix: form.get("namespacePrefix"),
          endpoint: form.get("endpoint"),
          credentialReference: form.get("credentialReference") || null,
          credentialHeader: form.get("credentialHeader") || "Authorization",
          credentialPrefix: form.get("credentialPrefix") || "Bearer "
        });
        showToast("Server updated.");
        closeModal();
        await loadAll();
      } catch (err) {
        showToast(err.message, true);
      }
    });
  },
  "sync-server": async (id) => {
    await api("POST", `/admin/servers/${id}/sync`);
    delete state.catalogCache[id];
    showToast("Catalog sync triggered.");
    await loadAll();
  },
  "toggle-server-catalog": async (id) => {
    if (state.serverCatalogExpanded.has(id)) {
      state.serverCatalogExpanded.delete(id);
    } else {
      state.serverCatalogExpanded.add(id);
      await fetchCatalog(id);
    }
    renderServers();
  },
  "view-primitive": (id, target) => {
    const kind = Number(target.dataset.kind);
    const index = Number(target.dataset.index);
    const key = KIND_CATALOG_KEY[kind];
    const item = state.catalogCache[id]?.[key]?.[index];
    if (!item) return;
    openModal(`<i class="bi bi-braces me-2"></i>${escapeHtml(KIND_NAMES[kind].replace(/s$/, ""))} metadata`, primitiveMetadataHtml(kind, item));
  },
  "manage-server-permissions": async (id) => {
    state.permTree.expanded.add(id);
    await fetchCatalog(id);
    renderPermissions();
    switchTab("permissions");
  },
  "delete-server": async (id) => {
    if (!confirm("Delete this server registration?")) return;
    await api("DELETE", `/admin/servers/${id}`);
    showToast("Server deleted.");
    await loadAll();
  },

  "open-add-model-provider": () => {
    openModal('<i class="bi bi-cpu me-2"></i>Add Model Provider', modelProviderModalHtml());
    el("model-provider-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      try {
        await api("POST", "/admin/model-providers", modelProviderPayload(new FormData(e.target)));
        showToast("Model provider added.");
        closeModal();
        await loadAll();
      } catch (err) { showToast(err.message, true); }
    });
  },
  "open-edit-model-provider": (id) => {
    const provider = state.modelProviders.find((p) => p.id === id);
    openModal('<i class="bi bi-pencil me-2"></i>Edit Model Provider', modelProviderModalHtml(provider));
    el("model-provider-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      try {
        await api("PUT", `/admin/model-providers/${id}`, modelProviderPayload(new FormData(e.target)));
        showToast("Model provider updated.");
        closeModal();
        await loadAll();
      } catch (err) { showToast(err.message, true); }
    });
  },
  "delete-model-provider": async (id) => {
    if (!confirm("Delete this model provider, its routes, and associated grants?")) return;
    await api("DELETE", `/admin/model-providers/${id}`);
    showToast("Model provider deleted.");
    await loadAll();
  },
  "open-add-model-route": () => {
    if (!state.modelProviders.length) {
      showToast("Add a model provider first.", true);
      return;
    }
    openModal('<i class="bi bi-signpost me-2"></i>Add Model Route', modelRouteModalHtml());
    el("model-route-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const form = new FormData(e.target);
      try {
        await api("POST", "/admin/model-routes", {
          providerId: form.get("providerId"),
          publicName: form.get("publicName"),
          downstreamModel: form.get("downstreamModel"),
          enabled: form.get("enabled") === "on"
        });
        showToast("Model route added.");
        closeModal();
        await loadAll();
      } catch (err) { showToast(err.message, true); }
    });
  },
  "open-edit-model-route": (id) => {
    const route = state.modelRoutes.find((r) => r.id === id);
    openModal('<i class="bi bi-pencil me-2"></i>Edit Model Route', modelRouteModalHtml(route));
    el("model-route-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const form = new FormData(e.target);
      try {
        await api("PUT", `/admin/model-routes/${id}`, {
          providerId: form.get("providerId"),
          publicName: form.get("publicName"),
          downstreamModel: form.get("downstreamModel"),
          enabled: form.get("enabled") === "on"
        });
        showToast("Model route updated.");
        closeModal();
        await loadAll();
      } catch (err) { showToast(err.message, true); }
    });
  },
  "delete-model-route": async (id) => {
    if (!confirm("Delete this public model route?")) return;
    await api("DELETE", `/admin/model-routes/${id}`);
    showToast("Model route deleted.");
    await loadAll();
  },
  "open-add-model-permission": () => {
    openModal('<i class="bi bi-shield-plus me-2"></i>Add Model Access Grant', modelPermissionModalHtml());
    const scope = el("model-permission-scope");
    const syncScope = () => {
      const provider = scope.value === "0";
      el("model-permission-provider-wrap").classList.toggle("d-none", !provider);
      el("model-permission-route-wrap").classList.toggle("d-none", provider);
    };
    scope.addEventListener("change", syncScope);
    syncScope();
    el("model-permission-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const form = new FormData(e.target);
      const scopeValue = Number(form.get("scope"));
      try {
        await api("POST", "/admin/model-permissions", {
          roleId: form.get("roleId"),
          scope: scopeValue,
          providerId: scopeValue === 0 ? form.get("providerId") || null : null,
          modelRouteId: scopeValue === 1 ? form.get("modelRouteId") || null : null
        });
        showToast("Model access granted.");
        closeModal();
        await loadAll();
      } catch (err) { showToast(err.message, true); }
    });
  },
  "delete-model-permission": async (id) => {
    await api("DELETE", `/admin/model-permissions/${id}`);
    showToast("Model access revoked.");
    await loadAll();
  },

  "open-add-user": () => {
    openModal('<i class="bi bi-person-plus me-2"></i>Add User', addUserModalHtml());
    el("user-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const form = new FormData(e.target);
      try {
        await api("POST", "/admin/users", {
          username: form.get("username"),
          password: form.get("password") || null,
          externalSubject: form.get("externalSubject") || null
        });
        showToast("User created.");
        closeModal();
        await loadAll();
      } catch (err) {
        showToast(err.message, true);
      }
    });
  },
  "manage-user-roles": (id) => openRoleAssignmentModal("user", state.users.find((u) => u.id === id)),
  "delete-user": async (id) => {
    if (!confirm("Delete this user?")) return;
    await api("DELETE", `/admin/users/${id}`);
    showToast("User deleted.");
    await loadAll();
  },

  "open-add-role": () => {
    openModal('<i class="bi bi-person-badge me-2"></i>Add Role', addRoleModalHtml());
    el("role-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const form = new FormData(e.target);
      try {
        await api("POST", "/admin/roles", {
          name: form.get("name"),
          description: form.get("description") || null,
          isGlobalAdmin: form.get("isGlobalAdmin") === "on",
          isUserAdmin: form.get("isUserAdmin") === "on",
          isServerAdmin: form.get("isServerAdmin") === "on"
        });
        showToast("Role created.");
        closeModal();
        await loadAll();
      } catch (err) {
        showToast(err.message, true);
      }
    });
  },
  "open-edit-role": (id) => {
    const role = state.roles.find((r) => r.id === id);
    openModal('<i class="bi bi-pencil me-2"></i>Edit Role', addRoleModalHtml(role));
    el("role-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const form = new FormData(e.target);
      try {
        await api("PUT", `/admin/roles/${id}`, {
          name: form.get("name"),
          description: form.get("description") || null,
          isGlobalAdmin: form.get("isGlobalAdmin") === "on",
          isUserAdmin: form.get("isUserAdmin") === "on",
          isServerAdmin: form.get("isServerAdmin") === "on"
        });
        showToast("Role updated.");
        closeModal();
        await loadAll();
      } catch (err) {
        showToast(err.message, true);
      }
    });
  },
  "manage-role-permissions": (id) => {
    state.permTree.roleId = id;
    renderPermissions();
    switchTab("permissions");
  },
  "delete-role": async (id) => {
    if (!confirm("Delete this role? Assignments and permissions using it are also removed.")) return;
    await api("DELETE", `/admin/roles/${id}`);
    showToast("Role deleted.");
    await loadAll();
  },
  "open-claim-mappings": () => {
    openModal('<i class="bi bi-diagram-3 me-2"></i>Claim-based role mapping', claimMappingsModalHtml());
    el("mapping-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const form = new FormData(e.target);
      try {
        await api("POST", "/admin/claim-mappings", {
          claimType: form.get("claimType"),
          claimValue: form.get("claimValue"),
          roleId: form.get("roleId")
        });
        showToast("Claim mapping added.");
        await loadAll();
        openModal('<i class="bi bi-diagram-3 me-2"></i>Claim-based role mapping', claimMappingsModalHtml());
      } catch (err) {
        showToast(err.message, true);
      }
    });
  },
  "delete-mapping": async (id) => {
    await api("DELETE", `/admin/claim-mappings/${id}`);
    showToast("Claim mapping deleted.");
    await loadAll();
    openModal('<i class="bi bi-diagram-3 me-2"></i>Claim-based role mapping', claimMappingsModalHtml());
  },

  "open-add-apikey": () => {
    openModal('<i class="bi bi-key me-2"></i>Create API Key', addApiKeyModalHtml());
    el("apikey-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const form = new FormData(e.target);
      try {
        const expires = form.get("expiresAt");
        const result = await api("POST", "/admin/apikeys", {
          name: form.get("name"),
          expiresAt: expires ? new Date(expires).toISOString() : null
        });
        await loadAll();
        openModal('<i class="bi bi-key me-2"></i>API key created', `
          <p>Copy this key now - it will not be shown again.</p>
          <code class="key-display d-block p-2 bg-light border rounded">${escapeHtml(result.key)}</code>`);
      } catch (err) {
        showToast(err.message, true);
      }
    });
  },
  "manage-apikey-roles": (id) => openRoleAssignmentModal("apikey", state.apiKeys.find((k) => k.id === id)),
  "delete-apikey": async (id) => {
    if (!confirm("Delete this API key?")) return;
    await api("DELETE", `/admin/apikeys/${id}`);
    showToast("API key deleted.");
    await loadAll();
  },

  "toggle-permission-node": async (id) => {
    if (state.permTree.expanded.has(id)) {
      state.permTree.expanded.delete(id);
    } else {
      state.permTree.expanded.add(id);
      await fetchCatalog(id);
    }
    renderPermissions();
  }
};

// ---- wiring ----

document.addEventListener("DOMContentLoaded", async () => {
  // Check auth configuration
  try {
    const config = await fetch("/auth/config").then((r) => r.json());
    if (config.oidcConfigured) {
      const oidcSection = el("oidc-login-section");
      if (oidcSection) {
        oidcSection.classList.remove("d-none");
        el("oidc-login-btn").href = "/account/login?returnUrl=" + encodeURIComponent(window.location.pathname);
      }
    }
  } catch { /* default to form */ }

  // Check if session cookie is active (e.g. from OIDC return)
  if (!state.token) {
    try {
      const session = await fetch("/auth/session").then((r) => r.json());
      if (session.authenticated && session.token) {
        state.token = session.token;
        state.username = session.username;
        sessionStorage.setItem("mcpproxy_token", state.token);
        sessionStorage.setItem("mcpproxy_username", state.username);
      }
    } catch { /* silent */ }
  }

  el("login-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const username = el("login-username").value;
    const password = el("login-password").value;
    try {
      const result = await api("POST", "/auth/login", { username, password });
      state.token = result.accessToken;
      state.username = username;
      sessionStorage.setItem("mcpproxy_token", state.token);
      sessionStorage.setItem("mcpproxy_username", username);
      el("login-error").classList.add("d-none");
      await enterApp();
    } catch (err) {
      el("login-error").textContent = "Sign-in failed. Check your username and password.";
      el("login-error").classList.remove("d-none");
    }
  });

  el("logout-btn").addEventListener("click", signOut);

  document.querySelectorAll("#main-tabs a").forEach((a) => {
    a.addEventListener("click", () => switchTab(a.dataset.tab));
  });

  document.body.addEventListener("change", (e) => {
    if (!e.target.classList.contains("perm-server")) return;
    const serverId = e.target.dataset.server;
    document.querySelectorAll(`.perm-item[data-server="${serverId}"]`).forEach((cb) => {
      cb.disabled = e.target.checked;
    });
  });

  document.body.addEventListener("click", async (e) => {
    const target = e.target.closest("[data-action]");
    if (!target) return;
    const action = target.dataset.action;
    const id = target.dataset.id;
    try {
      const handler = ACTION_HANDLERS[action];
      if (handler) {
        await handler(id, target);
      }
    } catch (err) {
      showToast(err.message, true);
    }
  });

  if (state.token) {
    enterApp();
  }
});

async function enterApp() {
  try {
    el("whoami").textContent = state.username || "";
    el("login-view").style.display = "none";
    el("app-view").classList.add("visible");
    switchTab("servers");
    await loadAll();
  } catch (err) {
    showToast(err.message, true);
    signOut();
  }
}
