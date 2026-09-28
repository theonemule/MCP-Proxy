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

### Database provider behavior

`Program.cs` binds `DatabaseOptions` and selects the EF Core provider during service registration:

- `DatabaseProvider.Sqlite` calls `UseSqlite`.
- `DatabaseProvider.SqlServer` calls `UseSqlServer`.
- `DatabaseProvider.Postgres` calls `UseNpgsql`.

`DatabaseOptions.GetConnectionString()` selects the provider-specific setting and falls back to the
generic `ConnectionString`. `DatabaseSeeder` uses `EnsureCreatedAsync`; this is suitable for the
current initialization model but is not a replacement for a versioned migration pipeline in a mature
production deployment.

The Docker Compose profile uses PostgreSQL as a shared database. The application does not create a
separate schema per node; all nodes use the same EF model and database state.

### Redis provider behavior

When `Cache:Enabled` is true, `Cache:Provider` is `Redis`, and a connection string is present,
startup registers `AddStackExchangeRedisCache` and `RedisCatalogInvalidationBus`. Redis pub/sub uses
the fixed channel `mcp-proxy:catalog-invalidated`. The message body is a server GUID.

`CatalogCache.RefreshOneAsync` publishes after a successful downstream refresh. A receiving node
loads the matching enabled server from its shared database and refreshes its local catalog with
publishing disabled, preventing notification loops. Memory mode registers
`InMemoryCatalogInvalidationBus` and is appropriate only for process-local coordination.

### OIDC authentication-only versus authorization-enabled deployments

The code supports two operational interpretations of OIDC:

1. **Authentication-only**: OIDC establishes an authenticated principal. The deployment may place
  authorization at another boundary or expose only uniformly protected routes. The token is still
  validated by the proxy, but no assumption should be made that arbitrary OIDC roles automatically
  become proxy permissions.
2. **Authentication plus authorization**: `PermissionService` combines principal claims with
  `UserRole`, `ApiKeyRole`, and `ClaimRoleMapping` data. Claim mappings can grant proxy roles to an
  external principal without a local `User` row. Linked external subjects additionally receive the
  local user's direct roles.

This distinction matters because OIDC token validation alone does not grant `IsGlobalAdmin`,
`IsUserAdmin`, `IsServerAdmin`, or MCP capability permissions. Those are proxy authorization data.

### Azure Entra integration details

For Entra, the proxy registration needs an authorization-code redirect URI matching
`/signin-oidc`, an audience matching the access token's `aud` claim, and a client secret **value**.
The client registration needs its own redirect URI and delegated permission for the proxy API scope.

Recommended Entra mapping flow:

1. Define app roles on the proxy registration.
2. Assign users/groups or applications to those app roles in the enterprise application.
3. Confirm the resulting access or ID token contains the expected `roles` claim.
4. Create a proxy `Role` representing the desired authorization.
5. Create a `ClaimRoleMapping` for `roles` and the exact Entra app-role value.
6. Test both authentication and the intended proxy scope with a newly issued token.

The proxy deliberately does not treat an arbitrary incoming `roles` claim as an administrator role
until an explicit database mapping exists. This prevents identity-provider role names from silently
becoming proxy privileges.

### Logging provider behavior

`Program.cs` clears the default providers, parses `LoggingOptions.MinimumLevel` as a standard
`Microsoft.Extensions.Logging.LogLevel`, then selects the configured built-in provider:

- `Console` uses a single-line timestamped console logger.
- `Debug` registers the .NET debug logger.
- `EventSource` registers the EventSource logger.
- `None` registers no application provider.

The `Preset`, `SyslogHost`, `SyslogPort`, and `ApplicationName` properties are configuration
contracts for deployment/provider expansion; the current startup path uses `Provider` and
`MinimumLevel`. Do not log secret values, bearer tokens, complete API keys, or passwords from a new
provider.

### Default Microsoft Learn server

The client registry includes an enabled entry for:

```text
https://learn.microsoft.com/api/mcp
```

It uses Streamable HTTP and `ForwardToken.None`, so no user credential or API key is sent by default.
The entry is in `src/McpClient/Data/mcp-servers.json` and can be disabled or removed through the
client configuration workflow. The proxy may also register Microsoft Learn as a downstream server
through its administration API when the service should expose it to other MCP clients.

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

## MCP 2.x and 2026-07-28 Protocol Support

The proxy references `ModelContextProtocol.AspNetCore` 2.2.0. The northbound transport explicitly uses `HttpServerSessionMode.Stateless`, which serves the `2026-07-28` stateless protocol directly while retaining initialize-era compatibility on the same HTTP endpoints. A modern request is self-contained and does not use `Mcp-Session-Id`; older clients may still negotiate an initialize-capable protocol revision.

`DownstreamClientFactory` uses the SDK 2.x `HttpClientTransport` in Streamable HTTP mode without pinning `McpClientOptions.ProtocolVersion`. This allows the SDK to negotiate the current protocol with a modern downstream server and fall back when the downstream peer only implements an older revision.

The `2026-07-28` protocol's discovery and per-request metadata are handled by the SDK. Gateway authorization remains request-scoped and continues to filter tools, resources, and prompts independently of the negotiated protocol revision.

## Model Router Architecture

The model router is parallel to the MCP transport rather than embedded inside it. Its code lives under `src/McpProxy/Models` and reuses the existing authentication principal, role resolution, API-key authentication, and claim-role mapping infrastructure.

The durable model entities are:

- `ModelProvider`, which stores the provider kind, public slug, base endpoint, optional unified chat path, and environment-secret references.
- `ModelRoute`, which maps one globally unique public model name to a provider-specific downstream model ID.
- `ModelPermission`, which grants a role either provider-wide access or access to one model route.

`PermissionService` is the authorization boundary. `CanAccessModelProviderAsync` controls native pass-through. `CanAccessModelRouteAsync` controls the normalized API. A provider-wide grant implies access to all routes owned by that provider, while a route grant never implies native provider access.

`ModelRouterService` implements the normalized `ModelChatRequest` contract and dispatches to protocol adapters for OpenAI-compatible APIs, Ollama, and AWS Bedrock. The same contract supports buffered responses and normalized streaming. OpenAI-compatible SSE, Ollama NDJSON, and Bedrock `ConverseStream` EventStream frames are converted into `ModelChatStreamEvent` values. `GenericHttp` is intentionally native-only. This keeps the normalized surface small while allowing providers with unusual APIs to remain usable without gateway-specific translation code.

`NativeModelProxyService` forwards native requests after removing gateway credentials and hop-by-hop headers. It then applies only the configured downstream credential. The configured gateway API-key header is removed dynamically, not just the default `X-Api-Key`. Native responses are streamed back to the caller.

Bedrock uses the Converse API. A bearer API key can come from the provider credential reference or `AWS_BEARER_TOKEN_BEDROCK`. Otherwise the dependency-free `AwsSigV4Signer` signs requests using credentials resolved from explicit `env:` references or standard AWS environment variables. No AWS SDK dependency is required.

### Model router HTTP surface

Northbound model routes are:

- `GET /models/` to list public aliases visible to the caller.
- `POST /models/chat` for the normalized chat contract; `"stream": true` switches the response to SSE.
- `POST /models/chat/stream` as an explicit always-streaming alias.
- `/models/native/{providerScope}` and `/models/native/{providerScope}/{**path}` for native provider pass-through, including raw provider streaming.

Administration routes are:

- `GET/POST /admin/model-providers`
- `PUT/DELETE /admin/model-providers/{id}`
- `GET/POST /admin/model-routes`
- `PUT/DELETE /admin/model-routes/{id}`
- `GET/POST /admin/model-permissions`
- `DELETE /admin/model-permissions/{id}`

Provider and route administration use `AdminScope.ServerAdmin`. Model permission administration uses `AdminScope.UserAdmin`.

The normalized request is deliberately provider-neutral:

```json
{
  "model": "fast-coder",
  "systemPrompt": "Keep the answer concise.",
  "prompt": "Explain this function.",
  "stream": true,
  "parameters": {
    "temperature": 0.2,
    "max_tokens": 800
  }
}
```

OpenAI-compatible parameters pass through except fields owned by the gateway. Ollama parameters are translated into its native request shape. Bedrock inference fields are mapped to `inferenceConfig`, with other model-specific fields placed under `additionalModelRequestFields`.

The normalized SSE event contract uses `start`, `delta`, `usage`, `done`, and `error` event names. `AwsEventStreamReader` validates AWS EventStream prelude and message CRCs before decoding Bedrock streaming payloads.

### Model schema compatibility

The project historically uses `EnsureCreated` rather than EF migrations. Fresh databases receive the model tables from the EF model. `ModelSchemaUpgrade.EnsureAsync` creates only the new model-router tables and indexes for existing SQLite, PostgreSQL, and SQL Server databases. This preserves existing users, roles, API keys, MCP registrations, and permissions when upgrading.

### Model router security rules

- Provider secrets are environment references. Plaintext downstream credentials are never returned by the admin API or persisted in model-provider rows.
- Northbound `Authorization`, the configured API-key header, cookies, forwarding headers, proxy-auth headers, and hop-by-hop headers are never forwarded to a downstream model provider.
- Route grants are the least-privilege default for the normalized API. Provider grants should be reserved for clients that need the provider's native API or every model hosted by that provider.
- Unauthorized models and providers return not-found behavior rather than disclosing registered resources.
- Unified downstream failures are logged server-side and returned to callers as generic gateway failures so provider response details are not reflected across the security boundary.

The model-router tests cover provider-versus-route authorization, OpenAI-compatible request translation and downstream credential replacement, normalized OpenAI/Ollama/Bedrock streaming, deterministic Bedrock SigV4 signing, and schema upgrade of an existing SQLite database. A container smoke test also verifies both `2026-07-28` `server/discover` and `2025-11-25` `initialize` against the same stateless MCP endpoint.
