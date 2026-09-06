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
