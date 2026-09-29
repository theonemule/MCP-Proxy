# MCP Proxy

> **Beta:** the project is usable, but interfaces and operational behavior may still change. Issues and pull requests are welcome.

MCP Proxy is a .NET 10 gateway for two related workloads:

1. **Model Context Protocol aggregation and governance**. It exposes tools, resources, and prompts from multiple downstream MCP servers through one protected endpoint.
2. **AI model gateway and intelligent routing**. It exposes an OpenAI-compatible /v1 API in front of OpenAI-compatible providers, Ollama, AWS Bedrock, and native HTTP providers.

The repository also contains an optional browser client for OIDC sign-in, AI chat, and MCP tool use.

## Documentation

- [README.md](README.md) explains installation, configuration, administration, and normal use.
- [README.DEVELOPER.md](README.DEVELOPER.md) explains implementation, testing, and extension points.
- [Architecture](docs/ARCHITECTURE.md) describes components, request flows, data, security boundaries, and deployment topology.
- [MCP Gateway](docs/MCP-GATEWAY.md) covers downstream MCP registration, namespacing, synchronization, credentials, and capability permissions.
- [Model Gateway and Intelligent Routing](docs/MODEL-GATEWAY.md) covers provider integration, direct model selection, default routing, capability metadata, failover, and authorization.
- [Optional Browser Client](docs/CLIENT.md) explains the interactive OIDC, LLM, and MCP client.
- [HTTP API Reference](docs/API.md) lists proxy, MCP, model, administration, and browser-client endpoints.
- [Operations Guide](docs/OPERATIONS.md) covers configuration, Docker, databases, Redis, secrets, OIDC, scaling, logging, backup, and troubleshooting.

## Repository layout

- src/McpProxy is the shared gateway, administration application, MCP proxy, model gateway, and intelligent router.
- src/McpClient is the optional OIDC browser client and AI/MCP chat application.
- tests/McpProxy.Tests contains the automated test suite.
- McpProxy.slnx contains both applications and the tests.

## Northbound interfaces

| Purpose | Endpoint |
| --- | --- |
| Combined MCP gateway | /mcp |
| One MCP server by namespace | /servers/{serverScope}/mcp |
| OpenAI-compatible model API | /v1 |
| Legacy normalized model API | /models |
| Native model-provider pass-through | /models/native/{providerScope}/... |
| Administration API | /admin/... |
| Administration UI | / |

For new model integrations, use **/v1**. The /models/chat contract remains for backward compatibility.

## Quick start

### Requirements

You need the .NET 10 SDK. Docker is optional. OIDC is optional for local development but is the normal external identity mode.

### Build and test

~~~bash
dotnet restore McpProxy.slnx
dotnet build McpProxy.slnx
dotnet test McpProxy.slnx
~~~

### Start the proxy locally

The default local database is SQLite.

~~~bash
export Bootstrap__AdminUsername=admin
export Bootstrap__AdminPassword='replace-this'
dotnet run --project src/McpProxy --launch-profile http
~~~

PowerShell:

~~~powershell
$env:Bootstrap__AdminUsername = "admin"
$env:Bootstrap__AdminPassword = "replace-this"
dotnet run --project src/McpProxy --launch-profile http
~~~

The default development URL is:

~~~text
http://localhost:5105
~~~

The bootstrap account is created only when the database has no users and no roles.

### Start the optional browser client

~~~bash
dotnet run --project src/McpClient --launch-profile http
~~~

The default development URL is:

~~~text
http://localhost:5256
~~~

The browser client is not required to use the proxy.

## First MCP workflow

1. Sign in to the proxy administration UI.
2. Create or select a role.
3. Register a downstream MCP server.
4. Give it a stable namespace prefix such as docs or github.
5. Synchronize the server so the proxy discovers its tools, resources, and prompts.
6. Grant the role access to the whole server or selected capabilities.
7. Assign the role to a user, API key, or OIDC claim mapping.
8. Connect an MCP client to http://localhost:5105/mcp.

The proxy namespaces capabilities to avoid collisions. A downstream tool named search on a server with namespace docs appears northbound as:

~~~text
docs__search
~~~

The proxy also exposes per-server MCP endpoints:

~~~text
/servers/{serverScope}/mcp
~~~

They use the same authentication and authorization rules while narrowing discovery and invocation to one registered server.

## MCP protocol behavior

The proxy uses ModelContextProtocol.AspNetCore 2.2.0 and configures northbound Streamable HTTP as stateless. The implementation is intended for current MCP 2.x protocol behavior while retaining SDK-supported compatibility with initialize-era clients.

Downstream MCP connections also use the MCP 2.x client stack and are not pinned to one older protocol revision.

Authorization is independent of protocol negotiation. Tools, resources, and prompts are filtered for the authenticated caller on every request.

## Model gateway quick start

The preferred model API is OpenAI compatible:

~~~text
GET  /v1/models
GET  /v1/models/{model}
POST /v1/chat/completions
POST /v1/responses
POST /v1/{other-json-operation}
~~~

A public **model route** is the model name clients see. It may map to one downstream model or to several candidate models across providers.

### Direct model selection

If the caller supplies model, that public route is selected explicitly:

~~~json
{
  "model": "gpt-5.6",
  "messages": [
    {
      "role": "user",
      "content": "Review this architecture."
    }
  ]
}
~~~

An explicitly supplied model always takes precedence over the configured default router.

If you want a public name to mean one exact backend model, configure that route with only its primary provider/model and no additional routing targets.

### Intelligent and default routing

A model route can be marked as the default intelligent router. Raw OpenAI-compatible requests may then omit model:

~~~json
{
  "messages": [
    {
      "role": "user",
      "content": "Review this architecture for concurrency problems."
    }
  ]
}
~~~

The gateway resolves the default route, examines the request, and chooses the best authorized candidate behind that route.

Some OpenAI SDKs require a model string. In that case, create a normal public route with a name such as router or auto and send that name:

~~~json
{
  "model": "router",
  "messages": [
    {
      "role": "user",
      "content": "Review this architecture for concurrency problems."
    }
  ]
}
~~~

router and auto are examples, not built-in magic names. They work only if an administrator creates routes with those public names.

### How intelligent routing works

Each primary route and additional routing target can describe:

- reasoning capability from none through high
- maximum context and output tokens
- tool/function-calling support
- vision/image-input support
- JSON or JSON-Schema structured-output support
- specialties such as coding, math, vision, creative, and summarization
- relative cost tier
- relative latency tier
- administrative priority
- weight

For OpenAI-compatible requests the selector:

1. Removes disabled providers and targets.
2. Removes provider types that cannot serve the requested operation.
3. Hard-filters known capability, context, and output-limit mismatches.
4. Scores remaining candidates for reasoning fit, specialty fit, unknown-capability risk, cost, and latency.
5. Uses administrative priority to refine candidates with the same suitability score.
6. Uses smooth weighted selection only when suitability and priority are tied.
7. Retries safe transient failures against the next ranked candidate.
8. Temporarily removes repeatedly failing targets with a circuit breaker.

This is primarily a **model selector**, not a load balancer.

See [docs/MODEL-GATEWAY.md](docs/MODEL-GATEWAY.md) for the full routing contract.

## OpenAI SDK example

A gateway API key is accepted in the normal OpenAI bearer slot.

~~~python
from openai import OpenAI

client = OpenAI(
    base_url="https://gateway.example/v1",
    api_key="mcp_<prefix>.<secret>",
)

response = client.chat.completions.create(
    model="router",
    messages=[
        {"role": "user", "content": "Explain this function and identify race conditions."}
    ],
)

print(response.choices[0].message.content)
~~~

The same /v1 surface can authenticate with an OIDC/JWT access token placed in the OpenAI SDK api_key field. Gateway-generated keys begin with mcp_. Any other non-empty bearer value is handled by JWT/OIDC bearer authentication.

This preserves the normal OpenAI wire format:

~~~text
Authorization: Bearer <credential>
~~~

Opaque OAuth tokens that cannot be validated by the configured JWT/OIDC handler are rejected.

## Model providers

The current provider kinds are:

| Provider kind | Intended use |
| --- | --- |
| OpenAiCompatible | OpenAI, Microsoft Foundry or Azure OpenAI v1 endpoints, Hugging Face OpenAI-compatible endpoints, vLLM, Open WebUI, Ollama /v1, and similar APIs |
| Ollama | Native Ollama /api/chat translated to and from OpenAI Chat Completions |
| AwsBedrock | Native Bedrock Converse and ConverseStream, including SigV4 |
| GenericHttp | Native pass-through only |

For OpenAI-compatible providers, the gateway preserves the OpenAI request shape, substitutes the private downstream model identifier, applies the configured provider credential, and rewrites model identity in the response back to the public route name.

## Provider credentials

Model-provider secrets use environment references:

~~~text
env:MY_PROVIDER_API_KEY
~~~

A normal bearer provider typically uses:

~~~text
CredentialReference = env:MY_PROVIDER_API_KEY
CredentialHeader    = Authorization
CredentialPrefix    = Bearer
~~~

The secret value is resolved at runtime and is not stored in the model-provider row.

AWS Bedrock can use a configured bearer credential, AWS_BEARER_TOKEN_BEDROCK, or SigV4 using configured environment references or standard AWS environment variables.

## Authentication

When authentication is enabled, the proxy supports:

- browser cookie authentication
- locally issued JWTs in Internal mode
- external JWT/OIDC bearer tokens in Oidc mode
- proxy API keys in the configured API-key header
- proxy API keys in Authorization: Bearer for OpenAI-compatible clients

The default API-key header is:

~~~text
X-Api-Key
~~~

The OpenAI /v1 endpoints use an API-oriented authentication policy so invalid API credentials return HTTP 401 instead of redirecting to the browser login flow.

## Authorization

Roles are the unit of authorization.

Role administration flags are:

- global administrator
- user administrator
- server administrator

MCP permissions can cover:

- an entire MCP server
- one tool
- one resource
- one prompt

Model permissions can cover:

- one model route
- one model provider

A route permission grants use of that logical public model. Provider permissions are broader and also control native provider access. The gateway evaluates target authorization before sending inference traffic.

OIDC authentication alone does not automatically grant proxy permissions. External claims become proxy roles only through explicit claim-role mappings.

## API keys

Proxy API keys are generated once and displayed once. The database stores the public prefix and a hash of the secret, not the plaintext key.

A generated key has the form:

~~~text
mcp_<prefix>.<secret>
~~~

Assign roles to the API key the same way you assign roles to a user.

## OIDC and Microsoft Entra ID

For OIDC mode, configure at minimum:

~~~json
{
  "Auth": {
    "Enabled": true,
    "Mode": "Oidc",
    "Authority": "https://login.microsoftonline.com/<tenant>/v2.0",
    "Audience": "api://<proxy-app-id>",
    "ClientId": "<proxy-app-id>",
    "ClientSecret": "env:MCP_PROXY_OIDC_CLIENT_SECRET",
    "RequireHttpsMetadata": true
  }
}
~~~

For Entra ID:

1. Register the proxy application.
2. Expose an API scope for clients that need delegated access.
3. Configure the proxy redirect URI ending in /signin-oidc.
4. Create the client secret and store the **secret value**, not the secret identifier.
5. Optionally define Entra app roles.
6. Create matching proxy roles and claim-role mappings for incoming roles values.
7. Register the browser client separately if you use src/McpClient.

The proxy deliberately does not interpret an arbitrary incoming roles claim as a proxy administrator role without an explicit database mapping.

## Administration UI

The browser administration UI manages:

- roles
- local users
- API keys
- claim mappings
- MCP permissions
- downstream MCP servers
- model providers
- model routes
- model routing targets
- model permissions

For model routing, the UI exposes the common provider, route, target, default-route, priority, and weight workflow.

The full intelligent-routing capability profile is available through the administration API. The UI preserves that richer metadata when basic route or target properties are edited.

## Databases

Supported relational databases are:

- SQLite
- PostgreSQL
- SQL Server

SQLite is the default and is appropriate for development and simple single-node deployments.

PostgreSQL or SQL Server should be used when multiple proxy instances share state.

The project currently initializes with EnsureCreatedAsync. ModelSchemaUpgrade adds the model-gateway tables and additive routing columns for existing SQLite, PostgreSQL, and SQL Server databases. This is a compatibility bridge, not a mature versioned EF migration pipeline.

## Cache and multi-node coordination

The default cache mode is process-local memory.

Redis can be enabled for distributed cache registration and MCP catalog invalidation:

~~~json
{
  "Cache": {
    "Enabled": true,
    "Provider": "Redis",
    "ConnectionString": "redis:6379"
  }
}
~~~

MCP catalog entries remain process-local. Redis publishes invalidation notifications so other nodes refresh their local catalogs.

Model routing health and smooth weighted-selection state are also process-local. Multiple proxy nodes do not currently share model circuit-breaker state.

## Docker Compose

The repository includes a Compose profile with MCP Proxy, PostgreSQL, and Redis.

Set the required values:

~~~bash
export POSTGRES_PASSWORD='replace-this'
export BOOTSTRAP_ADMIN_PASSWORD='replace-this'
export MCP_PROXY_OIDC_CLIENT_SECRET='replace-this'
docker compose up --build -d
~~~

The proxy is published at:

~~~text
http://localhost:5105
~~~

Useful commands:

~~~bash
docker compose ps
docker compose logs -f proxy
docker compose down
~~~

To destroy PostgreSQL and Redis named volumes too:

~~~bash
docker compose down -v
~~~

Do not use -v unless you intend to delete deployment data.

The browser client is intentionally not included in the Compose profile.

## Core proxy configuration

The primary proxy settings are:

~~~json
{
  "Auth": {
    "Enabled": true,
    "Mode": "Oidc",
    "TokenLifetimeMinutes": 60,
    "Authority": "...",
    "Audience": "...",
    "ClientId": "...",
    "ClientSecret": "env:MCP_PROXY_OIDC_CLIENT_SECRET",
    "RequireHttpsMetadata": true,
    "ApiKeysEnabled": true,
    "ApiKeyHeaderName": "X-Api-Key"
  },
  "Database": {
    "Provider": "Sqlite",
    "ConnectionString": "Data Source=mcp-proxy.db",
    "SqliteConnectionString": "Data Source=mcp-proxy.db",
    "SqlServerConnectionString": "",
    "PostgresConnectionString": ""
  },
  "Cache": {
    "Provider": "Memory",
    "Enabled": false,
    "ConnectionString": "",
    "Database": 0
  },
  "LoggingOptions": {
    "Provider": "Console",
    "MinimumLevel": "Information"
  },
  "Bootstrap": {
    "AdminUsername": "admin",
    "AdminPassword": ""
  }
}
~~~

.NET configuration environment variables use double underscores:

~~~text
Database__Provider=Postgres
Cache__Enabled=true
Auth__Mode=Oidc
~~~

See [docs/OPERATIONS.md](docs/OPERATIONS.md) for deployment guidance.

## Secret handling

Use env:VARIABLE_NAME for application and downstream-provider secret references wherever the configuration contract supports it.

Do not commit:

- OIDC client secrets
- model-provider API keys
- MCP downstream credentials
- database passwords
- Redis passwords
- bootstrap passwords
- stable JWT signing keys

The current secret provider reads environment variables. It is isolated behind an abstraction so a managed vault can be added later.

## Logging

Built-in logging provider choices are:

- Console
- Debug
- EventSource
- None

LoggingOptions.MinimumLevel is parsed as a normal .NET LogLevel.

SyslogHost, SyslogPort, Preset, and ApplicationName exist in the configuration model, but the current startup path does not yet implement a syslog provider.

## Browser client

The optional McpClient application supports:

- OIDC sign-in
- multiple MCP server configurations
- OpenAI-compatible hosted inference
- the local MCP Proxy /v1 endpoint as an inference source
- model discovery
- MCP tools in the LLM tool loop
- resource reading
- prompt rendering

The client has two LLM profiles:

- Hosted
- Proxy

When the active Proxy profile has an explicit credential, that value is sent as the OpenAI bearer credential. When the field is blank, the client reuses the signed-in user's access token.

## Troubleshooting

### The bootstrap administrator does not appear

Bootstrap seeding runs only when both users and roles are empty. Existing databases are not modified.

### OIDC login succeeds but the user cannot administer or use MCP or models

Authentication and authorization are separate. Add a direct role assignment or claim-role mapping with the required proxy role and permissions.

### /v1 says model_not_found

The public model route may not exist, may be disabled, or may not be authorized for the caller. These cases intentionally use similar behavior so hidden model names are not disclosed.

### A request omitting model fails

Mark one enabled model route as the default.

### A caller explicitly names a model but traffic still uses another backend

A public route can contain multiple routing targets. Explicitly naming a route bypasses **default route selection**, but intelligent selection still occurs **inside the named route**. If one exact backend is required, give the route only one target.

### A model route works for chat but not another OpenAI operation

Provider compatibility depends on operation type. Native Ollama supports the chat adapter but not arbitrary OpenAI /v1 operation forwarding.

### A secondary target is never selected

Check provider and target enabled state, request requirements, token limits, permissions, operation compatibility, circuit state, suitability score, priority, and weight.

### Redis is running but model routing differs between nodes

That is expected today. Redis coordinates MCP catalog invalidation. Model routing health and weighted state are process-local.

### A downstream secret does not resolve

Provider secret references must use env:VARIABLE_NAME and the variable must exist in the proxy process.

## Security notes

- Use HTTPS outside local development.
- Prefer route-level model permissions when provider-wide access is unnecessary.
- Do not forward northbound authorization credentials to downstream model providers.
- Do not expose raw provider errors across the gateway boundary.
- Keep OIDC claim mappings explicit and narrow.
- Use a stable signing key across nodes if internally issued JWTs are in use.
- Treat native provider pass-through as broader than a single route grant.
- Back up the relational database before production upgrades.

## License

See [LICENSE](LICENSE).
