# MCP Gateway

The MCP gateway combines multiple downstream MCP servers behind one authenticated and authorized northbound endpoint.

Use this guide when configuring downstream servers, namespaces, synchronization, and MCP permissions.

## Northbound endpoints

The combined endpoint is:

~~~text
/mcp
~~~

A single registered server can also be addressed through:

~~~text
/servers/{serverScope}/mcp
~~~

The server scope is the registered namespace prefix.

Both surfaces use the same identity and permission model.

## Protocol and transport

The proxy uses the Model Context Protocol .NET SDK 2.2.0.

Northbound MCP is served over Streamable HTTP in stateless session mode.

Downstream connections also use the MCP .NET client and Streamable HTTP.

The proxy relies on the SDK for protocol negotiation and transport-level MCP behavior. Authorization is applied independently of the negotiated protocol version.

## Registering a downstream server

Use the administration UI or:

~~~text
POST /admin/servers
~~~

Example request:

~~~json
{
  "name": "Documentation",
  "namespacePrefix": "docs",
  "endpoint": "https://example.internal/mcp",
  "credentialReference": "env:DOCS_MCP_TOKEN",
  "credentialHeader": "Authorization",
  "credentialPrefix": "Bearer "
}
~~~

The request fields are:

| Field | Purpose |
| --- | --- |
| name | Human-readable server name. |
| namespacePrefix | Unique prefix used in the northbound namespace. |
| endpoint | Downstream Streamable HTTP MCP endpoint. |
| credentialReference | Optional env:NAME reference for a static downstream credential. |
| credentialHeader | Header that receives the resolved credential. Defaults to Authorization. |
| credentialPrefix | Text prepended to the resolved credential. Defaults to Bearer plus a space. |

New server registrations are enabled by default.

The current create/update API does not expose an enabled field.

## Namespacing

Suppose a server is registered with:

~~~text
namespacePrefix = docs
~~~

and it exposes:

~~~text
tool: search
prompt: summarize
~~~

The combined gateway exposes names such as:

~~~text
docs__search
docs__summarize
~~~

Namespacing prevents collisions when multiple downstream servers expose capabilities with the same native name.

The namespace prefix must be unique among registered MCP servers.

## Synchronization

The proxy maintains a process-local catalog of tools, resources, and prompts discovered from each enabled server.

A manual synchronization is available at:

~~~text
POST /admin/servers/{id}/sync
~~~

The last cached catalog can be inspected at:

~~~text
GET /admin/servers/{id}/catalog
~~~

The server record also exposes:

- LastSyncedAt
- LastError

Catalog synchronization is also performed by the runtime rather than requiring every northbound discovery call to contact every downstream server.

## Catalog invalidation in multi-node deployments

With memory-only operation, every proxy node owns its own catalog.

With Redis enabled, a successful refresh publishes the registered server ID on:

~~~text
mcp-proxy:catalog-invalidated
~~~

Other nodes then reload the server registration from the shared database and refresh their own local cache.

Redis therefore coordinates freshness. It does not replace the process-local MCP catalog.

## Downstream credentials

Static downstream credentials use an environment reference:

~~~text
env:VARIABLE_NAME
~~~

Example:

~~~text
CredentialReference = env:DOCS_MCP_TOKEN
CredentialHeader = Authorization
CredentialPrefix = Bearer
~~~

The following headers cannot be used as downstream credential headers because they affect routing, forwarding, or MCP protocol state:

- Host
- Cookie
- Origin
- Forwarded
- X-Forwarded-For
- X-Forwarded-Host
- X-Forwarded-Proto
- MCP-Protocol-Version
- Mcp-Session-Id

A missing environment variable or unsafe header causes credential resolution to fail instead of silently making an unsafe request.

## Authentication

The northbound MCP surface uses the proxy authentication system.

Depending on configuration, callers can authenticate with:

- gateway API key
- internal JWT
- OIDC/JWT bearer token

Browser cookies are part of the general application authentication system but programmatic MCP clients normally use an API credential.

A gateway API key can normally be sent in:

~~~text
X-Api-Key: mcp_<prefix>.<secret>
~~~

## Authorization model

MCP permissions belong to roles.

A role can be granted:

- the whole server
- one tool
- one resource
- one prompt

Capability enum values are:

~~~text
0 Server
1 Tool
2 Resource
3 Prompt
~~~

A server-wide grant uses a null ItemName.

A Tool, Resource, or Prompt grant requires ItemName.

## Creating a permission

Permissions can be created through the role endpoint:

~~~text
POST /admin/roles/{roleId}/permissions
~~~

Example whole-server grant:

~~~json
{
  "roleId": "<role-guid>",
  "serverId": "<server-guid>",
  "kind": 0,
  "itemName": null
}
~~~

Example one-tool grant:

~~~json
{
  "roleId": "<role-guid>",
  "serverId": "<server-guid>",
  "kind": 1,
  "itemName": "search"
}
~~~

The role ID in the route is the authoritative role used by the endpoint.

## Permission evaluation

Effective roles can come from:

- direct UserRole assignments
- ApiKeyRole assignments
- ClaimRoleMapping matches

The gateway checks the caller before returning discovery results and before invoking a capability.

Unauthorized capabilities are omitted from discovery.

A denied capability is intentionally handled like an unknown capability where practical so the gateway does not unnecessarily reveal protected names.

## Combined endpoint versus per-server endpoint

Use /mcp when a client should see an aggregated catalog from several permitted servers.

Use /servers/{serverScope}/mcp when the integration should be explicitly scoped to one server.

Per-server scoping is useful when:

- an application knows which MCP service it needs
- namespace aggregation is unnecessary
- operational debugging should isolate one downstream service

Authorization still applies.

## Request flow

A typical tool invocation follows this path:

~~~text
northbound MCP client
  -> authenticate caller
  -> resolve namespaced capability
  -> authorize role/capability
  -> create downstream MCP client
  -> resolve downstream static credential
  -> invoke downstream server
  -> return MCP result
~~~

## Failure behavior

A downstream failure does not grant access to another server or bypass namespace resolution.

Synchronization failures are stored in LastError for operational visibility.

A temporarily unavailable server can fail independently without changing role assignments or the durable server registration.

## Administration scopes

Managing registered MCP servers requires ServerAdmin or GlobalAdmin.

Managing users, roles, API keys, claim mappings, and permissions requires UserAdmin or GlobalAdmin.

These checks are enforced by endpoint filters on the server, not only by the administration UI.

## Example setup

A typical least-privilege setup is:

1. Create a role named DocumentationReader.
2. Register the Documentation MCP server with namespace docs.
3. Synchronize it.
4. Inspect its catalog.
5. Grant DocumentationReader only the required tools/resources/prompts.
6. Assign the role to a user, API key, or OIDC claim mapping.
7. Connect the client to /mcp.
8. Confirm only the granted docs capabilities appear.

## Troubleshooting

### The server registers but the catalog is empty

Check:

- endpoint URL
- downstream server availability
- downstream credential reference
- environment variable value
- LastError
- proxy logs

Then run the sync endpoint again.

### A tool exists downstream but is not visible

Check:

- role permission
- exact ItemName
- server-wide grant
- role assignment
- OIDC claim mapping
- namespace scoping
- current cached catalog

### A capability name contains the wrong prefix

The public prefix comes from NamespacePrefix on the registered server.

Changing the prefix changes northbound names after the catalog is refreshed.

### Different proxy nodes show different catalogs

Confirm all nodes use the same relational database and Redis invalidation configuration.

Catalog objects are still held locally on every node.

### A credential header is rejected

The requested header is reserved for HTTP routing, forwarding, or MCP protocol state. Use a normal provider credential header such as Authorization or a service-specific API-key header.
