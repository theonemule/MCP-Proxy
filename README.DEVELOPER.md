# MCP Proxy Developer Guide

This guide is for contributors and maintainers. Start with [README.md](README.md) for product usage.

Related documentation:

- [Architecture](docs/ARCHITECTURE.md)
- [MCP Gateway](docs/MCP-GATEWAY.md)
- [Model Gateway and Intelligent Routing](docs/MODEL-GATEWAY.md)
- [Optional Browser Client](docs/CLIENT.md)
- [HTTP API Reference](docs/API.md)
- [Operations Guide](docs/OPERATIONS.md)

## Solution layout

~~~text
src/
  McpProxy/
    Admin/            administration and authentication endpoints
    Configuration/    database, cache, logging, and secret options
    Data/             EF entities and DbContext
    Mcp/              MCP gateway and endpoint scoping
    Models/           model gateway, routing, provider adapters, native proxy
    Registry/         MCP downstream clients, catalog cache, invalidation
    Security/         auth options, API keys, JWTs, permissions, claim types
    wwwroot/          administration SPA
  McpClient/
    Configuration/    client options
    Models/           chat and API models
    Services/         LLM, MCP, token, conversation, and settings services
    wwwroot/          optional browser client
tests/
  McpProxy.Tests/
~~~

Both applications target .NET 10.

The proxy uses ASP.NET Core, Entity Framework Core, Model Context Protocol .NET SDK 2.2.0, SQLite/PostgreSQL/SQL Server, optional Redis, and OpenAI-compatible HTTP contracts for northbound model inference.

## Runtime composition

Program.cs is composition-oriented. Major services include:

- ProxyDbContext
- PermissionService
- DownstreamClientFactory
- CatalogCache
- ICatalogInvalidationBus
- GatewayService
- ModelRouterService
- ModelRouteSelector
- OpenAiCompatibilityService
- NativeModelProxyService

Authentication is added only when Auth:Enabled is true.

Startup creates the configured database if needed, applies model-schema compatibility upgrades, seeds the bootstrap administrator only for an empty identity store, starts the MCP catalog cache, and maps the HTTP surfaces.

## Authentication design

The general proxy policy scheme is Smart.

It chooses:

1. API-key authentication when the configured API-key header is present.
2. API-key or JWT bearer authentication based on Authorization: Bearer.
3. Cookie authentication when no API credential is present.

The OpenAI /v1 surface uses OpenAiSmart. It avoids browser redirects and interprets OpenAI's bearer credential slot as either:

- a gateway API key when it has the mcp_ format
- a JWT/OIDC bearer token otherwise

OpenAiBearerCredentialClassifier only classifies the bearer value. JWT validity is still determined by ASP.NET bearer authentication.

### Internal mode

AuthMode.Internal validates bearer tokens signed with the proxy's symmetric signing key.

If no signing key is configured, a random key is generated at startup. That is convenient for development but unsuitable when tokens must survive restarts or be accepted across nodes.

### OIDC mode

AuthMode.Oidc configures:

- interactive authorization-code login with PKCE
- cookie sessions for the administration UI
- JWT bearer validation for APIs
- saved OIDC tokens on the browser session

OIDC bearer validation can enrich a principal with proxy-local identity claims when the external subject matches an enabled local User.

A principal that is not linked to a local user can still receive authorization through ClaimRoleMapping.

## Authorization design

PermissionService is the central authorization boundary.

Authorization sources are:

- UserRole
- ApiKeyRole
- ClaimRoleMapping

Role administration flags are:

- IsGlobalAdmin
- IsUserAdmin
- IsServerAdmin

MCP authorization uses Permission.

Model authorization uses ModelPermission.

Authentication handlers establish identity. They should not silently grant application privileges.

### MCP permission behavior

CapabilityKind.Server grants the entire downstream server.

Tool, Resource, and Prompt grants apply to one named capability.

Discovery is filtered. A caller should not learn about protected tools merely because another caller can see them.

### Model permission behavior

ModelPermissionScope.Route grants one public logical route.

ModelPermissionScope.Provider grants provider-wide model access and native provider proxy access.

PermissionService.CanAccessModelRouteTargetAsync is used after candidate ranking so secondary targets do not bypass provider and route authorization rules.

A direct route grant authorizes internal targets behind that route. A provider-only grant does not automatically authorize a secondary target on another ungranted provider.

## MCP gateway internals

The northbound MCP server is configured through the official ASP.NET Core MCP SDK.

The Streamable HTTP transport uses stateless mode.

Mapped endpoints are:

~~~text
/mcp
/servers/{serverScope}/mcp
~~~

McpEndpointScope reads the optional route scope and narrows operations to one namespace.

GatewayService implements:

- tool discovery
- tool invocation
- resource discovery
- resource reading
- prompt discovery
- prompt rendering

Northbound names use:

~~~text
{namespace}__{native-name}
~~~

CatalogCache stores a process-local snapshot of downstream tools, resources, and prompts.

DownstreamClientFactory creates short-lived downstream MCP clients using Streamable HTTP.

CredentialResolver resolves optional env:NAME credentials and blocks reserved or unsafe headers.

## MCP cache invalidation

ICatalogInvalidationBus has:

- InMemoryCatalogInvalidationBus
- RedisCatalogInvalidationBus

Redis uses:

~~~text
mcp-proxy:catalog-invalidated
~~~

A refresh publishes the server ID. Receiving nodes reload the registered server from the shared database and refresh their local catalog without republishing.

Redis coordinates invalidation. The actual MCP catalog objects remain process-local.

## Model gateway architecture

The preferred model surface is /v1.

Durable model objects are:

- ModelProvider
- ModelRoute
- ModelRouteTarget
- ModelPermission

A ModelRoute is both the public OpenAI model identity and the implicit primary target.

ModelRouteTarget adds provider/model candidates behind the same public identity.

This preserves older single-target routes while allowing intelligent routing.

## Provider kinds

ModelProviderKind contains:

- OpenAiCompatible
- Ollama
- AwsBedrock
- GenericHttp

OpenAiCompatible is the convergence path for new integrations.

Ollama and native Bedrock are adapters for providers that need translation.

GenericHttp is native-proxy only.

## Explicit model versus default router

OpenAiCompatibilityService distinguishes two cases.

When model is present:

~~~text
request model
  -> exact public ModelRoute lookup
  -> intelligent candidate selection inside that route
~~~

When model is absent:

~~~text
request
  -> enabled IsDefault route
  -> intelligent candidate selection inside that route
~~~

The default route is not consulted when a caller explicitly names a public model.

This behavior is covered by regression tests.

A public name such as router or auto has no special parser behavior. It is intelligent only because the administrator configures candidates behind it.

## Intelligent model selection

ModelRoutingRequirements.Infer examines OpenAI-compatible request JSON.

The current heuristic inputs include:

- reasoning_effort
- reasoning.effort
- request text
- approximate input token count
- requested output tokens
- tools
- image inputs
- JSON or JSON-Schema response formats
- coarse task specialty

The current specialty vocabulary is:

- general
- coding
- math
- vision
- creative
- summarization

The selector hard-filters known incompatibilities and computes a suitability score.

Ranking is:

1. protocol compatibility and hard constraints
2. suitability score
3. administrative priority
4. smooth weighted selection among exact suitability and priority ties
5. failover order and circuit state

Reasoning deficits carry a much larger penalty than using a somewhat more capable model.

Simple work places more weight on cost and latency hints than high-reasoning work.

Unknown capability values remain eligible with a penalty for backward compatibility.

## Routing metadata

ModelRoute and ModelRouteTarget support:

- Priority
- Weight
- ReasoningLevel
- MaxContextTokens
- MaxOutputTokens
- SupportsTools
- SupportsVision
- SupportsJsonSchema
- CostTier
- LatencyTier
- Specialties

ModelRoute also has IsDefault.

null capability booleans mean unknown.

Zero context/output values mean unknown or not declared.

Cost and latency are relative tiers from 1 through 5. Zero means unknown.

## Model routing state

ModelRoutingState is a singleton in one proxy process.

It stores:

- target failure counters
- circuit-open expiration
- smooth weighted-selection state

Defaults are three consecutive failures and a 30-second cooldown.

This state is not persisted and is not distributed through Redis.

## Model failover

The OpenAI-compatible service can attempt the next ranked target for safe transient failures.

The intended retry class includes:

- HTTP 408
- HTTP 409
- HTTP 425
- HTTP 429
- HTTP 5xx
- network failures
- timeouts not caused by caller cancellation
- invalid downstream payloads

Downstream 400, 401, 403, and 404 errors are not treated as cross-provider failover signals.

Streaming failover is allowed only before the first northbound chunk or event.

## OpenAI-compatible surface

OpenAiCompatibilityEndpoints maps:

~~~text
GET  /v1/models
GET  /v1/models/{model}
POST /v1/chat/completions
POST /v1/{**operation}
~~~

The catch-all POST route supports generic JSON operations such as Responses and Embeddings when the selected provider can serve them.

For OpenAiCompatible providers:

1. clone incoming JSON
2. replace public model with selected downstream model ID
3. apply configured provider credential
4. forward remaining OpenAI fields
5. rewrite downstream model identities back to the public route name

Chat streaming preserves OpenAI SSE and terminates with data: [DONE].

Generic SSE event names are preserved.

## Ollama adapter

The Ollama adapter targets native /api/chat.

It converts OpenAI-style messages and supported request controls to Ollama JSON and converts responses back to OpenAI-compatible structures for /v1/chat/completions.

Native Ollama is a chat adapter, not the generic provider for arbitrary /v1 operations.

If Ollama exposes a suitable OpenAI-compatible /v1 surface, it can instead be registered as OpenAiCompatible.

## AWS Bedrock adapter

The Bedrock adapter supports Converse and ConverseStream.

It translates:

- system and developer instructions
- messages
- tool definitions
- tool results
- tool choice
- common inference controls

AwsEventStreamReader decodes streaming frames and converts them to OpenAI-style chunks.

Credentials can come from a Bedrock bearer token or SigV4 credentials.

## Native provider proxy

NativeModelProxyService maps:

~~~text
/models/native/{providerScope}
/models/native/{providerScope}/{**path}
~~~

The native proxy:

- preserves provider-specific request semantics
- strips northbound auth, cookies, forwarding, and hop-by-hop headers
- applies only the configured downstream credential
- streams the downstream response

Provider-level permission is required.

## Legacy normalized model API

ModelRouterService maps:

~~~text
GET  /models/
POST /models/chat
POST /models/chat/stream
~~~

This is the earlier provider-neutral contract.

New integrations should use /v1. Request-aware intelligent routing lives in the OpenAI compatibility flow.

## Model administration API

Administration groups are:

~~~text
/admin/model-providers
/admin/model-routes
/admin/model-route-targets
/admin/model-permissions
~~~

Provider, route, and target administration require server-admin scope.

Model permission administration requires user-admin scope.

The UI exposes common management and basic routing fields. Rich capability metadata is available through the JSON APIs.

## Data layer

ProxyDbContext contains identity/RBAC, MCP server, and model-gateway entities.

Important uniqueness constraints include:

- role name
- user name
- API-key prefix
- MCP namespace prefix
- model provider slug
- model route public name
- route/provider/downstream-model target tuple

DatabaseSeeder calls EnsureCreatedAsync followed by ModelSchemaUpgrade.EnsureAsync.

The project does not currently use a versioned EF migration pipeline.

ModelSchemaUpgrade creates missing model tables and adds intelligent-routing columns for SQLite, PostgreSQL, and SQL Server deployments created by older builds.

## Browser client internals

McpClient is intentionally separate.

Important services are:

- McpServerRegistry
- McpSessionFactory
- ChatService
- ConversationStore
- LlmSettingsStore
- LlmClientFactory
- UserTokenProvider

McpServerRegistry persists configured servers in Data/mcp-servers.json.

LlmSettingsStore persists LLM profiles in Data/llm-settings.json.

The client supports a direct Hosted profile and a Proxy profile.

When the Proxy profile has no explicit credential, ChatService can supply the signed-in user's access token.

Conversation state is process-local and in memory.

## Administration UI

src/McpProxy/wwwroot is a static administration SPA backed by /admin and /auth APIs.

When changing the model UI, preserve rich route and target fields that are not editable in the basic form. Current handlers echo those values back during updates to avoid erasing API-configured metadata.

## Database providers

DatabaseOptions selects:

- SQLite with UseSqlite
- SQL Server with UseSqlServer
- PostgreSQL with UseNpgsql

Provider-specific connection strings fall back to ConnectionString when empty.

The Docker Compose profile uses PostgreSQL.

## Redis behavior

When Cache:Enabled is true, Cache:Provider is Redis, and a connection string is present, startup registers AddStackExchangeRedisCache and RedisCatalogInvalidationBus.

Redis currently coordinates MCP catalog invalidation. Do not assume it distributes every process-local state object.

## Secret resolution

ISecretProvider abstracts secret retrieval.

The current implementation is EnvironmentSecretProvider.

SecretReferenceResolver supports env:VARIABLE_NAME for application configuration.

Model-provider credentials require environment references rather than raw secret values in provider rows.

## Logging

Program.cs clears the default providers and selects one of:

- Console
- Debug
- EventSource
- None

MinimumLevel uses normal .NET logging levels.

Do not add password, token, complete API-key, provider-secret, or raw Authorization logging.

## Build and test

~~~bash
dotnet restore McpProxy.slnx
dotnet build McpProxy.slnx
dotnet test McpProxy.slnx
~~~

When the host does not have .NET 10:

~~~bash
docker run --rm   -v "$PWD:/src"   -w /src   mcr.microsoft.com/dotnet/sdk:10.0   dotnet test McpProxy.slnx -c Release --nologo
~~~

The suite covers identity, RBAC, configuration, MCP behavior, model authorization, OpenAI compatibility, provider adaptation, routing selection, failover behavior, bearer-slot credential classification, Bedrock signing/streaming, and schema upgrades.

## Routing regression expectations

When changing model routing, preserve these behaviors:

- an explicitly supplied model route overrides the configured default route
- omitting model uses the enabled default route
- capability incompatibilities are filtered before policy priority
- high reasoning demand can outrank a lower-priority weak model
- simple requests can prefer cheaper or faster otherwise-equivalent models
- weight matters only among suitability and priority ties
- unauthorized secondary targets are removed
- streaming does not fail over after output starts
- old routes with unknown capability metadata remain usable

## Security invariants

Do not regress these boundaries:

- plaintext proxy API-key secrets are never persisted
- model-provider secret values are not persisted in provider rows
- northbound bearer credentials are never reused as downstream model-provider credentials
- OIDC roles do not automatically become proxy roles
- route access and native provider access are separate
- unknown and unauthorized model aliases remain intentionally difficult to distinguish
- provider error internals are not blindly reflected northbound
- downstream MCP and model credential headers reject unsafe reserved headers
- admin scopes are enforced server-side

## Extension points

Reasonable seams include:

- a new ISecretProvider
- another database provider
- another ICatalogInvalidationBus
- a structured or external logging provider
- a new model provider adapter
- a richer routing classifier
- distributed model health state
- additional authorization concepts
- new MCP transport or credential modes
- versioned database migrations

When adding a model provider, first decide whether it belongs in OpenAiCompatible, needs a provider adapter, or should be native pass-through only.

Avoid adding a proprietary northbound inference contract unless the OpenAI-compatible surface cannot represent the required behavior.

## Documentation expectations

Behavioral changes that affect users should update:

- README.md
- the relevant file under docs
- README.DEVELOPER.md when architecture or extension behavior changes
- tests that lock down the changed contract
