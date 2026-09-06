# MCP Proxy Developer Guide

This guide documents the implementation and extension model. For installation, configuration, administration, and day-to-day use, read [README.md](README.md).

## Solution Layout

- `src/McpProxy`: ASP.NET Core MCP gateway, authentication, RBAC, administration API, catalog synchronization, and proxy UI.
- `src/McpClient`: separate OIDC web client, LLM chat application, MCP server registry, and client UI.
- `tests/McpProxy.Tests`: unit tests for security, permissions, configuration, tokens, credentials, and invalidation behavior.
- `McpProxy.slnx`: solution containing both applications and the test project.

Both applications target .NET 10. The projects generate XML documentation files during build. Public classes, records, interfaces, enums, and service methods are documented in source so IDE and generated API tooling can explain their contracts.

## Runtime Architecture

```text
Northbound MCP client or browser
        |
        | OIDC cookie, JWT bearer, or X-Api-Key
        v
McpProxy
  |-- authentication and principal enrichment
  |-- PermissionService: direct roles plus claim mappings
  |-- GatewayService: namespace, resolve, authorize, invoke
  |-- CatalogCache: local latest downstream catalog
  |-- ICatalogInvalidationBus: memory events or Redis pub/sub
  |-- ProxyDbContext: identity, RBAC, and server state
  |
  +--> downstream MCP servers over Streamable HTTP

McpClient
  |-- OIDC cookie session
  |-- McpSessionFactory: credential forwarding
  |-- ChatService: LLM conversation and tool loop
  +--> configured MCP servers, including McpProxy
```

The proxy's database is the source of truth for users, roles, permissions, claim mappings, API keys, and registered downstream servers. The MCP catalog is a derived cache. A node may rebuild its catalog from the database and downstream server; Redis is used to notify other nodes that their local copy is stale.

## Proxy Project

### Authentication

`Program.cs` registers a policy scheme named `Smart`. It chooses API-key authentication when the configured API-key header is present, bearer authentication when an `Authorization` header is present, and cookie authentication otherwise.

`Auth:Mode` controls bearer and interactive authentication:

- `Internal`: locally issued JWTs signed with the configured or generated symmetric key.
- `Oidc`: JWT validation and interactive authorization-code login through the configured authority.

`AuthEndpoints` exposes local login, OIDC configuration, session inspection, logout, and token exchange. `ApiKeyAuthenticationHandler` looks up a non-secret key prefix, verifies the secret hash, rejects disabled or expired keys, and updates `LastUsedAt` after successful authentication.

OIDC claim mapping is independent of authentication. `PermissionService` resolves roles from direct user/API-key assignments and `ClaimRoleMapping` rows. A claim-mapped external principal can therefore receive a global administrator role without a corresponding local user row.

### Authorization

`PermissionService` is the authorization boundary. It provides:

- `IsGlobalAdminAsync`: checks `Role.IsGlobalAdmin`.
- `HasAdminScopeAsync`: checks global, user-admin, or server-admin flags.
- `CanAccessAsync`: checks server-wide or named capability permission.
- `GetAccessibleServerIdsAsync`: returns servers with at least one capability grant.

`AdminScopeRouting.RequireAdminScope` adds an endpoint filter to administration route groups. Authentication and authorization remain separate: the identity handler establishes claims; the permission service decides what those claims may do.

`CapabilityKind.Server` means a whole-server grant. `Tool`, `Resource`, and `Prompt` grants require `ItemName`. The gateway intentionally reports denied capabilities as unknown to avoid revealing the existence of protected tools or resources.

### Data model

`ProxyDbContext` contains:

- `Role`: named role and administration flags.
- `Permission`: role-to-server/capability grant.
- `User`: local account, optional password hash, optional external subject.
- `UserRole`: user-role join table.
- `ApiKey`: public prefix, secret hash, enabled/expiry/usage state.
- `ApiKeyRole`: API-key role join table.
- `ClaimRoleMapping`: claim type/value to role mapping.
- `McpServer`: downstream endpoint, namespace, credential reference, and sync status.

Indexes enforce unique role names, usernames, API-key prefixes, and server namespace prefixes. Foreign keys cascade role assignments and server permissions where appropriate.

`DatabaseSeeder` calls `EnsureCreatedAsync` and seeds an `Administrator` role plus the configured bootstrap user only when both users and roles are empty. It does not modify an existing database.

### Gateway and catalogs

`CatalogCache` refreshes enabled downstream servers at startup and every five minutes. `RefreshOneAsync` can also be called by the administration API. The cache stores tools, resources, and prompts in a process-local concurrent dictionary.

`ICatalogInvalidationBus` has two implementations:

- `InMemoryCatalogInvalidationBus` for memory-cache/default deployments.
- `RedisCatalogInvalidationBus` for multi-node deployments.

Redis subscribers listen on `mcp-proxy:catalog-invalidated`. A successful refresh publishes the server ID. Other nodes query their shared database for that server and refresh their own local catalog without republishing the event.

`GatewayService` reads only the local catalog for list operations. It prefixes names and URIs using `{NamespacePrefix}__{NativeName}`, resolves a public identifier back to its server, checks `IPermissionService`, then creates a short-lived downstream client for invocation.

`DownstreamClientFactory` creates a Streamable HTTP MCP client and uses `CredentialResolver` to resolve optional `env:VARIABLE_NAME` credentials. Reserved protocol and forwarding headers are rejected.

### Configuration and providers

`DatabaseOptions` selects `Sqlite`, `SqlServer`, or `Postgres` and chooses the provider-specific connection string, falling back to `ConnectionString` when needed. SQLite remains the default.

`CacheOptions` selects memory or Redis. Redis registration provides the distributed cache package and the catalog invalidation bus. Cache metadata currently remains intentionally small because catalog entries are kept local for efficient SDK object access.

`SecretReferenceResolver` accepts literals unchanged and resolves `env:NAME` through `ISecretProvider`. `EnvironmentSecretProvider` is the current implementation. A managed vault implementation can replace it through dependency injection.

`LoggingOptions` controls minimum level and built-in provider selection. Current providers are Console, Debug, EventSource, and None. The options object leaves room for a future syslog, Seq, Application Insights, or structured logging adapter.

## Client Project

`McpClient` is a separate ASP.NET Core application. It uses cookie authentication with OpenID Connect and protects `/api` routes when `Oidc:Enabled` is true.

`McpServerRegistry` loads `Data/mcp-servers.json`, assigns missing IDs, enforces unique names, clones values at API boundaries, and writes through a temporary file before replacement. It is protected by a lock for concurrent requests.

`McpSessionFactory` connects to enabled servers. It can forward an access token, ID token, no token, or a configured API key. Connection failures are collected in `McpConnectionSet` so one unavailable server does not prevent other servers from being used.

`ChatService` obtains current LLM settings, connects to all enabled MCP servers, collects tools, prefixes tool names by server, submits the conversation to the configured `IChatClient`, and returns assistant text, tool-call records, and warnings. Conversation history is held by `ConversationStore` in memory and partitioned by user and conversation ID.

`LlmSettingsStore` persists editable settings to `Data/llm-settings.json`. `LlmClientFactory` validates the endpoint and model before building an OpenAI-compatible chat client with the configured maximum tool iterations.

## HTTP Surface

### Proxy authentication

- `GET /auth/config`
- `GET /account/login`
- `POST /account/logout`
- `GET /auth/session`
- `POST /auth/login`
- `POST /auth/token-exchange`
- `POST /auth/exchange`

### Proxy administration

- Roles: `GET/POST /admin/roles`, `PUT/DELETE /admin/roles/{id}`, role permissions routes.
- Permissions: `GET /admin/permissions`, `DELETE /admin/permissions/{id}`.
- Users: list/create/delete and user-role assignment routes.
- API keys: list/create/delete and API-key role assignment routes.
- Claim mappings: list/create/delete routes.
- Servers: list/create/update/delete, sync, and cached catalog routes.

User-admin scope protects roles, users, API keys, claim mappings, and permissions. Server-admin scope protects server routes. Global admin implies both.

### Proxy MCP

The combined MCP endpoint is `/mcp`. Per-server MCP endpoints are `/servers/{serverScope}/mcp`. The Model Context Protocol operations are tools/list, tools/call, resources/list, resources/read, prompts/list, and prompts/get.

### Client API

- `GET /api/session`
- `POST /api/chat`
- `POST /api/chat/{conversationId}/reset`
- `GET /api/servers`
- `POST /api/servers/{serverName}/resources/read`
- `POST /api/servers/{serverName}/prompts/get`
- CRUD `/api/server-configurations`
- `GET/PUT /api/llm-settings`

## Security Rules

- Never store a plaintext API key after creation; only its prefix and hash are persisted.
- Never commit OIDC, LLM, downstream, database, Redis, or bootstrap secrets.
- Use `env:NAME` references and deployment environment variables.
- Treat the OIDC client secret as the secret value, not the Entra secret ID.
- Use HTTPS and secure cookie policy outside local development.
- Set a stable internal JWT signing key across nodes when internal JWTs are used.
- Keep claim mappings narrowly scoped; a global-admin mapping grants full administration to every matching identity.
- Keep downstream credential headers away from Host, Cookie, Origin, forwarding, and MCP session headers.

## Build, Test, and Diagnostics

```powershell
$env:DOTNET_ROOT = "C:\Users\bstewart\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet restore McpProxy.slnx
dotnet build McpProxy.slnx
dotnet test McpProxy.slnx
```

The tests cover:

- PBKDF2 password hashing.
- API-key generation, parsing, hashing, and verification.
- Direct and claim-mapped RBAC.
- Global and scoped administration roles.
- JWT reserved-claim protection and expiry.
- Database provider connection-string selection.
- Environment secret references.
- Downstream credential header safety.
- In-memory catalog invalidation and cancellation.

Use a separate integration environment for PostgreSQL, SQL Server, Redis, OIDC, and real downstream MCP tests. The unit suite deliberately does not require those services.

## Extension Points

- Implement `ISecretProvider` for a managed secret service.
- Add a provider branch to `DatabaseOptions` and startup registration.
- Implement another `ICatalogInvalidationBus` for a different broker.
- Add a structured logging provider without coupling domain services to a vendor.
- Extend `PermissionService` for new authorization concepts.
- Extend `McpServerOptions` and `McpSessionFactory` for new transport or credential modes.

Keep public types and methods XML-documented. The build generates documentation files and reports missing public documentation warnings during development.
