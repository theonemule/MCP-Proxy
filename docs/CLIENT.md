# Optional Browser Client

McpClient is a separate web application included in the solution.

It is useful for testing or using the gateway interactively, but it is not required for MCP Proxy.

The client combines:

- OIDC sign-in
- an OpenAI-compatible LLM connection
- multiple MCP server connections
- an LLM tool-calling loop
- direct resource reading
- prompt rendering

## Start the client

~~~bash
dotnet run --project src/McpClient --launch-profile http
~~~

Default local URL:

~~~text
http://localhost:5256
~~~

## OIDC configuration

The client uses an Oidc configuration section.

Important fields include:

~~~text
Enabled
Authority
ClientId
ClientSecret
ResponseType
CallbackPath
SignedOutCallbackPath
RequireHttpsMetadata
GetClaimsFromUserInfoEndpoint
UsePkce
NameClaimType
RoleClaimType
Scopes
MetadataAddress
~~~

The normal callback path is:

~~~text
/signin-oidc
~~~

When OIDC is enabled, the client protects its /api routes with the signed-in cookie session.

## LLM profiles

The client supports two model sources:

~~~text
Proxy
Hosted
~~~

Each connection profile contains:

~~~text
Endpoint
ApiKey
Model
~~~

### Proxy profile

The default proxy endpoint is:

~~~text
http://localhost:5105/v1
~~~

The Model value is a public MCP Proxy model route.

The ApiKey field can contain:

- a gateway mcp_ API key
- an OAuth/OIDC access token
- nothing

When the Proxy credential is blank, ChatService can reuse the signed-in user's access token.

This makes the browser client useful for end-to-end OIDC and model-route RBAC testing.

### Hosted profile

Hosted connects directly to an OpenAI-compatible service.

Its model value is the hosted provider's own model or deployment ID.

## Model discovery

The client calls:

~~~text
POST /api/llm-models
~~~

to query an OpenAI-compatible models endpoint.

For Proxy mode, this can exercise the gateway with either:

- the explicitly entered credential
- the signed-in user's access token when the credential field is blank

Saved API keys are not returned by the client settings API.

Submitting an empty password/credential field during settings changes preserves the existing stored key where the implementation supports that behavior.

## Chat loop

POST /api/chat invokes ChatService.

At a high level the client:

1. Loads the active LLM profile.
2. Connects to enabled MCP servers.
3. Collects their tools.
4. Prefixes tool names by server for the model-facing tool set.
5. Sends conversation history and tools to the configured OpenAI-compatible model.
6. Executes requested MCP tools.
7. Returns tool results to the model.
8. Repeats until the model returns a final answer or the configured iteration limit is reached.

The default maximum tool-call iteration count is 10.

## Conversation storage

ConversationStore keeps chat history in memory.

Conversation state is partitioned by authenticated user and conversation ID.

It is not currently persisted or shared across client application nodes.

Restarting the client removes in-memory conversation state.

## Client MCP server registry

The browser client has its own MCP server registry.

This is separate from the downstream server registry stored by MCP Proxy.

The client registry is persisted in:

~~~text
src/McpClient/Data/mcp-servers.json
~~~

A client server configuration contains:

~~~text
Id
Name
Description
Endpoint
TransportMode
Enabled
ForwardToken
AuthorizationScheme
ApiKey
ApiKeyHeaderName
AdditionalHeaders
ConnectionTimeoutSeconds
~~~

TransportMode supports values understood by the SDK configuration, including:

~~~text
AutoDetect
StreamableHttp
Sse
~~~

## Credential forwarding modes

ForwardToken supports:

~~~text
0 None
1 AccessToken
2 IdToken
3 ApiKey
~~~

Use None when the MCP server does not need the user's identity.

Use AccessToken when a downstream MCP server expects the signed-in user's OAuth access token.

Use IdToken only when that is explicitly what the downstream service expects.

Use ApiKey for a static server-specific credential.

## Using MCP Proxy from the client

A common configuration is to register MCP Proxy itself as one client MCP server:

~~~text
Endpoint: http://localhost:5105/mcp
~~~

Then choose an appropriate credential forwarding mode.

For an OIDC end-to-end scenario, AccessToken is normally the relevant mode when the token audience and proxy configuration match.

For a machine-style test, use a gateway API key.

## Direct downstream MCP connections

The browser client can also connect directly to other MCP servers without using MCP Proxy.

This is useful for development and comparison, but direct connections do not receive MCP Proxy's central RBAC, namespace, or catalog-governance behavior.

## Resources and prompts

The client exposes APIs for direct operations on a configured server:

~~~text
POST /api/servers/{serverName}/resources/read
POST /api/servers/{serverName}/prompts/get
~~~

The client also exposes:

~~~text
GET /api/servers
~~~

for the discovered server/capability view used by the UI.

## Configuration APIs

MCP server registry:

~~~text
GET    /api/server-configurations
POST   /api/server-configurations
PUT    /api/server-configurations/{id}
DELETE /api/server-configurations/{id}
~~~

LLM settings:

~~~text
GET  /api/llm-settings
PUT  /api/llm-settings
POST /api/llm-models
~~~

Session:

~~~text
GET /api/session
~~~

## Security notes

The browser client and MCP Proxy have separate security responsibilities.

The client owns:

- its OIDC session
- saved LLM connection settings
- saved client-side MCP server settings

The proxy owns:

- gateway authentication
- gateway role resolution
- MCP permissions
- model permissions
- downstream proxy/provider credentials

A signed-in client user does not automatically have permission in MCP Proxy. The access token and proxy claim-role mappings or linked user roles must produce the required authorization.

## Troubleshooting

### Login works but proxy model discovery fails

Check:

- Proxy endpoint ends in /v1
- access token audience
- proxy OIDC/JWT validation
- model-route permissions
- whether an explicit Proxy credential overrides login-token reuse

### Tools do not appear in chat

Check:

- client MCP server is enabled
- connection succeeds
- selected ForwardToken mode is correct
- proxy or downstream authorization permits discovery
- the server actually advertises tools

### The client loses a conversation after restart

That is expected. ConversationStore is in memory.

### One MCP server is down and chat still starts

Connection failures are collected per server so one unavailable MCP server does not necessarily prevent the client from using other reachable servers.

### Proxy mode works with an API key but not a blank credential

A blank Proxy credential relies on the signed-in user's access token. Verify OIDC login stored a usable access token and that the proxy accepts its issuer/audience.
