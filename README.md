# THIS IS STILL VERY MUCH BETA, BUT PLEASE FEEL FREE TO USE, CREATE, and SEND PRS.

# MCP Proxy User Guide

MCP Proxy gives people and applications one controlled way to use tools, resources, and prompts from multiple Model Context Protocol (MCP) servers.

It includes two separate applications:

- **MCP Proxy** is the gateway and administration application. It connects to downstream MCP servers, applies access rules, and exposes one protected MCP endpoint.
- **MCP Client** is an optional browser application. It signs a person in, connects to configured MCP servers, and provides an AI chat experience that can use their tools.

This document is the complete user, administrator, and operator guide. It explains what the system is for, how to install it, how to configure it, how to sign in, how to manage access, how to connect MCP servers, how to use the client, and how to troubleshoot common problems.

Technical implementation and extension details are intentionally kept separate in [README.DEVELOPER.md](README.DEVELOPER.md).

## 1. What MCP Proxy Does

An MCP deployment may have many servers. Each server may provide different tools, resources, and prompts. Without a gateway, every person and application must know how to connect to every server and must be configured separately for each one.

MCP Proxy centralizes that experience.

A caller connects to the proxy. The proxy:

1. Authenticates the caller.
2. Determines the caller's roles.
3. Determines which servers and capabilities those roles may use.
4. Shows only permitted capabilities during discovery.
5. Sends approved requests to the correct downstream MCP server.
6. Returns the result through one consistent endpoint.

This gives an organization one MCP address, one access-control model, one place to manage server connections, and one place to audit operational behavior through logs.

## 2. The Two Applications

### 2.1 MCP Proxy

The proxy is the central service. In local development it normally runs at:

```text
http://localhost:5105
```

It provides:

- The administration website.
- User, role, permission, API-key, and server management.
- Local and OpenID Connect authentication.
- The protected MCP endpoint used by applications.
- Connection and catalog management for downstream MCP servers.

You can use the proxy without the browser client. Any compatible MCP client can connect to the proxy once it has an approved credential.

### 2.2 MCP Client

The client is a separate browser application. In local development it normally runs at:

```text
http://localhost:5256
```

It provides:

- OpenID Connect sign-in.
- An AI chat interface.
- MCP server discovery.
- Resource reading and prompt rendering.
- Settings for the LLM connection and client-side MCP servers.

The client is designed for people. The proxy is the shared service boundary used by people and applications.

## 3. Quick Start

This section gets a local installation running. The later sections explain all settings and workflows in detail.

### 3.1 Requirements

You need:

- The .NET SDK version required by the project.
- An identity-provider application registration if you want OpenID Connect sign-in.
- An OpenAI-compatible model endpoint and API key if you want to use chat.

PostgreSQL and Redis are optional for a local single-node installation. The default local setup uses SQLite and in-memory coordination.

### 3.2 Confirm the .NET SDK

From the repository directory, run:

```powershell
dotnet --info
```

If the SDK is installed in a directory that is not on `PATH`, set it for the current PowerShell session. For the development environment used by this project:

```powershell
$env:DOTNET_ROOT = "C:\Users\bstewart\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --info
```

### 3.3 Build the solution

```powershell
dotnet restore McpProxy.slnx
dotnet build McpProxy.slnx
dotnet test McpProxy.slnx
```

### 3.4 Start the proxy locally

For a fresh local database, set the bootstrap administrator before the first start:

```powershell
$env:Bootstrap__AdminUsername = "admin"
$env:Bootstrap__AdminPassword = "change-this-password"
dotnet run --project src/McpProxy --launch-profile http
```

Open:

```text
http://localhost:5105
```

The proxy creates its local SQLite database automatically. The bootstrap administrator is created only when the database has no users and no roles.

### 3.5 Start the client

Open a second terminal:

```powershell
dotnet run --project src/McpClient --launch-profile http
```

Open:

```text
http://localhost:5256
```

The client requires its OIDC and LLM settings before its complete sign-in and chat workflow can work. The proxy can be used independently while the client is configured.

### 3.6 First useful workflow

1. Sign in to the proxy administration website.
2. Register a downstream MCP server.
3. Give it a unique name and namespace prefix.
4. Synchronize it so its tools, resources, and prompts are discovered.
5. Create or select a role.
6. Give that role access to the whole server or selected capabilities.
7. Sign in as a user who has that role, or use an API key assigned to that role.
8. Connect a compatible MCP client to `http://localhost:5105/mcp`.
9. Confirm that only permitted capabilities are visible.

## 3.7 Run the proxy with Docker

The repository includes a production multi-stage [Dockerfile](Dockerfile) for the proxy and a
[docker-compose.yml](docker-compose.yml) profile containing the proxy, PostgreSQL, and Redis.
The browser client is intentionally not part of this deployment profile; it is a testing and user
experience application that can be run separately with `dotnet run`.

Set the required values in the shell that will start Compose:

```powershell
$env:POSTGRES_PASSWORD = "replace-with-a-database-password"
$env:BOOTSTRAP_ADMIN_PASSWORD = "replace-with-an-admin-password"
$env:MCP_PROXY_OIDC_CLIENT_SECRET = "replace-with-the-oidc-secret-value"
docker compose up --build -d
```

The proxy is then available at:

```text
http://localhost:5105
```

Compose uses PostgreSQL as the shared database and Redis for distributed cache and catalog
notifications. Data is stored in the named Docker volumes `proxy-postgres` and `proxy-redis`.

Useful commands:

```powershell
docker compose ps
docker compose logs -f proxy
docker compose down
docker compose down -v
```

`docker compose down -v` deletes the named database and Redis volumes. Use it only when you intend
to reset the deployment data. The proxy image listens on container port `8080`; Compose maps it to
host port `5105`.

To run the client for testing against the containerized proxy, start it separately:

```powershell
dotnet run --project src/McpClient --launch-profile http
```

Configure the client-side MCP server endpoint as `http://localhost:5105/mcp`.

## 4. Understanding the Important Concepts

### 4.1 Downstream MCP servers

A downstream server is an MCP server that the proxy connects to. It may provide tools, resources, prompts, or all three.

Examples include:

- Documentation search.
- Source control.
- Databases and reporting systems.
- Internal business applications.
- Development and operations tools.

A server must be registered, enabled, synchronized, and covered by permissions before a caller can use it through the proxy.

### 4.2 Namespace prefixes

Every registered server has a namespace prefix. This keeps names unique when multiple servers expose similarly named capabilities.

For example, a server with prefix `docs` and a native tool named `search` appears through the proxy as:

```text
docs__search
```

Choose short, stable, descriptive prefixes. Changing a prefix can affect client configuration and capability permissions.

### 4.3 Roles

A role is a reusable set of permissions and, optionally, administrative responsibilities. Roles may be assigned to:

- Local users.
- API keys.
- External identities through claim mappings.

The built-in administrative flags are:

- **Global administrator**: all administration capabilities.
- **User administrator**: users, roles, API keys, claim mappings, and permissions.
- **Server administrator**: registered servers and catalog synchronization.

Administrative responsibility and MCP usage permissions are separate. A server administrator does not automatically have permission to use every tool on the server.

### 4.4 Permissions

A permission says which role may use which capability.

The available levels are:

- **Server**: every tool, resource, and prompt from that server.
- **Tool**: one named tool.
- **Resource**: one resource URI.
- **Prompt**: one prompt.

Use server-wide permissions for trusted roles. Use individual capability permissions when the server includes sensitive or high-impact operations.

### 4.5 Local users

A local user has a username and password. The password is stored as a secure hash rather than as plain text.

A local user may also have an external identity subject attached to it. This is useful when a known external identity should receive direct proxy role assignments.

### 4.6 OpenID Connect identities

OpenID Connect lets the proxy trust an external identity provider such as Microsoft Entra ID.

The identity provider authenticates the person. The proxy uses claims from the identity to decide what that person may do.

There are two common models:

1. **Claim mapping**: a claim such as `roles=ProxyAdministrator` maps directly to a proxy role. The person does not need a local user record.
2. **Linked external user**: the identity provider's subject is stored on a local proxy user. That user receives directly assigned proxy roles as well as any matching claim-mapped roles.

Claim mapping is usually the preferred model for centrally managed workforce groups and application roles.

### 4.7 API keys

An API key is a credential for an application, automation job, or service account. It is not intended for interactive human sign-in.

The complete key is shown once when it is created. If it is lost, create a replacement. Give each application its own key so one key can be revoked without interrupting unrelated applications.

## 5. Choosing a Storage Setup

### 5.1 SQLite and memory cache

This is the default setup.

Use it for:

- Local development.
- Demonstrations.
- A single process on one machine.
- Small installations where a single-node failure is acceptable.

It requires no separate database or cache service. The database is a local file and cache coordination is held in process memory.

It is not intended for multiple proxy instances that must share state.

### 5.2 PostgreSQL or SQL Server with Redis

Use a shared database and Redis when you need several proxy instances or stronger production availability.

The shared database stores:

- Users.
- Roles.
- Permissions.
- API keys.
- Claim mappings.
- Registered servers.
- Catalog synchronization status.

Redis provides distributed cache support and cross-node catalog refresh notifications. Redis does not replace the database.

## 6. Complete Installation Options

### 6.1 Local SQLite

```powershell
$env:Bootstrap__AdminUsername = "admin"
$env:Bootstrap__AdminPassword = "use-a-real-development-password"
dotnet run --project src/McpProxy --launch-profile http
```

SQLite is created automatically. Changing the bootstrap password later does not change an already-created administrator.

### 6.2 PostgreSQL

```powershell
$env:Database__Provider = "Postgres"
$env:Database__PostgresConnectionString = "Host=127.0.0.1;Port=5432;Database=mcp_proxy;Username=mcp_proxy;Password=replace-me"
$env:Bootstrap__AdminUsername = "admin"
$env:Bootstrap__AdminPassword = "replace-me"
dotnet run --project src/McpProxy --launch-profile http
```

The PostgreSQL account must be able to connect and create the application's tables during first startup.

### 6.3 SQL Server

```powershell
$env:Database__Provider = "SqlServer"
$env:Database__SqlServerConnectionString = "Server=localhost;Database=McpProxy;Trusted_Connection=True;TrustServerCertificate=True"
$env:Bootstrap__AdminUsername = "admin"
$env:Bootstrap__AdminPassword = "replace-me"
dotnet run --project src/McpProxy --launch-profile http
```

Use the authentication format appropriate for your SQL Server environment.

### 6.4 Redis

```powershell
$env:Cache__Enabled = "true"
$env:Cache__Provider = "Redis"
$env:Cache__ConnectionString = "127.0.0.1:6379"
dotnet run --project src/McpProxy --launch-profile http
```

Use the connection-string format required by your Redis installation, including credentials or TLS options where necessary.

### 6.5 Multiple proxy instances

For each instance:

1. Deploy the same application version.
2. Use the same PostgreSQL or SQL Server database.
3. Use the same Redis service.
4. Use the same OIDC authority, client ID, audience, and secret.
5. Use the same internal signing key if internal JWTs are enabled.
6. Put the instances behind a load balancer or reverse proxy.
7. Configure health checks, backups, restart policies, and centralized logs.

Each instance keeps a local copy of the latest downstream catalog for speed. When one instance synchronizes a server, Redis notifies the other instances to refresh their local copies.

## 7. Authentication Setup

### 7.1 Local login

Local login is useful for development, emergency administration, and environments without an identity provider.

Set the bootstrap values before the first start:

```powershell
$env:Bootstrap__AdminUsername = "admin"
$env:Bootstrap__AdminPassword = "use-a-strong-password"
```

The proxy administration page provides the local login form. The local login endpoint also returns a proxy-issued bearer token for API use.

For normal workforce access, use individual OIDC identities instead of sharing one local administrator account.

### 7.2 OIDC setup checklist

Configure the proxy identity-provider application registration with:

- A redirect URI matching the proxy URL, such as `http://localhost:5105/signin-oidc` for local development.
- The correct post-logout redirect configuration.
- A client secret value.
- The API audience expected by the proxy.
- Any application roles or group claims you plan to map.

Configure the proxy with:

```text
Auth:Mode=Oidc
Auth:Authority=https://your-identity-provider/...
Auth:Audience=your-api-audience
Auth:ClientId=your-proxy-client-id
Auth:ClientSecret=env:MCP_PROXY_OIDC_CLIENT_SECRET
```

Then set the secret for the process:

```powershell
$env:MCP_PROXY_OIDC_CLIENT_SECRET = "the-secret-value"
```

Use the secret **value**, not the secret ID. Restart after changing it.

### 7.3 Client OIDC setup

The client has its own identity-provider application registration and client ID. Configure:

```text
Oidc:Authority=your-authority
Oidc:ClientId=your-client-client-id
Oidc:ClientSecret=env:MCP_CLIENT_OIDC_CLIENT_SECRET
```

Then set:

```powershell
$env:MCP_CLIENT_OIDC_CLIENT_SECRET = "the-client-secret-value"
```

The client requests the scopes listed under `Oidc:Scopes`. Include the proxy API scope when the client must receive an access token for the proxy.

### 7.4 Map an external claim to a role

To give an external identity access without creating a local user:

1. Create a proxy role, such as `ProxyAdministrators`.
2. Set its administration flags or MCP permissions.
3. Identify the claim type emitted by the identity provider, such as `roles`, `groups`, or `group`.
4. Identify the exact claim value emitted for the intended user or group.
5. Create a claim mapping in the proxy administration area.
6. Sign out and sign in again to obtain a fresh token.
7. Verify access in the appropriate administration area or MCP client.

Claim values are matched case-insensitively. If the provider emits a group object ID, map the object ID rather than the display name.

## 8. Using the Administration Website

Open the proxy URL in a browser and sign in. The administration website is the control center for the deployment.

### 8.1 Create a role

Create a role when multiple people or applications should receive the same access.

Give it:

- A clear name.
- A description.
- Any required administration flags.
- Its MCP server or capability permissions.

Useful example roles include:

- `DocumentationReaders`.
- `ReportingTools`.
- `ServerOperators`.
- `ProxyUserAdministrators`.
- `ProxyGlobalAdministrators`.

Use several focused roles instead of one role that grants everything.

### 8.2 Create a local user

A user can have:

- A password.
- An external subject.
- Both.

At least one is required. Assign one or more roles after creating the user.

### 8.3 Create a claim mapping

A claim mapping connects an identity-provider claim to a proxy role. For example:

```text
Claim type:  roles
Claim value: ProxyGlobalAdministrator
Role:        ProxyGlobalAdministrators
```

Use the exact claim type and value present in the signed-in identity.

### 8.4 Create an API key

Create a key with a descriptive name and optional expiration. Assign only the roles it needs.

Copy the complete key immediately. It cannot be recovered later. Store it in a secret manager or protected deployment variable.

### 8.5 Register a downstream server

Enter:

- Display name.
- Unique namespace prefix.
- MCP endpoint URL.
- Optional credential reference.
- Optional credential header.
- Optional credential prefix.

For example:

```text
Name:             Documentation
Namespace prefix: docs
Endpoint:         https://example.internal/mcp
Credential ref:   env:DOCUMENTATION_MCP_TOKEN
```

The proxy reads the credential environment variable when it connects. Never put the token in the server name, description, or checked-in documentation.

### 8.6 Synchronize a server

Synchronizing asks the downstream server what tools, resources, and prompts it provides.

Synchronize after:

- Registering a new server.
- Changing a server's capabilities.
- Updating a server endpoint.
- Repairing a downstream outage.

A successful synchronization updates the last-synchronized timestamp. A failure is recorded as an error. The previous successful catalog may remain available until a new successful catalog is received.

### 8.7 Grant access

Give a trusted role server-wide access when it may safely use all capabilities. Give a restricted role individual tool, resource, or prompt permissions when it should use only selected operations.

## 9. Connecting to the Proxy MCP Endpoint

A compatible MCP client connects to:

```text
http://localhost:5105/mcp
```

Use one of:

- A proxy-issued bearer token from local login.
- An OIDC bearer token issued for the proxy audience.
- An API key in the configured header.

The client discovers tools, resources, and prompts. The proxy filters discovery according to the caller's roles.

A per-server endpoint is also available:

```text
http://localhost:5105/servers/{namespacePrefix}/mcp
```

This limits requests to one server namespace but does not bypass authorization.

## 10. Configuring and Using the Browser Client

### 10.1 Add the proxy as a client server

The client server registry is stored in `src/McpClient/Data/mcp-servers.json` and can also be managed through the client settings UI.

A typical entry is:

```json
{
  "name": "MCP Proxy",
  "endpoint": "http://localhost:5105/mcp",
  "transportMode": "StreamableHttp",
  "enabled": true,
  "forwardToken": "AccessToken",
  "authorizationScheme": "Bearer",
  "connectionTimeoutSeconds": 30
}
```

Forwarding choices:

- `AccessToken`: forward the user's access token.
- `IdToken`: forward the user's ID token.
- `None`: do not forward a user token.
- `ApiKey`: use the server's configured static API key.

Treat the server registry as sensitive if it contains an API key. Do not copy live credentials into examples or documentation.

### 10.2 Configure the LLM

Set:

- OpenAI-compatible endpoint.
- Model or deployment name.
- API key.
- System prompt.
- Maximum tool iterations.
- Optional temperature and output-token limits.

The client validates the endpoint and model before chat begins. If one MCP server is unavailable, the client reports a warning and continues with other available servers.

### 10.3 Use chat

1. Sign in.
2. Confirm an MCP server is enabled.
3. Open chat.
4. Ask a question.
5. The assistant decides whether a tool is useful.
6. The client sends the tool request.
7. The assistant uses the result to answer.

Conversation history is kept in memory and separated by signed-in user and conversation ID. Restarting the client clears conversation history.

### 10.4 Discover capabilities

The client discovery view reports:

- Server connection status.
- Server name and version.
- Tools.
- Resources.
- Prompts.
- Resource templates.
- Connection and discovery errors.

Some servers expose tools but not resources or prompts. A missing capability category does not necessarily mean the server is broken.

## 11. Configuration Reference

### 11.1 Proxy settings

| Setting | Meaning |
| --- | --- |
| `Auth:Enabled` | Enables proxy authentication. Keep enabled in shared environments. |
| `Auth:Mode` | `Internal` for proxy-issued tokens or `Oidc` for an external provider. |
| `Auth:Authority` | Identity-provider authority URL. |
| `Auth:Audience` | Accepted proxy bearer-token audience. |
| `Auth:ClientId` | Proxy OIDC application ID. |
| `Auth:ClientSecret` | Secret value or `env:VARIABLE_NAME` reference. |
| `Auth:SigningKey` | Stable Base64 signing key for internal JWTs. |
| `Auth:TokenLifetimeMinutes` | Internal token lifetime. |
| `Auth:RequireHttpsMetadata` | Requires HTTPS for OIDC metadata. |
| `Auth:ApiKeysEnabled` | Enables API-key authentication. |
| `Auth:ApiKeyHeaderName` | Header used for API keys. |
| `Database:Provider` | `Sqlite`, `SqlServer`, or `Postgres`. |
| `Database:ConnectionString` | Fallback database connection string. |
| `Database:SqliteConnectionString` | SQLite connection string. |
| `Database:SqlServerConnectionString` | SQL Server connection string. |
| `Database:PostgresConnectionString` | PostgreSQL connection string. |
| `Cache:Provider` | `Memory` or `Redis`. |
| `Cache:Enabled` | Enables Redis-backed cache and cross-node notifications. |
| `Cache:ConnectionString` | Redis connection string. |
| `Cache:Database` | Redis logical database number. |
| `LoggingOptions:Provider` | `Console`, `Debug`, `EventSource`, or `None`. |
| `LoggingOptions:MinimumLevel` | Minimum log level. |
| `Bootstrap:AdminUsername` | Username created on an empty database. |
| `Bootstrap:AdminPassword` | Password used only for first bootstrap. |

Nested environment variables use double underscores:

```powershell
$env:Database__Provider = "Postgres"
$env:Cache__Enabled = "true"
```

### 11.2 Client settings

| Setting | Meaning |
| --- | --- |
| `Oidc:Enabled` | Requires sign-in for client API operations. |
| `Oidc:Authority` | Identity-provider authority URL. |
| `Oidc:ClientId` | Client application ID. |
| `Oidc:ClientSecret` | Secret value or environment reference. |
| `Oidc:CallbackPath` | OIDC sign-in callback, normally `/signin-oidc`. |
| `Oidc:SignedOutCallbackPath` | OIDC sign-out callback. |
| `Oidc:UsePkce` | Enables PKCE. |
| `Oidc:NameClaimType` | Display-name claim. |
| `Oidc:RoleClaimType` | Role claim. |
| `Oidc:Scopes` | Requested identity-provider scopes. |
| `Llm:Endpoint` | OpenAI-compatible API base URL. |
| `Llm:ApiKey` | LLM API key. |
| `Llm:Model` | Model or deployment name. |
| `Llm:SystemPrompt` | Assistant instructions. |
| `Llm:MaxToolIterations` | Maximum tool-call rounds. |
| `Mcp:ClientName` | Client name sent during MCP initialization. |
| `Mcp:ClientVersion` | Client version sent during MCP initialization. |

## 12. Secret Safety

Never commit these values:

- OIDC client secrets.
- LLM API keys.
- Database passwords.
- Redis passwords.
- Downstream MCP credentials.
- Bootstrap passwords.
- Complete API keys.
- JWT signing keys.

Use environment references:

```text
Auth:ClientSecret=env:MCP_PROXY_OIDC_CLIENT_SECRET
Oidc:ClientSecret=env:MCP_CLIENT_OIDC_CLIENT_SECRET
```

Then provide the values to the process:

```powershell
$env:MCP_PROXY_OIDC_CLIENT_SECRET = "secret-value"
$env:MCP_CLIENT_OIDC_CLIENT_SECRET = "secret-value"
```

Secrets are resolved during startup. Restart after changing one.

`AADSTS7000215: Invalid client secret` usually means the value is expired, revoked, from another application registration, or the secret ID was supplied instead of the secret value.

## 13. Production Guidance

For a production deployment:

- Use PostgreSQL or SQL Server instead of SQLite.
- Use Redis when running more than one proxy node.
- Run behind HTTPS.
- Store secrets in a deployment secret manager or protected environment variables.
- Use a stable internal JWT signing key if internal JWTs are enabled.
- Use an external identity provider for normal human access.
- Give people and applications separate roles and credentials.
- Give automation separate API keys with expiration dates.
- Give each role only the permissions it needs.
- Back up the shared database.
- Monitor the database, Redis, downstream MCP servers, and identity provider.
- Send logs to a central logging system.
- Rotate secrets and credentials according to organizational policy.
- Test failover and recovery before relying on multiple nodes for availability.

## 14. Troubleshooting

### The proxy does not start

Check the terminal output. Common causes include:

- The selected database is unavailable.
- A connection string is incorrect.
- A required `env:` secret is missing.
- Redis is enabled but unreachable.
- Port `5105` is already in use.
- The .NET SDK is not on `PATH`.

### The bootstrap administrator does not work

The bootstrap user is created only when both users and roles are empty. Changing bootstrap variables later does not change an existing account.

Use the existing account, use OIDC with an assigned claim role, or intentionally reset a disposable development database.

### OIDC returns an invalid-client error

Check:

1. The correct application registration is being used.
2. The client ID matches that registration.
3. The authority points to the correct tenant or issuer.
4. The environment variable exists in the process that started the proxy.
5. The secret value, not the secret ID, was supplied.
6. The redirect URI matches the actual URL and port.
7. The proxy was restarted after the secret changed.

### A user can sign in but cannot administer

Check that:

- The token contains the expected role or group claim.
- The claim mapping uses the exact claim type and value.
- The mapped role has the required administration flag.
- The user signed in again after the mapping was created.
- The request reaches the intended proxy instance and database.

### A server is visible but tools are missing

Check that:

- The server is enabled.
- The endpoint is correct.
- Synchronization succeeded.
- The downstream server is available.
- The caller has a server-wide or capability-level permission.
- The namespace prefix is correct.

The proxy intentionally does not reveal whether an unauthorized capability exists.

### Redis is running but another node is stale

Check that every node uses the same Redis service, shared database, and Redis settings. Confirm that `Cache:Enabled` is `true`, `Cache:Provider` is `Redis`, and Redis pub/sub is permitted.

### Client chat cannot use a tool

Check that:

- The client is signed in.
- The client points to the intended proxy.
- The forwarded token has the proxy audience and required role.
- The server is enabled in client configuration.
- The proxy role permits the tool.
- The LLM endpoint, model, and API key are valid.

### A downstream credential fails

Check that:

- The reference starts with `env:`.
- The environment variable exists in the proxy process.
- The header name is accepted.
- The prefix matches the downstream server's expectation.
- The credential is valid and has not expired.

## 15. Data Reset and Recovery

The proxy database contains durable users, roles, permissions, API keys, claim mappings, and server registrations. Do not delete it casually.

The client stores settings under its `Data` directory. Conversation history is held in memory and is lost when the client restarts.

For a disposable local reset, stop the application and remove the local SQLite database only when losing its data is acceptable. A shared PostgreSQL or SQL Server database should be reset only through an intentional administrative process.

## 16. Further Documentation

- This file, [README.md](README.md), is the complete user and administrator guide.
- [README.DEVELOPER.md](README.DEVELOPER.md) is the separate complete developer guide covering architecture, source-level contracts, APIs, testing, security implementation, and extension points.
