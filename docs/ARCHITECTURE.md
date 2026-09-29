# Architecture

AI Governance Gateway is a shared AI governance enforcement point with two parallel northbound planes:

- an MCP plane for tools, resources, and prompts
- a model-inference plane using an OpenAI-compatible API

Both planes reuse the same authentication principal and role system.

Cross-cutting governance services apply authentication, authorization, configurable model-based guardrails, structured telemetry, audit correlation, and model-routing policy before traffic reaches downstream AI capabilities.

## System context

~~~mermaid
flowchart LR
    U[Users and applications]
    C[MCP clients]
    O[OpenAI-compatible clients]
    A[Administration browser]

    P[AI Governance Gateway]

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

src/AIGovernanceGateway/Program.cs composes authentication, authorization, EF Core, MCP protocol handlers, model services, cache coordination, static administration files, and endpoint mappings.

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
- ModelRoutingDecisionService
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
    participant Gateway
    participant Perm as PermissionService
    participant Cache as CatalogCache

    Client->>Gateway: tools/list
    Gateway->>Perm: resolve caller roles and permissions
    Gateway->>Cache: read cached downstream catalogs
    Cache-->>Gateway: tools from registered servers
    Gateway->>Gateway: filter and namespace allowed tools
    Gateway-->>Client: authorized tool list
~~~

Discovery does not return unauthorized capabilities.

## MCP tool invocation flow

~~~mermaid
sequenceDiagram
    participant Client
    participant Gateway
    participant Perm as PermissionService
    participant Factory as DownstreamClientFactory
    participant Server as Downstream MCP server

    Client->>Gateway: tools/call docs__search
    Gateway->>Gateway: resolve namespace and native name
    Gateway->>Perm: authorize tool/server
    Perm-->>Gateway: allowed
    Gateway->>Factory: create downstream MCP client
    Factory->>Server: Streamable HTTP MCP request
    Server-->>Factory: MCP result
    Factory-->>Gateway: result
    Gateway-->>Client: result
~~~

## Model request flow with an explicit public model

When model is supplied, the gateway uses that public route and does not consult the default route. A single-target route behaves as fixed routing. A multi-target route can still use model-driven backend selection.

~~~mermaid
sequenceDiagram
    participant Client
    participant API as /v1
    participant Perm as PermissionService
    participant Selector as ModelRouteSelector
    participant Decision as ModelRoutingDecisionService
    participant RoutingModel as Routing model
    participant Provider

    Client->>API: request model = gpt-5.6
    API->>Perm: authorize logical route
    API->>Selector: hard-filter and fallback-rank candidates
    Selector-->>API: compatible candidate order
    API->>Perm: authorize candidate targets
    API->>Decision: authorized candidates + request
    Decision->>RoutingModel: summarize intent, infer task, choose candidate
    RoutingModel-->>Decision: structured routing decision
    Decision-->>API: selected candidate first + fallback order
    API->>Provider: request using private downstream model
    Provider-->>API: response
    API->>API: rewrite model identity to gpt-5.6
    API-->>Client: OpenAI-compatible response
~~~

Explicit public-route selection and backend target selection are separate steps. If the route has one backend, or no routing model is configured, no semantic routing call is required.

## Model request flow with the default router

When model is omitted, the gateway first resolves the normal ModelRoute marked IsDefault. The routing controller is a different ModelRoute marked IsRoutingModel.

~~~mermaid
sequenceDiagram
    participant Client
    participant API as /v1
    participant DB
    participant Selector as ModelRouteSelector
    participant Decision as ModelRoutingDecisionService
    participant RoutingModel as Routing model
    participant Provider

    Client->>API: request without model
    API->>DB: resolve enabled IsDefault route
    DB-->>API: logical route
    API->>Selector: hard-filter and fallback-rank candidates
    Selector-->>API: compatible candidates
    API->>Decision: authorized candidates + request
    Decision->>RoutingModel: summarize intent, identify inference task, match candidates
    RoutingModel-->>Decision: selected candidate
    Decision-->>API: selected candidate first + failover order
    API->>Provider: inference request
    Provider-->>API: response
    API-->>Client: public route identity
~~~

The routing-model invocation bypasses intelligent routing to prevent recursion. If it is missing, unavailable, or returns an invalid decision, the deterministic selector order is used instead.

## Native model pass-through flow

~~~mermaid
sequenceDiagram
    participant Client
    participant Gateway
    participant Perm as PermissionService
    participant Provider

    Client->>Gateway: /models/native/{provider}/...
    Gateway->>Perm: authorize provider scope
    Gateway->>Gateway: strip northbound credentials and unsafe headers
    Gateway->>Gateway: add configured provider credential
    Gateway->>Provider: native request
    Provider-->>Gateway: native response or stream
    Gateway-->>Client: native response or stream
~~~

Native pass-through intentionally does not normalize the provider API.

## Intelligent-routing decision pipeline

The current decision pipeline is:

~~~text
logical route
  -> primary target plus additional route targets
  -> enabled and protocol compatibility filtering
  -> hard capability and token-limit filtering
  -> health/circuit filtering
  -> deterministic suitability/priority/weight fallback order
  -> authorization filtering
  -> routing model summarizes intent and identifies inference task
  -> routing model selects best eligible candidate
  -> selected candidate moves to front of fallback list
  -> execution and failover
  -> circuit-state updates
~~~

Hard filters include declared unsupported tools, vision, or JSON Schema, plus known insufficient context and output limits.

The model-driven decision uses the request intent and inference task together with candidate reasoning, specialty, context, feature, cost, and latency metadata.

The deterministic suitability/priority/weight order remains available if the routing controller cannot return a valid decision and remains the failover order behind the selected candidate.

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
    Client --> Gateway
    Gateway --> SQLite[(SQLite)]
    Gateway --> MCP[MCP servers]
    Gateway --> Models[Model providers]
~~~

This is appropriate for development and small deployments.

## Multi-node deployment

~~~mermaid
flowchart LR
    Client --> LB[Load balancer]
    LB --> P1[Gateway node 1]
    LB --> P2[Gateway node 2]

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
2. **Gateway to downstream MCP server**
   - resolve the registered downstream credential
   - do not accidentally leak unrelated northbound headers
3. **Gateway to model provider**
   - never reuse the caller's gateway bearer credential as the provider credential
   - apply only the provider credential configured for that provider
4. **Administrator to configuration store**
   - enforce admin scope server-side
   - store secret references instead of model-provider plaintext secrets

The architecture deliberately treats native provider access as broader than logical route access.

## Telemetry and governance boundary

GatewayTelemetryMiddleware sits on the northbound data plane after authentication/authorization and before the mapped MCP/model endpoints. It assigns or propagates a correlation ID, captures request/response telemetry, and optionally invokes GuardrailService.

Outbound MCP and model HttpClient instances include GatewayTelemetryHttpHandler, so southbound provider traffic uses the same structured telemetry envelope. GatewayService and the model routing services add semantic events for MCP operations, model selection, intent classification, failover, and guardrail decisions.

Telemetry is sink-neutral. The application emits through ILogger and does not persist telemetry in the application database.

When output guardrails are enabled, governed responses are buffered until the evaluator returns an allow/block decision. This deliberately trades streaming immediacy for the guarantee that blocked output is not partially released. The guardrail evaluator is invoked internally through ModelRouterService.ChatInternalAsync, bypassing public model routing and avoiding recursive guardrail evaluation.

Credential-bearing headers and credential-shaped JSON properties are redacted before telemetry emission or guardrail evaluation. Additional redaction names are configuration-driven.

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
