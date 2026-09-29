# Architecture

MCP Proxy is a shared gateway with two parallel northbound planes:

- an MCP plane for tools, resources, and prompts
- a model-inference plane using an OpenAI-compatible API

Both planes reuse the same authentication principal and role system.

## System context

~~~mermaid
flowchart LR
    U[Users and applications]
    C[MCP clients]
    O[OpenAI-compatible clients]
    A[Administration browser]

    P[MCP Proxy]

    DB[(SQLite / PostgreSQL / SQL Server)]
    R[(Redis optional)]

    M1[Downstream MCP server A]
    M2[Downstream MCP server B]

    AI1[OpenAI-compatible provider]
    AI2[Ollama]
    AI3[AWS Bedrock]
    AI4[Generic native provider]

    C -->|/mcp| P
    O -->|/v1| P
    A -->|/admin + cookie/OIDC| P
    U --> C
    U --> O

    P <--> DB
    P <--> R

    P --> M1
    P --> M2

    P --> AI1
    P --> AI2
    P --> AI3
    P --> AI4
~~~

## Major components

### ASP.NET Core host

src/McpProxy/Program.cs composes authentication, authorization, EF Core, MCP protocol handlers, model services, cache coordination, static administration files, and endpoint mappings.

### Identity and authorization

Security contains:

- AuthOptions
- ApiKeyAuthenticationHandler
- JwtTokenService
- OpenAiBearerCredentialClassifier
- PermissionService
- claim constants
- admin-scope routing helpers

Authentication answers **who is calling**.

PermissionService answers **what that principal may do**.

### MCP gateway

Mcp and Registry contain:

- GatewayService
- McpEndpointScope
- DownstreamClientFactory
- CatalogCache
- CredentialResolver
- catalog invalidation implementations

The gateway exposes one combined namespace while preserving per-server scoping.

### Model gateway

Models contains:

- OpenAiCompatibilityService
- OpenAiCompatibilityEndpoints
- ModelRouteSelector
- ModelRoutingState
- ModelRouterService
- NativeModelProxyService
- ModelCredentialResolver
- Bedrock signing and EventStream support
- model schema upgrade support

The OpenAI-compatible service is the preferred model boundary.

### Administration

Admin contains:

- authentication and session endpoints
- user, role, API-key, claim, and MCP administration
- model provider, route, target, and permission administration
- database bootstrap logic

The browser UI in wwwroot is a thin client over these APIs.

## Data model

~~~mermaid
erDiagram
    ROLE ||--o{ USER_ROLE : grants
    USER ||--o{ USER_ROLE : has

    ROLE ||--o{ API_KEY_ROLE : grants
    API_KEY ||--o{ API_KEY_ROLE : has

    ROLE ||--o{ CLAIM_ROLE_MAPPING : mapped_by

    ROLE ||--o{ PERMISSION : grants
    MCP_SERVER ||--o{ PERMISSION : protected_by

    MODEL_PROVIDER ||--o{ MODEL_ROUTE : primary_for
    MODEL_ROUTE ||--o{ MODEL_ROUTE_TARGET : has
    MODEL_PROVIDER ||--o{ MODEL_ROUTE_TARGET : target_provider

    ROLE ||--o{ MODEL_PERMISSION : grants
    MODEL_PROVIDER ||--o{ MODEL_PERMISSION : provider_scope
    MODEL_ROUTE ||--o{ MODEL_PERMISSION : route_scope
~~~

The relational database is authoritative for identity, authorization, registered MCP servers, model providers, model routes, route targets, and model permissions.

MCP catalogs and model health state are derived runtime state.

## MCP discovery flow

~~~mermaid
sequenceDiagram
    participant Client
    participant Proxy
    participant Perm as PermissionService
    participant Cache as CatalogCache

    Client->>Proxy: tools/list
    Proxy->>Perm: resolve caller roles and permissions
    Proxy->>Cache: read cached downstream catalogs
    Cache-->>Proxy: tools from registered servers
    Proxy->>Proxy: filter and namespace allowed tools
    Proxy-->>Client: authorized tool list
~~~

Discovery does not return unauthorized capabilities.

## MCP tool invocation flow

~~~mermaid
sequenceDiagram
    participant Client
    participant Proxy
    participant Perm as PermissionService
    participant Factory as DownstreamClientFactory
    participant Server as Downstream MCP server

    Client->>Proxy: tools/call docs__search
    Proxy->>Proxy: resolve namespace and native name
    Proxy->>Perm: authorize tool/server
    Perm-->>Proxy: allowed
    Proxy->>Factory: create downstream MCP client
    Factory->>Server: Streamable HTTP MCP request
    Server-->>Factory: MCP result
    Factory-->>Proxy: result
    Proxy-->>Client: result
~~~

## Model request flow with an explicit public model

When model is supplied, the gateway uses that public route and does not consult the default route.

~~~mermaid
sequenceDiagram
    participant Client
    participant API as /v1
    participant Perm as PermissionService
    participant Router as ModelRouteSelector
    participant Provider

    Client->>API: request model = gpt-5.6
    API->>Perm: authorize logical route
    API->>Router: rank candidates inside gpt-5.6
    Router-->>API: candidate order
    API->>Perm: authorize candidate target
    API->>Provider: request using private downstream model
    Provider-->>API: response
    API->>API: rewrite model identity to gpt-5.6
    API-->>Client: OpenAI-compatible response
~~~

Explicit public-route selection and backend target selection are separate steps.

A named route may still contain several targets.

## Model request flow with the default router

When model is omitted:

~~~mermaid
sequenceDiagram
    participant Client
    participant API as /v1
    participant DB
    participant Router as ModelRouteSelector
    participant Provider

    Client->>API: request without model
    API->>DB: resolve enabled IsDefault route
    DB-->>API: logical route
    API->>Router: infer requirements and rank candidates
    Router-->>API: candidate order
    API->>Provider: request
    Provider-->>API: response
    API-->>Client: public route identity
~~~

The default router is a normal ModelRoute with IsDefault set. There is no separate router entity.

## Native model pass-through flow

~~~mermaid
sequenceDiagram
    participant Client
    participant Proxy
    participant Perm as PermissionService
    participant Provider

    Client->>Proxy: /models/native/{provider}/...
    Proxy->>Perm: authorize provider scope
    Proxy->>Proxy: strip northbound credentials and unsafe headers
    Proxy->>Proxy: add configured provider credential
    Proxy->>Provider: native request
    Provider-->>Proxy: native response or stream
    Proxy-->>Client: native response or stream
~~~

Native pass-through intentionally does not normalize the provider API.

## Intelligent-routing decision pipeline

The current decision pipeline is:

~~~text
logical route
  -> primary target plus additional route targets
  -> enabled and protocol compatibility filtering
  -> hard capability and token-limit filtering
  -> suitability scoring
  -> administrative priority
  -> weighted tie selection
  -> authorization filtering
  -> execution and failover
  -> circuit-state updates
~~~

Hard filters include declared unsupported tools, vision, or JSON Schema, plus known insufficient context and output limits.

Soft scoring includes reasoning fit, specialty fit, unknown-capability risk, cost tier, and latency tier.

Priority is a policy control after task fit.

Weight is not general traffic splitting. It is used only when candidates are tied by suitability and priority.

## Authentication boundary

There are three common credential presentations:

~~~text
Browser
  -> cookie
  -> interactive OIDC or local login

API or MCP client
  -> X-Api-Key or Authorization: Bearer <JWT>

OpenAI client
  -> Authorization: Bearer <gateway key or JWT/OIDC token>
~~~

The gateway does not forward northbound authentication credentials to model providers.

Downstream credentials are configured independently on MCP server registrations or model providers.

## Authorization boundary

Authorization is role based:

~~~text
principal
  -> direct user or API-key roles
  + claim-role mappings
  -> effective roles
  -> admin flags
  -> MCP permissions
  -> model permissions
~~~

OIDC claims have no direct privilege meaning until mapped.

This prevents identity-provider role names from implicitly becoming gateway administration roles.

## MCP cache architecture

CatalogCache is process-local.

It stores discovered downstream MCP tools, resources, and prompts.

Refresh can happen:

- at startup
- periodically
- through the administration API

Redis can publish invalidation so another node knows to refresh its own process-local copy.

The database remains authoritative for the server registration itself.

## Model routing state architecture

ModelRoutingState is process-local.

It stores:

- consecutive failure count
- circuit-open expiration
- smooth weighted-selection state

Current defaults are:

~~~text
FailureThreshold = 3
Cooldown = 30 seconds
~~~

This state is intentionally not in the relational database and is not currently replicated through Redis.

## Shared and local state

### Durable and shareable through the relational database

- users
- roles
- API keys
- claim mappings
- MCP server registrations
- MCP permissions
- model providers
- model routes
- model route targets
- model permissions

### Coordinated through Redis

- MCP catalog invalidation messages

### Process-local

- MCP catalog object cache
- model failure counters
- model circuit state
- model smooth weighted state
- browser-client conversation history

## Single-node deployment

~~~mermaid
flowchart LR
    Client --> Proxy
    Proxy --> SQLite[(SQLite)]
    Proxy --> MCP[MCP servers]
    Proxy --> Models[Model providers]
~~~

This is appropriate for development and small deployments.

## Multi-node deployment

~~~mermaid
flowchart LR
    Client --> LB[Load balancer]
    LB --> P1[Proxy node 1]
    LB --> P2[Proxy node 2]

    P1 --> DB[(PostgreSQL / SQL Server)]
    P2 --> DB

    P1 --> Redis[(Redis)]
    P2 --> Redis

    P1 --> MCP[MCP servers]
    P2 --> MCP

    P1 --> Models[Model providers]
    P2 --> Models
~~~

Use a shared relational database and stable authentication configuration across nodes.

Remember that model routing health remains node-local.

## Security boundaries

The important trust boundaries are:

1. **Northbound client to proxy**
   - authenticate the caller
   - authorize the requested MCP capability or model route
2. **Proxy to downstream MCP server**
   - resolve the registered downstream credential
   - do not accidentally leak unrelated northbound headers
3. **Proxy to model provider**
   - never reuse the caller's gateway bearer credential as the provider credential
   - apply only the provider credential configured for that provider
4. **Administrator to configuration store**
   - enforce admin scope server-side
   - store secret references instead of model-provider plaintext secrets

The architecture deliberately treats native provider access as broader than logical route access.

## Schema initialization

DatabaseSeeder performs:

~~~text
EnsureCreatedAsync
ModelSchemaUpgrade.EnsureAsync
bootstrap seeding if users and roles are both empty
~~~

ModelSchemaUpgrade bridges older installations that predate the model tables and newer intelligent-routing columns.

It supports SQLite, PostgreSQL, and SQL Server.

The current project does not yet use a normal versioned migration history.

## Design intent

The architectural direction is:

- MCP protocol details remain inside the MCP SDK and gateway layer.
- OpenAI-compatible model inference is the stable northbound model contract.
- Provider-specific differences stay behind adapters or native pass-through.
- Authentication is separate from authorization.
- Public model identity is separate from provider-native model identity.
- Intelligent routing chooses for task fit first, policy controls second.
- Secret values stay out of durable provider configuration.
