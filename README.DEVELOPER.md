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

`ChatService` obtains current LLM settings, selects either the Proxy or Hosted inference profile, connects to all enabled MCP servers, collects tools, prefixes tool names by server, submits the conversation to the configured `IChatClient`, and returns assistant text, tool-call records, and warnings. Conversation history is held by `ConversationStore` in memory and partitioned by user and conversation ID.

`LlmSettingsStore` persists both inference profiles to `Data/llm-settings.json`. It migrates the earlier flat endpoint/API-key/model layout into the Hosted profile so existing client installations keep their direct provider configuration.

`LlmClientFactory` builds the OpenAI-compatible client from the active profile. Proxy mode uses an explicitly configured gateway API key when present. When the proxy key is empty, `ChatService` supplies the signed-in user's access token so end-to-end OIDC and model-route RBAC can be tested. Hosted mode uses the hosted profile's API key.

The client settings API does not return saved model API keys. Empty password fields preserve existing keys server-side. `POST /api/llm-models` performs OpenAI `GET /models` discovery against either profile and reports whether proxy discovery used an API key or the signed-in access token.

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

The model gateway is parallel to the MCP transport and reuses the same authentication principal, role resolution, API-key roles, claim-role mappings, and administrative security boundary.

The durable entities remain:

- `ModelProvider`, which stores provider protocol, endpoint, and environment-backed credential references.
- `ModelRoute`, which maps a public OpenAI model ID to one provider-native model identifier.
- `ModelPermission`, which grants a role either provider-wide access or access to one model route.

`PermissionService` is the authorization boundary. `CanAccessModelProviderAsync` controls native pass-through. `CanAccessModelRouteAsync` controls OpenAI-compatible inference. Provider-wide access implies all routes on that provider; route access does not imply native provider access.

### OpenAI v1 compatibility layer

`OpenAiCompatibilityEndpoints` exposes:

- `GET /v1/models`
- `GET /v1/models/{model}`
- `POST /v1/chat/completions`
- `POST /v1/{**operation}` for JSON OpenAI model operations such as Responses and Embeddings

`OpenAiCompatibilityService` is the primary model inference service for new clients.

The northbound contract deliberately matches OpenAI rather than defining another gateway schema. Public route names are OpenAI model IDs. Model-list responses use `object: "list"` and `object: "model"`. Chat responses use `chat.completion` and streaming responses use `chat.completion.chunk` records followed by `data: [DONE]`.

For `ModelProviderKind.OpenAiCompatible`, the service clones the incoming OpenAI request, changes only `model` to the private downstream model ID, forces the selected streaming mode, applies the provider credential, and forwards the remaining OpenAI fields unchanged. Responses are returned in the downstream OpenAI shape with `model` rewritten to the public alias.

The default chat path is `/v1/chat/completions`. When a provider base endpoint already ends in `/v1`, including `/openai/v1`, the resolver appends only the requested operation path. The catch-all POST route requires a `model` field, resolves permissions, substitutes the downstream model ID, and forwards the remaining JSON unchanged. For `AwsBedrock`, generic OpenAI operations use `/openai/v1/{operation}` and retain bearer-token or SigV4 authentication.

### Native provider adapters

`ModelProviderKind.Ollama` translates OpenAI messages and common sampling controls to `/api/chat`. Ollama output is converted back into OpenAI message, tool-call, usage, finish-reason, and streaming chunk structures.

`ModelProviderKind.AwsBedrock` translates OpenAI messages, developer/system instructions, function tools, tool results, tool choice, and common inference settings into Bedrock Converse. `ConverseStream` EventStream frames are decoded by `AwsEventStreamReader` and converted to OpenAI Chat Completion chunks. Bedrock tool-use events are mapped to OpenAI function tool calls.

A Bedrock endpoint that already implements OpenAI Chat Completions can instead be registered as `OpenAiCompatible` when its authentication can be represented by the normal static credential configuration. The native Bedrock adapter remains useful for SigV4 and models exposed through Converse.

`ModelProviderKind.GenericHttp` is native-proxy only.

### OpenAI authentication compatibility

OpenAI SDKs send API keys through:

```text
Authorization: Bearer <api-key>
```

Gateway-generated API keys use the existing `mcp_<prefix>.<secret>` format and are now accepted in that bearer position as well as in the configured legacy API-key header.

The `/v1` routes use the `OpenAiSmart` authentication policy scheme. It selects the gateway API-key handler for gateway bearer keys, JWT bearer validation for other bearer tokens, and the API-key scheme for unauthenticated challenges. This prevents browser-cookie redirects on the API surface.

`ApiKeyAuthenticationHandler` returns an OpenAI-shaped HTTP 401 envelope for `/v1` challenges.

### OpenAI errors

Validation, authorization, and routing errors use the standard OpenAI envelope:

```json
{
  "error": {
    "message": "...",
    "type": "invalid_request_error",
    "param": "model",
    "code": "model_not_found"
  }
}
```

Downstream implementation details are logged server-side and returned northbound as a generic `api_error`/`upstream_error` rather than reflecting provider error bodies across the gateway security boundary.

### Legacy normalized surface

`ModelRouterService` and the older endpoints remain available for compatibility:

- `GET /models/`
- `POST /models/chat`
- `POST /models/chat/stream`

They should not be used as the contract for new integrations. `/v1` is the convergence surface.

### Native provider proxy

`NativeModelProxyService` remains available under:

- `/models/native/{providerScope}`
- `/models/native/{providerScope}/{**path}`

It removes gateway authorization, cookies, forwarding headers, and hop-by-hop headers, adds only the configured downstream credential, and streams the provider response without normalization.

### Administration routes

Administration remains provider/route based:

- `GET/POST /admin/model-providers`
- `PUT/DELETE /admin/model-providers/{id}`
- `GET/POST /admin/model-routes`
- `PUT/DELETE /admin/model-routes/{id}`
- `GET/POST /admin/model-permissions`
- `DELETE /admin/model-permissions/{id}`

Provider and route administration use `AdminScope.ServerAdmin`. Model permission administration uses `AdminScope.UserAdmin`.

### Model schema compatibility

The project historically uses `EnsureCreated` rather than EF migrations. Fresh databases receive the model tables from the EF model. `ModelSchemaUpgrade.EnsureAsync` creates only the model-router tables and indexes for existing SQLite, PostgreSQL, and SQL Server databases. Existing users, roles, API keys, MCP registrations, and permissions are preserved.

### Security rules

- Provider secrets are environment references. Plaintext downstream credentials are not returned by admin APIs or stored as model-provider secret values.
- Northbound `Authorization`, configured gateway API-key headers, cookies, forwarding headers, proxy-auth headers, and hop-by-hop headers are never forwarded as client credentials to a provider.
- OpenAI-compatible direct routing applies the configured downstream provider credential after northbound credentials have been isolated.
- Route grants are the least-privilege default.
- Unknown and unauthorized model IDs use indistinguishable `model_not_found` behavior.
- Provider error bodies are not reflected to the caller.

The test suite covers model authorization, OpenAI model-list shape, direct OpenAI-compatible request preservation, generic `/v1` operation forwarding, Responses-style SSE event preservation, recursive model-alias rewriting, OpenAI bearer API-key parsing, Ollama-to-OpenAI adaptation, Bedrock OpenAI-runtime routing, Bedrock streaming adaptation, SigV4 signing, and database schema upgrades.

### OpenAI bearer-slot credential multiplexing

OpenAI clients place their configured API credential in `Authorization: Bearer <value>`. `OpenAiBearerCredentialClassifier` makes the proxy behavior explicit without changing that wire format.

`Bearer mcp_...` selects `ApiKeyAuthenticationHandler`. Any other non-empty `Bearer` credential selects ASP.NET JWT bearer authentication. In OIDC mode, external access tokens are therefore processed by the configured OIDC/JWT validation path. This allows a user to paste an OAuth/OIDC access token into an OpenAI SDK's `api_key` setting and still authenticate to the proxy.

This is bearer-token compatibility, not generic OAuth token introspection. Opaque tokens that the configured JWT/OIDC bearer handler cannot validate are rejected.

The browser client's Proxy credential field follows the same rule. An explicit value is sent unchanged as the OpenAI SDK bearer credential. If the field is blank, `ChatService` supplies the signed-in user's access token.
