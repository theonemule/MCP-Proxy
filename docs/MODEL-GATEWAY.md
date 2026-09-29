# Model Gateway and Intelligent Routing

The model gateway exposes an OpenAI-compatible northbound API while hiding provider-specific endpoints, private model identifiers, authentication details, and routing policy.

The preferred base URL is:

~~~text
https://<proxy-host>/v1
~~~

## Why /v1 is the primary model surface

The project also contains a provider-neutral /models/chat contract. That endpoint remains for compatibility, but new integrations should use /v1.

Using the OpenAI wire contract means existing clients can usually change only:

- base URL
- credential
- model or public route name

The gateway then routes or translates the request downstream.

## Mental model

~~~text
client
  |
  | OpenAI-compatible request
  v
public ModelRoute
  |
  +--> primary provider/model
  +--> additional provider/model target
  +--> additional provider/model target
~~~

A ModelRoute is the public identity seen by clients.

A ModelRouteTarget is an additional downstream candidate behind that public identity.

The provider and downstream model stored on ModelRoute itself remain the implicit primary target.

## Two supported client behaviors

### Caller chooses a public model route

~~~json
{
  "model": "gpt-5.6",
  "messages": [
    {
      "role": "user",
      "content": "Explain this algorithm."
    }
  ]
}
~~~

The gateway performs an exact lookup of the public route gpt-5.6.

It does not consult the configured default route.

It then evaluates the candidates configured **inside that named route**.

If the route has only one backend, this behaves like fixed-model routing.

If the route has multiple targets, the caller chooses the logical route while the router chooses the backend candidate.

### Caller lets the gateway choose the logical route

Mark one route as IsDefault.

A raw OpenAI-compatible request may then omit model:

~~~json
{
  "messages": [
    {
      "role": "user",
      "content": "Analyze this code and identify concurrency problems."
    }
  ]
}
~~~

The gateway resolves the default route and performs intelligent target selection within it.

## SDKs that require a model string

Create a normal route named something such as:

~~~text
router
~~~

or:

~~~text
auto
~~~

Then configure the SDK to send that public name.

These are conventions only. The implementation does not treat router or auto specially.

## Exact backend versus logical route

If a caller must force one exact backend provider/model pair, create a public route that contains only that pair.

Do not attach multiple route targets to a public name that is supposed to mean one exact backend.

This distinction is intentional:

~~~text
model in client request
    -> public route identity
        -> backend target selection
~~~

## Provider types

### OpenAiCompatible

Use this whenever the downstream service exposes the OpenAI API operation you need.

Typical examples include:

- OpenAI
- Microsoft Foundry and Azure OpenAI-compatible v1 endpoints
- Hugging Face OpenAI-compatible inference endpoints
- vLLM
- Open WebUI
- Ollama's OpenAI-compatible /v1 endpoint

The gateway largely preserves incoming JSON.

It replaces the public model name with the selected private downstream model identifier and adds the configured provider credential.

Responses are rewritten so model identity remains the public route name.

### Ollama

Use Ollama for native /api/chat.

The gateway translates OpenAI Chat Completions messages and supported controls into Ollama's native request format and converts the response back.

Native Ollama is a chat adapter.

### AwsBedrock

Use AwsBedrock when the gateway should call native Bedrock Converse or ConverseStream or sign requests with SigV4.

The adapter handles:

- message conversion
- system and developer instructions
- tool definitions
- tool results
- tool choice
- common inference settings
- streaming EventStream decoding

When Bedrock exposes a suitable OpenAI-compatible operation and authentication can be represented as a normal provider credential, OpenAiCompatible may be simpler.

### GenericHttp

Use GenericHttp for native pass-through only.

It does not participate in unified chat or generic OpenAI routing.

## OpenAI endpoints

~~~text
GET  /v1/models
GET  /v1/models/{model}
POST /v1/chat/completions
POST /v1/{**operation}
~~~

The catch-all POST route supports JSON operations such as:

~~~text
/v1/responses
/v1/embeddings
~~~

when the chosen downstream provider supports the operation.

## Authentication

OpenAI clients normally send:

~~~text
Authorization: Bearer <api-key>
~~~

The proxy keeps that wire format.

Bearer values are classified as:

~~~text
mcp_...              -> gateway API-key authentication
other non-empty data -> JWT/OIDC bearer authentication
~~~

This lets an OIDC access token be placed into an OpenAI SDK's api_key field.

Python example:

~~~python
from openai import OpenAI

client = OpenAI(
    base_url="https://gateway.example/v1",
    api_key="mcp_<prefix>.<secret>",
)
~~~

or:

~~~python
client = OpenAI(
    base_url="https://gateway.example/v1",
    api_key="<oidc-access-token>",
)
~~~

The OIDC token must still pass the configured JWT/OIDC validation path.

Opaque OAuth tokens are not introspected automatically.

## Model discovery

GET /v1/models returns only public routes visible to the caller.

The client never needs the private downstream model ID.

Model visibility is part of authorization, not merely a presentation feature.

## Routing metadata

Each primary route and route target can carry:

| Field | Meaning |
| --- | --- |
| priority | Administrative preference after suitability. Lower is preferred. |
| weight | Relative tie distribution among candidates tied on suitability and priority. |
| reasoningLevel | None, Low, Medium, or High. |
| maxContextTokens | Declared maximum total context. Zero means unknown or not declared. |
| maxOutputTokens | Declared maximum output. Zero means unknown or not declared. |
| supportsTools | true, false, or null for unknown. |
| supportsVision | true, false, or null for unknown. |
| supportsJsonSchema | true, false, or null for unknown. |
| costTier | Relative 1 through 5 cost tier. Zero means unknown. |
| latencyTier | Relative 1 through 5 latency tier. Zero means unknown. |
| specialties | Comma-separated hints such as coding,math. |
| enabled | Whether the route or target can be selected. |

A route additionally has:

| Field | Meaning |
| --- | --- |
| isDefault | Use this logical route when an OpenAI-compatible request omits model. |

## Requirement inference

ModelRoutingRequirements.Infer examines the incoming OpenAI-compatible JSON.

It currently detects:

- explicit reasoning effort
- text suggesting reasoning complexity
- approximate input size
- requested output size
- function or tool use
- image or vision inputs
- structured JSON or JSON-Schema output
- coarse specialty

The selector is heuristic. It does not invoke another model to classify the request.

That avoids recursive inference cost, latency, and failure dependencies.

## Reasoning inference

Explicit reasoning fields are honored when present.

Otherwise content heuristics are used.

Examples of strong reasoning signals include:

- formal proof
- root-cause analysis
- threat modeling
- deep analysis
- complex architecture
- multi-step reasoning
- algorithm optimization
- race-condition analysis

Normal analysis, debugging, architecture, comparison, planning, security, implementation, and similar requests generally map to medium reasoning.

Straightforward work generally maps to low.

## Specialty inference

The current coarse specialties are:

~~~text
general
coding
math
vision
creative
summarization
~~~

Specialties are hints, not hard model classes.

A target can advertise several:

~~~text
coding,math,general
~~~

## Hard filters

A candidate is removed when the gateway knows it cannot satisfy the request.

Examples include:

- request requires tools and supportsTools is false
- request contains an image and supportsVision is false
- request requires JSON Schema and supportsJsonSchema is false
- estimated input plus requested output exceeds a declared context maximum
- requested output exceeds a declared output maximum
- provider kind cannot serve the requested operation
- provider or target is disabled

Unknown capability values are not hard failures.

## Suitability scoring

After hard filters, the router computes a suitability score.

The strongest soft penalty is insufficient reasoning capability.

A model that is more capable than necessary gets a smaller efficiency penalty rather than being rejected.

Unknown required capability metadata gets a penalty.

Specialty matches improve suitability.

Cost and latency matter more for simple work than for high-reasoning work.

## Priority and weight

Priority and weight are deliberately secondary:

~~~text
task and capability fit
    before
administrative priority
    before
weight
~~~

Priority is compared when candidates have the same suitability score.

Weight is used only when suitability and priority are both tied.

This prevents a weak model from winning a complex task merely because it has a lower numeric priority.

## Failover

After ranking, the OpenAI compatibility layer can attempt the next candidate for safe transient failures.

Retry and failover are intended for conditions such as:

- 408
- 409
- 425
- 429
- 5xx
- network failures
- gateway-side timeout conditions not caused by caller cancellation
- malformed or invalid downstream payloads

Provider request and authorization failures are not treated as normal cross-provider retry signals.

## Circuit breaker

Routing health is tracked per provider/model target.

Current defaults are:

~~~text
failure threshold: 3 consecutive failures
cooldown: 30 seconds
~~~

A target with an open circuit is removed from normal selection.

If every candidate is unhealthy, the selector can still fall back to the available candidate set instead of making the route permanently unreachable.

Circuit state is process-local.

## Streaming

Chat streaming uses OpenAI SSE:

~~~text
data: {chat.completion.chunk...}

data: [DONE]
~~~

The gateway may fail over only before the first northbound chunk or event is emitted.

After streaming starts, changing providers would create an invalid mixed stream.

## Authorization

Two model permission scopes exist.

### Route scope

A route permission grants one logical public route.

This is the preferred least-privilege grant for normal /v1 inference.

### Provider scope

A provider permission is broader.

It can authorize routes associated with that provider and native provider pass-through.

When a logical route has a secondary target on another provider, provider-only access does not silently grant that second provider.

Candidate authorization is checked through CanAccessModelRouteTargetAsync.

## Administration UI versus API

The browser administration UI supports:

- providers
- public routes
- default-route selection
- routing targets
- priority
- weight
- model permissions

The complete capability profile is configurable through the administration API.

Basic UI edits preserve capability fields previously configured through the API.

## Create a provider

~~~http
POST /admin/model-providers
Content-Type: application/json
~~~

~~~json
{
  "name": "Foundry",
  "slug": "foundry",
  "kind": 0,
  "baseEndpoint": "https://example.openai.azure.com/openai/v1",
  "chatPath": null,
  "enabled": true,
  "credentialReference": "env:FOUNDRY_API_KEY",
  "credentialHeader": "Authorization",
  "credentialPrefix": "Bearer "
}
~~~

Provider kind values:

~~~text
0 OpenAiCompatible
1 Ollama
2 AwsBedrock
3 GenericHttp
~~~

## Create a public route

~~~http
POST /admin/model-routes
Content-Type: application/json
~~~

~~~json
{
  "providerId": "<provider-guid>",
  "publicName": "router",
  "downstreamModel": "gpt-5.6",
  "enabled": true,
  "isDefault": true,
  "priority": 0,
  "weight": 100,
  "reasoningLevel": 3,
  "maxContextTokens": 200000,
  "maxOutputTokens": 32000,
  "supportsTools": true,
  "supportsVision": true,
  "supportsJsonSchema": true,
  "costTier": 4,
  "latencyTier": 3,
  "specialties": "general,coding,math"
}
~~~

Reasoning enum values:

~~~text
0 None
1 Low
2 Medium
3 High
~~~

## Add a routing target

~~~http
POST /admin/model-route-targets
Content-Type: application/json
~~~

~~~json
{
  "modelRouteId": "<route-guid>",
  "providerId": "<provider-guid>",
  "downstreamModel": "qwen3:14b",
  "priority": 10,
  "weight": 100,
  "enabled": true,
  "reasoningLevel": 2,
  "maxContextTokens": 65536,
  "maxOutputTokens": 8192,
  "supportsTools": true,
  "supportsVision": false,
  "supportsJsonSchema": true,
  "costTier": 1,
  "latencyTier": 1,
  "specialties": "coding,summarization"
}
~~~

## Grant route access

~~~json
{
  "roleId": "<role-guid>",
  "scope": 1,
  "providerId": null,
  "modelRouteId": "<route-guid>"
}
~~~

## Grant provider access

~~~json
{
  "roleId": "<role-guid>",
  "scope": 0,
  "providerId": "<provider-guid>",
  "modelRouteId": null
}
~~~

Model permission enum values are:

~~~text
0 Provider
1 Route
~~~

## Chat Completions with a named route

~~~bash
curl https://gateway.example/v1/chat/completions   -H 'Authorization: Bearer mcp_<prefix>.<secret>'   -H 'Content-Type: application/json'   -d '{
    "model": "router",
    "messages": [
      {"role": "user", "content": "Review this C# service for race conditions."}
    ]
  }'
~~~

## Chat Completions with the default route

~~~bash
curl https://gateway.example/v1/chat/completions   -H 'Authorization: Bearer mcp_<prefix>.<secret>'   -H 'Content-Type: application/json'   -d '{
    "messages": [
      {"role": "user", "content": "Review this C# service for race conditions."}
    ]
  }'
~~~

## Error behavior

The /v1 surface uses an OpenAI-style error envelope:

~~~json
{
  "error": {
    "message": "...",
    "type": "invalid_request_error",
    "param": "model",
    "code": "model_not_found"
  }
}
~~~

Unknown and unauthorized routes intentionally have similar not-found behavior.

Provider implementation details are logged server-side instead of being blindly reflected to callers.

## Provider setup notes

### Microsoft Foundry and Azure OpenAI-compatible v1

Register as OpenAiCompatible when the target exposes the OpenAI v1 operation you need.

Use the provider base URL at its OpenAI v1 root.

### Hugging Face

When using an OpenAI-compatible Hugging Face inference endpoint, register it as OpenAiCompatible and set the downstream route model to the provider's model identifier.

### Local Ollama

Two approaches are supported:

- register Ollama's /v1 compatibility endpoint as OpenAiCompatible
- use Ollama for the native /api/chat adapter

### AWS Bedrock

Use AwsBedrock when the gateway should use native Converse/ConverseStream or SigV4.

Bedrock region resolution uses:

- provider AwsRegion
- AWS_REGION
- AWS_DEFAULT_REGION

SigV4 credentials use configured env references or:

- AWS_ACCESS_KEY_ID
- AWS_SECRET_ACCESS_KEY
- AWS_SESSION_TOKEN

## Current limitations

- routing requirement inference is heuristic rather than model-based
- model health and weighted-selection state are not distributed between proxy nodes
- the browser admin UI does not expose every capability metadata field
- the legacy /models/chat surface does not implement the full request-aware routing behavior of /v1
- the project uses additive schema compatibility logic instead of a mature versioned migration pipeline
