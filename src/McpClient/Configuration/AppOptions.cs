namespace McpClient.Configuration;

/// <summary>OpenID Connect settings for the client web application.</summary>
public sealed class OidcOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Oidc";

    /// <summary>OIDC authority, e.g. https://login.microsoftonline.com/{tenant}/v2.0</summary>
    /// <summary>OIDC authority metadata endpoint.</summary>
    public string Authority { get; set; } = string.Empty;
    /// <summary>OIDC client identifier.</summary>
    public string ClientId { get; set; } = string.Empty;
    /// <summary>OIDC client secret or an environment reference.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Authorization response type, normally <c>code</c>.</summary>
    public string ResponseType { get; set; } = "code";
    /// <summary>Local callback path receiving the authorization response.</summary>
    public string CallbackPath { get; set; } = "/signin-oidc";
    /// <summary>Local callback path receiving sign-out responses.</summary>
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";
    /// <summary>Whether OIDC metadata must be retrieved over HTTPS.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
    /// <summary>Whether claims should also be loaded from the user-info endpoint.</summary>
    public bool GetClaimsFromUserInfoEndpoint { get; set; } = true;
    /// <summary>Whether PKCE is used during authorization-code sign-in.</summary>
    public bool UsePkce { get; set; } = true;
    /// <summary>Claim used as the authenticated user's display name.</summary>
    public string NameClaimType { get; set; } = "name";
    /// <summary>Claim used for role values.</summary>
    public string RoleClaimType { get; set; } = "roles";
    /// <summary>Scopes requested during sign-in.</summary>
    public string[] Scopes { get; set; } = ["openid", "profile", "email", "offline_access"];
    /// <summary>Optional explicit metadata address.</summary>
    public string? MetadataAddress { get; set; }
    /// <summary>Whether client routes require authentication.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Model connection selected by the client for inference.</summary>
public enum LlmSource
{
    /// <summary>Route inference through MCP Proxy's OpenAI-compatible /v1 surface.</summary>
    Proxy,
    /// <summary>Call a hosted OpenAI-compatible provider directly.</summary>
    Hosted
}

/// <summary>One OpenAI-compatible model connection profile.</summary>
public sealed class LlmConnectionOptions
{
    /// <summary>OpenAI-compatible API base URL.</summary>
    public string Endpoint { get; set; } = string.Empty;
    /// <summary>API key. Proxy mode may leave this empty to reuse the signed-in user's access token.</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Public proxy model alias or hosted provider model/deployment ID.</summary>
    public string Model { get; set; } = string.Empty;
}

/// <summary>Connection values used to test OpenAI model discovery without first saving settings.</summary>
public sealed record LlmModelDiscoveryRequest(LlmSource Source, string Endpoint, string? ApiKey);

/// <summary>Switchable proxy/hosted model profiles and shared agent-loop settings.</summary>
public sealed class LlmOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Llm";

    /// <summary>Profile used for the next chat turn.</summary>
    public LlmSource Source { get; set; } = LlmSource.Hosted;

    /// <summary>MCP Proxy OpenAI-compatible model profile.</summary>
    public LlmConnectionOptions Proxy { get; set; } = new()
    {
        Endpoint = "http://localhost:5105/v1"
    };

    /// <summary>Direct hosted OpenAI-compatible model profile.</summary>
    public LlmConnectionOptions Hosted { get; set; } = new()
    {
        Endpoint = "https://api.openai.com/v1",
        Model = "gpt-4o-mini"
    };

    /// <summary>System instruction sent at the beginning of each conversation.</summary>
    public string SystemPrompt { get; set; } =
        "You are a helpful assistant with access to tools exposed by MCP servers. " +
        "Use the tools when they help answer the user's request, and explain what you did.";

    /// <summary>Optional sampling temperature.</summary>
    public float? Temperature { get; set; }

    /// <summary>Optional maximum output token count.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Maximum number of tool-call iterations for one chat request.</summary>
    public int MaxToolIterations { get; set; } = 10;

    /// <summary>Returns the currently selected model connection.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LlmConnectionOptions ActiveConnection =>
        Source == LlmSource.Proxy ? Proxy : Hosted;
}

/// <summary>Credential forwarding mode for a downstream MCP server.</summary>
public enum ForwardedToken
{
    /// <summary>Do not forward an identity token.</summary>
    None,
    /// <summary>Forward the signed-in user's access token.</summary>
    AccessToken,
    /// <summary>Forward the signed-in user's ID token.</summary>
    IdToken,
    /// <summary>Use the server's own <see cref="McpServerOptions.ApiKey"/> instead of the signed-in user's credential.</summary>
    /// <summary>Use the server's configured static API key.</summary>
    ApiKey
}

/// <summary>Configuration for one downstream MCP server used by the client.</summary>
public sealed class McpServerOptions
{
    /// <summary>Stable identifier used by configuration APIs.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    /// <summary>Unique display name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Optional description shown in the client UI.</summary>
    public string? Description { get; set; }
    /// <summary>Downstream MCP endpoint.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>AutoDetect, StreamableHttp or Sse.</summary>
    /// <summary>Transport selection sent to the MCP SDK.</summary>
    public string TransportMode { get; set; } = "AutoDetect";
    /// <summary>Whether the server is included in discovery and chat sessions.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which token from the signed-in user is forwarded as the bearer credential.</summary>
    /// <summary>Identity credential to forward.</summary>
    public ForwardedToken ForwardToken { get; set; } = ForwardedToken.AccessToken;
    /// <summary>Authorization scheme used with a static or forwarded credential.</summary>
    public string AuthorizationScheme { get; set; } = "Bearer";

    /// <summary>The static credential sent when <see cref="ForwardToken"/> is <see cref="McpClient.Configuration.ForwardedToken.ApiKey"/>.</summary>
    /// <summary>Static credential used when <see cref="ForwardToken"/> is <see cref="ForwardedToken.ApiKey"/>.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Header carrying the static API key.</summary>
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";
    /// <summary>Additional downstream headers.</summary>
    public Dictionary<string, string> AdditionalHeaders { get; set; } = [];
    /// <summary>Connection timeout in seconds.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;
}

/// <summary>Client identity values sent to MCP servers.</summary>
public sealed class McpOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Mcp";

    /// <summary>MCP client name.</summary>
    public string ClientName { get; set; } = "McpClient";
    /// <summary>MCP client version.</summary>
    public string ClientVersion { get; set; } = "1.0.0";
}
