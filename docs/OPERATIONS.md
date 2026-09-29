# Operations Guide

This guide covers deployment, configuration, scaling, secrets, backup, and troubleshooting for AI Governance Gateway.

## Runtime requirements

The proxy targets .NET 10.

The repository Dockerfile builds with:

~~~text
mcr.microsoft.com/dotnet/sdk:10.0
~~~

and runs with:

~~~text
mcr.microsoft.com/dotnet/aspnet:10.0
~~~

The container listens on port 8080.

The included Compose profile maps:

~~~text
localhost:5105 -> container:8080
~~~

## Local single-node deployment

The default storage configuration is SQLite with process-local memory coordination.

This is appropriate for development, demos, and simple single-node installations.

Example:

~~~bash
export Bootstrap__AdminUsername=admin
export Bootstrap__AdminPassword='replace-this'
dotnet run --project src/AIGovernanceGateway --launch-profile http
~~~

The normal local URL is:

~~~text
http://localhost:5105
~~~

## Docker Compose deployment

The included Compose profile runs:

- AI Governance Gateway
- PostgreSQL
- Redis

Required environment variables are:

~~~text
POSTGRES_PASSWORD
BOOTSTRAP_ADMIN_PASSWORD
AI_GOVERNANCE_GATEWAY_OIDC_CLIENT_SECRET
~~~

Optional variables include:

~~~text
BOOTSTRAP_ADMIN_USERNAME
AUTH_SIGNING_KEY
~~~

Start:

~~~bash
docker compose up --build -d
~~~

Inspect:

~~~bash
docker compose ps
docker compose logs -f gateway
~~~

Stop:

~~~bash
docker compose down
~~~

Delete PostgreSQL and Redis named volumes too:

~~~bash
docker compose down -v
~~~

The last command destroys deployment data in the Compose-managed volumes.

The optional browser client is not part of the Compose profile.

## Database configuration

The configuration section is:

~~~json
{
  "Database": {
    "Provider": "Sqlite",
    "ConnectionString": "Data Source=ai-governance-gateway.db",
    "SqliteConnectionString": "Data Source=ai-governance-gateway.db",
    "SqlServerConnectionString": "",
    "PostgresConnectionString": ""
  }
}
~~~

Supported providers are:

~~~text
Sqlite
SqlServer
Postgres
~~~

Environment configuration uses normal .NET double-underscore notation:

~~~text
Database__Provider=Postgres
Database__PostgresConnectionString=Host=db;Database=ai_governance_gateway;Username=...;Password=...
~~~

Use PostgreSQL or SQL Server for a multi-node deployment with shared durable state.

## Database initialization

At startup:

1. EF Core EnsureCreatedAsync runs.
2. ModelSchemaUpgrade.EnsureAsync ensures model-gateway tables and additive routing columns.
3. Bootstrap administrator creation is attempted.

Bootstrap user creation occurs only when both the users table and roles table are empty.

Changing Bootstrap__AdminPassword after a database has already been initialized does not reset the existing administrator.

## Schema-upgrade model

The repository does not currently use versioned EF migrations.

ModelSchemaUpgrade performs additive compatibility work for:

- SQLite
- PostgreSQL
- SQL Server

It creates model-gateway tables for older databases and adds newer intelligent-routing columns when missing.

Back up production databases before an application upgrade.

## Redis

Redis is optional.

Enable it with:

~~~text
Cache__Enabled=true
Cache__Provider=Redis
Cache__ConnectionString=redis:6379
~~~

Redis currently provides:

- distributed cache registration
- MCP catalog invalidation pub/sub

The invalidation channel is:

~~~text
ai-governance-gateway:catalog-invalidated
~~~

Redis does not currently distribute:

- model circuit-breaker state
- model weighted-selection state
- browser-client conversation history

## Multi-node deployment checklist

For multiple proxy instances, use:

- shared PostgreSQL or SQL Server
- Redis for MCP catalog invalidation
- the same OIDC configuration on every node
- a stable internal JWT signing key when internal JWTs must work across nodes
- the same required downstream/provider secret environment variables on every node
- an HTTPS reverse proxy or load balancer

Model routing health remains node-local.

Two nodes may temporarily make different backend choices after observing different provider failures.

## Authentication configuration

### Internal mode

Use:

~~~text
Auth__Mode=Internal
~~~

Configure a stable base64 signing key when issued tokens must survive restart or be accepted by several nodes:

~~~text
Auth__SigningKey=<base64-key>
~~~

If no key is configured, the proxy generates one at process startup.

### OIDC mode

Use settings such as:

~~~text
Auth__Mode=Oidc
Auth__Authority=https://...
Auth__Audience=api://...
Auth__ClientId=...
Auth__ClientSecret=env:AI_GOVERNANCE_GATEWAY_OIDC_CLIENT_SECRET
Auth__RequireHttpsMetadata=true
~~~

Keep HTTPS metadata validation enabled outside controlled local development.

## Microsoft Entra ID notes

For Entra deployments, verify:

- the proxy redirect URI ends in /signin-oidc
- the API audience matches tokens intended for the proxy
- the configured client secret is the secret value
- clients have the delegated API scope they need
- expected app-role or group claims are actually emitted when claim mappings depend on them

OIDC authentication does not create proxy permissions automatically.

Use direct local role assignments or ClaimRoleMapping.

## API keys

The default API-key header is:

~~~text
X-Api-Key
~~~

Gateway API keys can also be used in the OpenAI bearer slot:

~~~text
Authorization: Bearer aigw_<prefix>.<secret>
~~~

The complete key is displayed when created. The database later contains only the public prefix and a hash of the secret.

A database backup therefore cannot reconstruct a lost plaintext API-key secret.

## Secret management

The current secret-provider implementation reads environment variables.

Use references such as:

~~~text
env:AI_GOVERNANCE_GATEWAY_OIDC_CLIENT_SECRET
env:FOUNDRY_API_KEY
env:GITHUB_TOKEN
~~~

Do not commit secret values to appsettings files or model-provider records.

ISecretProvider is the extension seam for a future managed secret store.

## Downstream MCP credentials

A registered MCP server can reference an environment-backed credential.

The proxy resolves the secret when it creates a downstream MCP client.

Keep downstream credentials distinct from unrelated northbound client credentials.

Credential headers are validated to avoid unsafe protocol and forwarding headers.

## Model-provider credentials

A model provider supports:

~~~text
CredentialReference
CredentialHeader
CredentialPrefix
~~~

A typical OpenAI-compatible provider uses:

~~~text
CredentialReference=env:PROVIDER_API_KEY
CredentialHeader=Authorization
CredentialPrefix=Bearer
~~~

The database stores the reference, not the secret value.

### AWS Bedrock

Bedrock can use a bearer token or SigV4.

Bearer options include:

- the provider credential reference
- AWS_BEARER_TOKEN_BEDROCK

Region resolution uses:

- provider AwsRegion
- AWS_REGION
- AWS_DEFAULT_REGION

SigV4 credential resolution uses provider-specific environment references or:

- AWS_ACCESS_KEY_ID
- AWS_SECRET_ACCESS_KEY
- AWS_SESSION_TOKEN

## Logging

Configuration example:

~~~json
{
  "LoggingOptions": {
    "Provider": "Console",
    "MinimumLevel": "Information",
    "ApplicationName": "ai-governance-gateway",
    "TelemetryEnabled": true,
    "CaptureRequestBodies": true,
    "CaptureResponseBodies": true,
    "CaptureHeaders": true,
    "MaxPayloadBytes": 1048576,
    "CaptureBinaryBodies": false,
    "AcceptInboundCorrelationId": true,
    "RedactedHeaders": [],
    "RedactedJsonFields": []
  },
  "Guardrails": {
    "Enabled": false,
    "EvaluateInputs": true,
    "EvaluateOutputs": true,
    "Model": "guardrail-model",
    "PolicyPrompt": "Apply the organization's acceptable-use and data-handling policy.",
    "BlockThreshold": 70,
    "MaxEvaluationBytes": 262144,
    "BlockOversizedInputs": true,
    "MaxBufferedResponseBytes": 4194304,
    "BlockOversizedResponses": true,
    "FailureMode": "Allow",
    "InputRefusalStatusCode": 403,
    "OutputRefusalStatusCode": 403,
    "PathPrefixes": ["/mcp", "/servers", "/models", "/v1"]
  }
}
~~~

Supported provider names in the current startup path are:

~~~text
Console
Debug
EventSource
None
~~~

The application clears the default logging providers before selecting the configured provider.

Structured telemetry is sink-neutral and is emitted through ILogger under the AIGovernanceGateway.Telemetry.GatewayTelemetry category. No telemetry table or application database dependency is introduced. A future file, syslog, OpenTelemetry, SIEM, Seq, ELK, Application Insights, or other ILogger provider can consume the same events.

High-value event names include:

- http.request and http.response for northbound traffic
- http.downstream.request and http.downstream.response for provider/MCP traffic
- mcp.tool.*, mcp.resource.*, and mcp.prompt.* for semantic MCP operations
- model.route.selected, model.routing.decision, model.route.failover, and model.route.success
- guardrail.evaluation, guardrail.blocked, and guardrail.error

Request and response bodies are captured up to MaxPayloadBytes. Text and JSON are logged as content, while binary bodies default to metadata-only unless CaptureBinaryBodies is enabled. Authorization/cookie headers and credential-shaped JSON properties are redacted recursively. Add organization-specific names to RedactedHeaders and RedactedJsonFields.

Every data-plane request receives an X-Correlation-ID response header. The gateway reuses a caller-supplied X-Correlation-ID by default so logs across clients, AI Governance Gateway, model providers, and downstream MCP servers can be joined.

Preset, SyslogHost, SyslogPort, and ApplicationName exist in the options model, but the current startup code does not implement a syslog sink.

Never log:

- passwords
- complete gateway API keys
- raw Authorization headers
- OIDC access or ID tokens
- downstream MCP credentials
- model-provider credentials
- database passwords

## Guardrails

Guardrails use an enabled model route as a policy classifier around the proxy data plane. They are disabled by default.

Input evaluation occurs after authentication/authorization middleware and before MCP/model routing. Output evaluation occurs before governed response bytes are released to the caller. Because output policy must be decided first, governed streaming responses are buffered and then released or replaced with a refusal.

The evaluator receives:

- whether it is rating an input or output
- sanitized request/response metadata
- the captured content
- the configured PolicyPrompt
- an instruction to treat the captured content as untrusted data and return only a structured decision

The evaluator returns risk_score, action, reason, and categories. Content is refused when action is block or risk_score is greater than or equal to BlockThreshold.

FailureMode=Allow is fail-open. FailureMode=Block is fail-closed. Use fail-closed when governance is mandatory and evaluator availability is part of the service SLO.

BlockOversizedInputs and BlockOversizedResponses prevent a request from evading policy by placing prohibited content beyond MaxEvaluationBytes. MaxBufferedResponseBytes protects the proxy from unbounded memory use while output governance is enabled.

A refusal returns JSON with error.type=guardrail_refusal, the stage, policy reason, score, categories, evaluation failure state, and correlation ID.

## MCP catalog behavior

MCP catalog state is cached in each proxy process.

Refresh occurs:

- at startup
- periodically
- after an administration-triggered server sync

With Redis enabled, a successful refresh publishes an invalidation message so other nodes refresh their own local copy.

If discovery appears stale, inspect the downstream server enabled state, last synchronization error, proxy logs, and Redis connectivity in multi-node deployments, then trigger a manual sync.

## Model routing health

A target can be temporarily removed after repeated failures.

Current defaults are:

~~~text
3 consecutive failures
30-second cooldown
~~~

This state is in process memory.

A proxy restart resets it.

Different nodes can have different circuit state.

## Model routing operational guidance

For routes intended to be intelligent pools:

- describe capabilities accurately
- set context and output limits when known
- use false only for capabilities known to be unsupported
- leave nullable capabilities unknown when they truly are unknown
- use cost and latency tiers as relative hints
- use priority as policy refinement, not as a replacement for capability metadata
- use weight only when traffic distribution between equivalent candidates is desired

For routes intended to mean one exact model:

- configure one primary target
- do not add additional routing targets

## Default-route guidance

At most one route should be administratively selected as the default.

A request with an explicit model does not use the default route.

A request without model requires an enabled default route that the caller can access.

Some client SDKs require a model value. For those clients, create a public route such as router or auto and configure the SDK with that route name.

## Backup

Back up at least:

- the relational database
- deployment configuration
- environment or external secret-manager configuration
- TLS and reverse-proxy configuration

The relational database contains identity, authorization, MCP registration, and model-routing configuration.

It does not contain environment secret values.

If the optional browser client is used operationally, its local JSON configuration files should be backed up separately.

## Upgrade checklist

Before an upgrade:

1. Back up the relational database.
2. Capture the current environment and configuration.
3. Review documentation and release changes.
4. Run the target build's automated tests.
5. Deploy to a staging or test instance.
6. Verify database initialization and schema compatibility.
7. Verify OIDC and API-key authentication.
8. Verify /mcp discovery and invocation.
9. Verify /v1/models.
10. Verify inference with an explicit public model.
11. Verify inference with model omitted when default routing is used.
12. Verify streaming.
13. Verify a secondary-provider failover path when the deployment relies on it.
14. Roll out additional nodes.

## Production security checklist

- terminate TLS
- restrict administration exposure where practical
- use strong bootstrap credentials
- remove unnecessary broad provider grants
- prefer route-level model permissions
- keep OIDC claim mappings narrow
- use separate provider credentials by environment
- keep database and Redis off the public internet
- back up the database
- use stable signing material across nodes when required
- protect native provider proxy access
- review custom logging for accidental credential disclosure

## Troubleshooting

### Startup reports a missing env secret

The process does not have the environment variable referenced by env:NAME.

Check exact spelling and the container or service environment.

### OIDC browser sign-in works but bearer API requests fail

Interactive cookie login and API bearer-token validation are separate flows.

Confirm the access token is issued for the configured API and is accepted by the JWT bearer path.

### An OpenAI-compatible request is redirected to login

The /v1 surface is designed for API-style 401 behavior.

Confirm the client base URL includes /v1 and is not calling an administration or browser route.

### /v1/models is empty

The authenticated principal has no accessible model routes, or all permitted routes or providers are disabled.

### A request without model reports model_required

No enabled default route is configured.

### An explicit model does not use the default route

That is expected. Explicit public-model selection has precedence.

### An explicit public model still reaches different backend models

That public route has multiple route targets. Remove additional targets if the route must force one backend.

### A secondary route target is never selected

Check:

- provider enabled state
- target enabled state
- request capability requirements
- context and output limits
- target authorization
- operation compatibility
- circuit state
- suitability score
- priority
- weight

### A model route works for chat but not another OpenAI operation

Provider support differs by operation.

Native Ollama participates in the chat adapter but is not the generic OpenAI-operation provider.

### Provider behavior differs between nodes

Model health and weighted-selection state are process-local.

That difference is expected in the current implementation.

### MCP capabilities are missing

Check:

- server enabled state
- namespace
- last sync result
- role permissions
- downstream credential
- catalog refresh
- Redis invalidation in a scaled deployment

### A provider credential works on one node but not another

Environment-backed secrets are per process or container.

Ensure every node has the environment variables referenced by its registered providers and MCP servers.