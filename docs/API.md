# HTTP API Reference

This document is a route-level reference for the current repository. For usage examples, see [../README.md](../README.md). For model-routing semantics, see [MODEL-GATEWAY.md](MODEL-GATEWAY.md).

## Authentication conventions

When authentication is enabled, most proxy routes require an authenticated principal.

Common API credential presentations are:

~~~text
X-Api-Key: mcp_<prefix>.<secret>
Authorization: Bearer <JWT-or-OIDC-token>
Authorization: Bearer mcp_<prefix>.<secret>
~~~

The last form is primarily for OpenAI-compatible clients that always place the configured API credential in the Bearer slot.

The administration browser also supports cookie authentication.

## Authentication and session endpoints

~~~text
GET  /auth/config
GET  /account/login
POST /account/logout
GET  /auth/session
POST /auth/login
POST /auth/token-exchange
POST /auth/exchange
~~~

/auth/token-exchange and /auth/exchange are aliases.

## Administration API

All administration routes are under:

~~~text
/admin
~~~

Administration scopes are enforced server-side.

### Roles

~~~text
GET    /admin/roles
POST   /admin/roles
PUT    /admin/roles/{id}
DELETE /admin/roles/{id}

GET    /admin/roles/{id}/permissions
POST   /admin/roles/{id}/permissions
~~~

### MCP permissions

~~~text
GET    /admin/permissions
DELETE /admin/permissions/{id}
~~~

Role permission creation is performed through:

~~~text
POST /admin/roles/{id}/permissions
~~~

### Users

~~~text
GET    /admin/users
POST   /admin/users
DELETE /admin/users/{id}

POST   /admin/users/{id}/roles
DELETE /admin/users/{id}/roles/{roleId}
~~~

### API keys

~~~text
GET    /admin/apikeys
POST   /admin/apikeys
DELETE /admin/apikeys/{id}

POST   /admin/apikeys/{id}/roles
DELETE /admin/apikeys/{id}/roles/{roleId}
~~~

### Claim-role mappings

~~~text
GET    /admin/claim-mappings
POST   /admin/claim-mappings
DELETE /admin/claim-mappings/{id}
~~~

### Downstream MCP servers

~~~text
GET    /admin/servers
POST   /admin/servers
PUT    /admin/servers/{id}
DELETE /admin/servers/{id}

POST   /admin/servers/{id}/sync
GET    /admin/servers/{id}/catalog
~~~

## Model administration API

### Model providers

~~~text
GET    /admin/model-providers
POST   /admin/model-providers
PUT    /admin/model-providers/{id}
DELETE /admin/model-providers/{id}
~~~

Provider request fields are:

~~~text
name
slug
kind
baseEndpoint
chatPath
enabled
credentialReference
credentialHeader
credentialPrefix
awsRegion
awsAccessKeyReference
awsSecretKeyReference
awsSessionTokenReference
~~~

ModelProviderKind values are:

~~~text
0 OpenAiCompatible
1 Ollama
2 AwsBedrock
3 GenericHttp
~~~

### Model routes

~~~text
GET    /admin/model-routes
POST   /admin/model-routes
PUT    /admin/model-routes/{id}
DELETE /admin/model-routes/{id}
~~~

Route request fields are:

~~~text
providerId
publicName
downstreamModel
enabled
isDefault
priority
weight
reasoningLevel
maxContextTokens
maxOutputTokens
supportsTools
supportsVision
supportsJsonSchema
costTier
latencyTier
specialties
~~~

ModelReasoningLevel values are:

~~~text
0 None
1 Low
2 Medium
3 High
~~~

When a route is made default through the administration API, the API clears the default flag from the other routes.

### Model route targets

~~~text
GET    /admin/model-route-targets
POST   /admin/model-route-targets
PUT    /admin/model-route-targets/{id}
DELETE /admin/model-route-targets/{id}
~~~

Target request fields are:

~~~text
modelRouteId
providerId
downstreamModel
priority
weight
enabled
reasoningLevel
maxContextTokens
maxOutputTokens
supportsTools
supportsVision
supportsJsonSchema
costTier
latencyTier
specialties
~~~

### Model permissions

~~~text
GET    /admin/model-permissions
POST   /admin/model-permissions
DELETE /admin/model-permissions/{id}
~~~

Permission fields are:

~~~text
roleId
scope
providerId
modelRouteId
~~~

ModelPermissionScope values are:

~~~text
0 Provider
1 Route
~~~

Provider, route, and route-target administration require server-admin scope. Model-permission administration requires user-admin scope. Global admin satisfies both.

## MCP protocol endpoints

Combined MCP gateway:

~~~text
/mcp
~~~

One-server scoped gateway:

~~~text
/servers/{serverScope}/mcp
~~~

The MCP SDK handles protocol method dispatch.

Gateway handlers implement:

- tools/list
- tools/call
- resources/list
- resources/read
- prompts/list
- prompts/get

The server uses Streamable HTTP with stateless northbound session mode.

## OpenAI-compatible model API

Base path:

~~~text
/v1
~~~

This is the recommended model API for new integrations.

### List models

~~~text
GET /v1/models
~~~

The result uses OpenAI model-list shape and contains only public routes the caller is authorized to see.

### Get one model

~~~text
GET /v1/models/{model}
~~~

The model identifier is the public ModelRoute.PublicName, not the private downstream model ID.

Unknown and unauthorized routes use equivalent not-found-style behavior.

### Chat Completions

~~~text
POST /v1/chat/completions
~~~

The request follows OpenAI Chat Completions JSON.

model can be:

- an explicitly selected public route
- omitted when an enabled default route is configured and authorized

If a model is explicitly supplied, that route takes precedence over the default route.

A named public route may itself contain several downstream routing targets.

With:

~~~json
{
  "stream": true
}
~~~

the response uses Server-Sent Events and OpenAI chat-completion chunks.

### Generic OpenAI POST operations

~~~text
POST /v1/{**operation}
~~~

Examples include:

~~~text
POST /v1/responses
POST /v1/embeddings
~~~

The selected provider must support the requested operation.

For OpenAI-compatible downstream providers, the gateway forwards the JSON contract while replacing the public model with the selected private downstream model.

For supported generic streaming operations, SSE event names are preserved and model identities inside JSON data payloads are rewritten back to the public alias.

## OpenAI-compatible error envelope

The /v1 surface returns errors in an OpenAI-compatible shape:

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

Provider implementation details are not treated as a stable northbound error contract.

## Legacy normalized model API

These endpoints remain for compatibility:

~~~text
GET  /models/
POST /models/chat
POST /models/chat/stream
~~~

The normalized chat request is:

~~~json
{
  "model": "public-model",
  "prompt": "Hello",
  "systemPrompt": "Optional system instruction",
  "parameters": {
    "temperature": 0.2
  },
  "stream": false
}
~~~

/models/chat can also stream when stream is true.

The normalized stream emits named SSE events such as start, delta, usage, and done.

This surface resolves a named route directly. New integrations should use /v1 for OpenAI compatibility and request-aware intelligent routing.

## Native model-provider proxy

~~~text
/models/native/{providerScope}
/models/native/{providerScope}/{**path}
~~~

The endpoint accepts these HTTP methods:

~~~text
GET
POST
PUT
PATCH
DELETE
HEAD
OPTIONS
~~~

This surface preserves provider-native semantics rather than translating to OpenAI.

Provider-level authorization is required.

Northbound gateway credentials, cookies, forwarding headers, and hop-by-hop headers are not reused as downstream provider credentials.

## Optional browser-client API

The separate McpClient application exposes the following routes.

### Chat

~~~text
POST /api/chat
POST /api/chat/{conversationId}/reset
~~~

### MCP discovery and direct resource/prompt actions

~~~text
GET  /api/servers
POST /api/servers/{serverName}/resources/read
POST /api/servers/{serverName}/prompts/get
~~~

### MCP server configuration

~~~text
GET    /api/server-configurations
POST   /api/server-configurations
PUT    /api/server-configurations/{id}
DELETE /api/server-configurations/{id}
~~~

### LLM configuration and discovery

~~~text
GET  /api/llm-settings
PUT  /api/llm-settings
POST /api/llm-models
~~~

### Client session

~~~text
GET /api/session
~~~

When OIDC is enabled in McpClient, its /api routes use the client cookie/OIDC session.

## API compatibility guidance

Use:

- /mcp for MCP clients
- /v1 for model clients
- /admin for product administration
- /models/native only when provider-native behavior is intentionally required
- /models/chat only for compatibility with integrations already built against the older normalized contract

Do not build new clients around private downstream model IDs or provider endpoints if a public model route can represent the integration.
