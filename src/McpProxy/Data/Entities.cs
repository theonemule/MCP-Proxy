namespace McpProxy.Data;

/// <summary>What a <see cref="Permission"/> grants access to within a server.</summary>
public enum CapabilityKind
{
    /// <summary>Every tool, resource, and prompt the server exposes.</summary>
    Server = 0,
    /// <summary>One named tool.</summary>
    Tool = 1,
    /// <summary>One resource URI.</summary>
    Resource = 2,
    /// <summary>One prompt name.</summary>
    Prompt = 3
}

/// <summary>A named set of permissions. Roles are the only unit of authorization.</summary>
public sealed class Role
{
    /// <summary>Stable identifier for the role.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Unique display and lookup name for the role.</summary>
    public required string Name { get; set; }
    /// <summary>Optional explanation of why the role exists.</summary>
    public string? Description { get; set; }

    /// <summary>Grants every administrative function, including user and server administration.</summary>
    public bool IsGlobalAdmin { get; set; }

    /// <summary>Grants management of users, roles, API keys, claim mappings, and permissions.</summary>
    public bool IsUserAdmin { get; set; }

    /// <summary>Grants management of registered MCP servers.</summary>
    public bool IsServerAdmin { get; set; }

    /// <summary>Permissions granted by this role.</summary>
    public List<Permission> Permissions { get; set; } = [];
    /// <summary>Local users assigned to this role.</summary>
    public List<UserRole> UserRoles { get; set; } = [];
    /// <summary>API keys assigned to this role.</summary>
    public List<ApiKeyRole> ApiKeyRoles { get; set; } = [];
    /// <summary>External identity claim mappings that resolve to this role.</summary>
    public List<ClaimRoleMapping> ClaimMappings { get; set; } = [];
    /// <summary>Model provider and model-route permissions granted by this role.</summary>
    public List<ModelPermission> ModelPermissions { get; set; } = [];
}

/// <summary>
/// Grants a role access to one server (<see cref="ItemName"/> null, <see cref="Kind"/> Server) or
/// to one named tool, resource, or prompt within it.
/// </summary>
public sealed class Permission
{
    /// <summary>Stable identifier for the permission.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Role receiving the permission.</summary>
    public Guid RoleId { get; set; }
    /// <summary>Related role navigation property.</summary>
    public Role Role { get; set; } = null!;
    /// <summary>Downstream server to which the permission applies.</summary>
    public Guid ServerId { get; set; }
    /// <summary>Related server navigation property.</summary>
    public McpServer Server { get; set; } = null!;
    /// <summary>Whether the grant covers a whole server or one capability family.</summary>
    public CapabilityKind Kind { get; set; }
    /// <summary>Name or URI of the individual capability; null for a server-wide grant.</summary>
    public string? ItemName { get; set; }
}

/// <summary>A locally authenticated principal. Password and/or external OIDC subject may both be set.</summary>
public sealed class User
{
    /// <summary>Stable identifier for the user.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Unique username used by local authentication.</summary>
    public required string Username { get; set; }
    /// <summary>PBKDF2 password hash, or null for an OIDC-only account.</summary>
    public string? PasswordHash { get; set; }
    /// <summary>External identity subject linked to this account, when present.</summary>
    public string? ExternalSubject { get; set; }
    /// <summary>Whether this account may authenticate.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Role assignments for the user.</summary>
    public List<UserRole> UserRoles { get; set; } = [];
}

/// <summary>Join entity assigning one role to one local user.</summary>
public sealed class UserRole
{
    /// <summary>Assigned user's identifier.</summary>
    public Guid UserId { get; set; }
    /// <summary>Assigned user navigation property.</summary>
    public User User { get; set; } = null!;
    /// <summary>Assigned role's identifier.</summary>
    public Guid RoleId { get; set; }
    /// <summary>Assigned role navigation property.</summary>
    public Role Role { get; set; } = null!;
}

/// <summary>A machine principal authenticated by a hashed secret rather than a login.</summary>
public sealed class ApiKey
{
    /// <summary>Stable identifier for the API key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-readable name shown in administration screens.</summary>
    public required string Name { get; set; }
    /// <summary>Non-secret prefix used to locate the key record.</summary>
    public required string KeyPrefix { get; set; }
    /// <summary>Hash of the secret portion of the key.</summary>
    public required string SecretHash { get; set; }
    /// <summary>Whether requests using this key are accepted.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Optional UTC expiration time.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>UTC timestamp of the most recent successful use.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Role assignments for the API key.</summary>
    public List<ApiKeyRole> ApiKeyRoles { get; set; } = [];
}

/// <summary>Join entity assigning one role to one API key.</summary>
public sealed class ApiKeyRole
{
    /// <summary>Assigned API key identifier.</summary>
    public Guid ApiKeyId { get; set; }
    /// <summary>Assigned API key navigation property.</summary>
    public ApiKey ApiKey { get; set; } = null!;
    /// <summary>Assigned role identifier.</summary>
    public Guid RoleId { get; set; }
    /// <summary>Assigned role navigation property.</summary>
    public Role Role { get; set; } = null!;
}

/// <summary>
/// Grants a role to any authenticated principal presenting a claim matching <see cref="ClaimType"/>
/// and <see cref="ClaimValue"/>. Used to assign roles from OIDC group/claim membership without a
/// local user record for every external identity.
/// </summary>
public sealed class ClaimRoleMapping
{
    /// <summary>Stable identifier for the mapping.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Claim type to match, such as <c>roles</c> or <c>group</c>.</summary>
    public required string ClaimType { get; set; }
    /// <summary>Case-insensitive claim value to match.</summary>
    public required string ClaimValue { get; set; }
    /// <summary>Role granted when the claim matches.</summary>
    public Guid RoleId { get; set; }
    /// <summary>Mapped role navigation property.</summary>
    public Role Role { get; set; } = null!;
}

/// <summary>A registered downstream MCP server reachable over Streamable HTTP.</summary>
public sealed class McpServer
{
    /// <summary>Stable identifier for the downstream server.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-readable server name.</summary>
    public required string Name { get; set; }
    /// <summary>Unique prefix used to namespace capabilities northbound.</summary>
    public required string NamespacePrefix { get; set; }
    /// <summary>HTTP or HTTPS Streamable HTTP endpoint.</summary>
    public required string Endpoint { get; set; }
    /// <summary>Whether the server participates in discovery and routing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Name of an environment variable holding the downstream credential, e.g. "env:GITHUB_TOKEN".</summary>
    public string? CredentialReference { get; set; }
    /// <summary>Header name used for the resolved downstream credential.</summary>
    public string CredentialHeader { get; set; } = "Authorization";
    /// <summary>Text prepended to the resolved credential value.</summary>
    public string CredentialPrefix { get; set; } = "Bearer ";

    /// <summary>UTC timestamp of the last successful catalog synchronization.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }
    /// <summary>Most recent synchronization error, if any.</summary>
    public string? LastError { get; set; }
    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Permissions that target this server.</summary>
    public List<Permission> Permissions { get; set; } = [];
}


/// <summary>Native protocol shape used by a downstream model provider.</summary>
public enum ModelProviderKind
{
    /// <summary>OpenAI-compatible chat-completions API, including compatible Foundry/Open WebUI endpoints.</summary>
    OpenAiCompatible = 0,
    /// <summary>Ollama native HTTP API.</summary>
    Ollama = 1,
    /// <summary>AWS Bedrock Runtime using the Converse API and SigV4 authentication.</summary>
    AwsBedrock = 2,
    /// <summary>Arbitrary HTTP provider available through native pass-through only.</summary>
    GenericHttp = 3
}

/// <summary>A registered downstream model-hosting provider.</summary>
public sealed class ModelProvider
{
    /// <summary>Stable identifier for the provider.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-readable provider name.</summary>
    public required string Name { get; set; }
    /// <summary>Unique URL-safe provider scope used by native proxy routes.</summary>
    public required string Slug { get; set; }
    /// <summary>Provider protocol family used by the unified router.</summary>
    public ModelProviderKind Kind { get; set; }
    /// <summary>Base HTTP endpoint. Bedrock may leave this empty to derive the regional runtime endpoint.</summary>
    public string BaseEndpoint { get; set; } = "";
    /// <summary>Optional provider-specific unified chat path. Defaults are chosen from <see cref="Kind"/>.</summary>
    public string? ChatPath { get; set; }
    /// <summary>Whether the provider may receive traffic.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional environment-backed static credential reference for HTTP providers.</summary>
    public string? CredentialReference { get; set; }
    /// <summary>Header carrying the resolved static credential.</summary>
    public string CredentialHeader { get; set; } = "Authorization";
    /// <summary>Text prepended to the resolved static credential.</summary>
    public string CredentialPrefix { get; set; } = "Bearer ";

    /// <summary>AWS region used for Bedrock requests.</summary>
    public string? AwsRegion { get; set; }
    /// <summary>Optional env reference for an AWS access key; falls back to the standard AWS environment variables.</summary>
    public string? AwsAccessKeyReference { get; set; }
    /// <summary>Optional env reference for an AWS secret key; falls back to the standard AWS environment variables.</summary>
    public string? AwsSecretKeyReference { get; set; }
    /// <summary>Optional env reference for an AWS session token.</summary>
    public string? AwsSessionTokenReference { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Public model aliases routed through this provider.</summary>
    public List<ModelRoute> Routes { get; set; } = [];
    /// <summary>Role grants that authorize native provider access.</summary>
    public List<ModelPermission> Permissions { get; set; } = [];
    /// <summary>Additional logical-route targets that use this provider.</summary>
    public List<ModelRouteTarget> RouteTargets { get; set; } = [];
}

/// <summary>Reasoning capability advertised by a model target for intelligent routing.</summary>
public enum ModelReasoningLevel
{
    /// <summary>No dedicated reasoning capability is expected.</summary>
    None = 0,
    /// <summary>Suitable for lightweight transformations and straightforward requests.</summary>
    Low = 1,
    /// <summary>Suitable for normal multi-step reasoning, coding, and analysis.</summary>
    Medium = 2,
    /// <summary>Suitable for difficult, deep, or explicitly high-reasoning requests.</summary>
    High = 3
}

/// <summary>A public model alias mapped to a provider-specific model identifier.</summary>
public sealed class ModelRoute
{
    /// <summary>Stable identifier for the route.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Owning provider identifier.</summary>
    public Guid ProviderId { get; set; }
    /// <summary>Owning provider.</summary>
    public ModelProvider Provider { get; set; } = null!;
    /// <summary>Stable northbound model name clients send to the unified router.</summary>
    public required string PublicName { get; set; }
    /// <summary>Provider-native model identifier.</summary>
    public required string DownstreamModel { get; set; }
    /// <summary>Whether the route may be selected.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Whether requests that omit model should use this logical route.</summary>
    public bool IsDefault { get; set; }
    /// <summary>Administrative preference tier for the primary target. Lower values are preferred after suitability.</summary>
    public int Priority { get; set; }
    /// <summary>Relative traffic share when multiple equally suitable targets remain tied.</summary>
    public int Weight { get; set; } = 100;
    /// <summary>Reasoning capability of the primary target.</summary>
    public ModelReasoningLevel ReasoningLevel { get; set; } = ModelReasoningLevel.Medium;
    /// <summary>Maximum total context tokens, or zero when unknown/unbounded.</summary>
    public int MaxContextTokens { get; set; }
    /// <summary>Maximum output tokens, or zero when unknown/unbounded.</summary>
    public int MaxOutputTokens { get; set; }
    /// <summary>Whether tool/function calling is supported; null means unknown.</summary>
    public bool? SupportsTools { get; set; }
    /// <summary>Whether image/vision inputs are supported; null means unknown.</summary>
    public bool? SupportsVision { get; set; }
    /// <summary>Whether structured JSON/JSON Schema output is supported; null means unknown.</summary>
    public bool? SupportsJsonSchema { get; set; }
    /// <summary>Relative cost tier from 1 (lowest) to 5 (highest), or zero when unknown.</summary>
    public int CostTier { get; set; }
    /// <summary>Relative latency tier from 1 (fastest) to 5 (slowest), or zero when unknown.</summary>
    public int LatencyTier { get; set; }
    /// <summary>Optional comma-separated specialties such as coding, math, vision, creative, summarization.</summary>
    public string? Specialties { get; set; }
    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Role grants that authorize this model alias.</summary>
    public List<ModelPermission> Permissions { get; set; } = [];
    /// <summary>Additional routing targets behind this public alias.</summary>
    public List<ModelRouteTarget> Targets { get; set; } = [];
}

/// <summary>
/// Additional downstream target for a public model route. The ModelRoute's existing provider/model
/// pair remains the implicit primary target at priority 0 and weight 100.
/// </summary>
public sealed class ModelRouteTarget
{
    /// <summary>Stable identifier for this routing target.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Logical public route owning this target.</summary>
    public Guid ModelRouteId { get; set; }
    /// <summary>Logical public route owning this target.</summary>
    public ModelRoute ModelRoute { get; set; } = null!;
    /// <summary>Provider receiving traffic when this target is selected.</summary>
    public Guid ProviderId { get; set; }
    /// <summary>Provider receiving traffic when this target is selected.</summary>
    public ModelProvider Provider { get; set; } = null!;
    /// <summary>Provider-native model identifier.</summary>
    public required string DownstreamModel { get; set; }
    /// <summary>Routing tier. Lower values are attempted before higher values.</summary>
    public int Priority { get; set; } = 100;
    /// <summary>Relative traffic share among healthy targets at the same priority.</summary>
    public int Weight { get; set; } = 100;
    /// <summary>Whether this target may receive routed traffic.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Reasoning capability advertised by this target.</summary>
    public ModelReasoningLevel ReasoningLevel { get; set; } = ModelReasoningLevel.Medium;
    /// <summary>Maximum total context tokens, or zero when unknown/unbounded.</summary>
    public int MaxContextTokens { get; set; }
    /// <summary>Maximum output tokens, or zero when unknown/unbounded.</summary>
    public int MaxOutputTokens { get; set; }
    /// <summary>Whether tool/function calling is supported; null means unknown.</summary>
    public bool? SupportsTools { get; set; }
    /// <summary>Whether image/vision inputs are supported; null means unknown.</summary>
    public bool? SupportsVision { get; set; }
    /// <summary>Whether structured JSON/JSON Schema output is supported; null means unknown.</summary>
    public bool? SupportsJsonSchema { get; set; }
    /// <summary>Relative cost tier from 1 (lowest) to 5 (highest), or zero when unknown.</summary>
    public int CostTier { get; set; }
    /// <summary>Relative latency tier from 1 (fastest) to 5 (slowest), or zero when unknown.</summary>
    public int LatencyTier { get; set; }
    /// <summary>Optional comma-separated specialties such as coding, math, vision, creative, summarization.</summary>
    public string? Specialties { get; set; }
    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Whether a model permission covers a whole provider or one public model route.</summary>
public enum ModelPermissionScope
{
    /// <summary>Allows native provider proxy access and every route on that provider.</summary>
    Provider = 0,
    /// <summary>Allows one model route through the unified API.</summary>
    Route = 1
}

/// <summary>Role grant for model-provider or public-model access.</summary>
public sealed class ModelPermission
{
    /// <summary>Stable identifier for the grant.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Role receiving the grant.</summary>
    public Guid RoleId { get; set; }
    /// <summary>Role receiving the grant.</summary>
    public Role Role { get; set; } = null!;
    /// <summary>Grant scope.</summary>
    public ModelPermissionScope Scope { get; set; }
    /// <summary>Provider identifier for provider-wide grants.</summary>
    public Guid? ProviderId { get; set; }
    /// <summary>Provider for provider-wide grants.</summary>
    public ModelProvider? Provider { get; set; }
    /// <summary>Model route identifier for route grants.</summary>
    public Guid? ModelRouteId { get; set; }
    /// <summary>Model route for route grants.</summary>
    public ModelRoute? ModelRoute { get; set; }
}
