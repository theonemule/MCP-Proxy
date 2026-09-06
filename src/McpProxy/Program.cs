using System.Security.Cryptography;
using System.Text.Json;
using McpProxy.Admin;
using McpProxy.Configuration;
using McpProxy.Data;
using McpProxy.Mcp;
using McpProxy.Registry;
using McpProxy.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));
builder.Services.Configure<SecretOptions>(builder.Configuration.GetSection("Secrets"));
builder.Services.Configure<LoggingOptions>(builder.Configuration.GetSection("LoggingOptions"));
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
var databaseOptions = builder.Configuration.GetSection("Database").Get<DatabaseOptions>() ?? new DatabaseOptions();
var cacheOptions = builder.Configuration.GetSection("Cache").Get<CacheOptions>() ?? new CacheOptions();
var secretOptions = builder.Configuration.GetSection("Secrets").Get<SecretOptions>() ?? new SecretOptions();
var loggingOptions = builder.Configuration.GetSection("LoggingOptions").Get<LoggingOptions>() ?? new LoggingOptions();
var secretProvider = new EnvironmentSecretProvider();

authOptions.ClientSecret = SecretReferenceResolver.Resolve(authOptions.ClientSecret, secretProvider);
authOptions.SigningKey = SecretReferenceResolver.Resolve(authOptions.SigningKey, secretProvider);
databaseOptions.ConnectionString = SecretReferenceResolver.Resolve(databaseOptions.ConnectionString, secretProvider) ?? databaseOptions.ConnectionString;
databaseOptions.SqliteConnectionString = SecretReferenceResolver.Resolve(databaseOptions.SqliteConnectionString, secretProvider) ?? databaseOptions.SqliteConnectionString;
databaseOptions.SqlServerConnectionString = SecretReferenceResolver.Resolve(databaseOptions.SqlServerConnectionString, secretProvider) ?? databaseOptions.SqlServerConnectionString;
databaseOptions.PostgresConnectionString = SecretReferenceResolver.Resolve(databaseOptions.PostgresConnectionString, secretProvider) ?? databaseOptions.PostgresConnectionString;
cacheOptions.ConnectionString = SecretReferenceResolver.Resolve(cacheOptions.ConnectionString, secretProvider) ?? cacheOptions.ConnectionString;

builder.Services.AddSingleton<ISecretProvider>(secretProvider);

builder.Services.AddDbContext<ProxyDbContext>(options =>
{
    var connectionString = databaseOptions.GetConnectionString();
    switch (databaseOptions.Provider)
    {
        case DatabaseProvider.Sqlite:
            options.UseSqlite(connectionString);
            break;
        case DatabaseProvider.SqlServer:
            options.UseSqlServer(connectionString);
            break;
        case DatabaseProvider.Postgres:
            options.UseNpgsql(connectionString);
            break;
        default:
            options.UseSqlite(connectionString);
            break;
    }
});

builder.Services.AddHttpClient("mcp-downstream", client =>
    client.DefaultRequestHeaders.UserAgent.ParseAdd("mcp-proxy/2.0"));

if (cacheOptions.Enabled && cacheOptions.Provider == CacheProvider.Redis && !string.IsNullOrWhiteSpace(cacheOptions.ConnectionString))
{
    var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(cacheOptions.ConnectionString);
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(redis);
    builder.Services.AddSingleton<ICatalogInvalidationBus, RedisCatalogInvalidationBus>();
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = cacheOptions.ConnectionString;
        options.InstanceName = "mcp-proxy:";
    });
}
else
{
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<ICatalogInvalidationBus, InMemoryCatalogInvalidationBus>();
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<McpEndpointScope>();
builder.Services.AddSingleton<DownstreamClientFactory>();
builder.Services.AddSingleton<CatalogCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CatalogCache>());
builder.Services.AddScoped<GatewayService>();

var signingKeyBytes = !string.IsNullOrWhiteSpace(authOptions.SigningKey)
    ? Convert.FromBase64String(authOptions.SigningKey)
    : RandomNumberGenerator.GetBytes(32);
var signingKey = new SymmetricSecurityKey(signingKeyBytes) { KeyId = "internal-1" };
builder.Services.AddSingleton(signingKey);
builder.Services.AddSingleton<JwtTokenService>();

if (authOptions.Enabled)
{
    var authentication = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "Smart";
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    });

    authentication.AddPolicyScheme("Smart", "API key, bearer token, or cookie", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            if (context.Request.Headers.ContainsKey(authOptions.ApiKeyHeaderName))
            {
                return ApiKeyAuthenticationOptions.SchemeName;
            }

            if (context.Request.Headers.ContainsKey("Authorization"))
            {
                return JwtBearerDefaults.AuthenticationScheme;
            }

            return CookieAuthenticationDefaults.AuthenticationScheme;
        };
    });

    authentication.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationOptions.SchemeName, _ => { });

    authentication.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "mcpproxy.auth.v1";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.HttpOnly = true;
        options.SlidingExpiration = true;
    });

    if (authOptions.Mode == AuthMode.Oidc)
    {
        if (string.IsNullOrWhiteSpace(authOptions.Authority) || string.IsNullOrWhiteSpace(authOptions.Audience))
        {
            throw new InvalidOperationException("Auth:Authority and Auth:Audience are required when Auth:Mode is Oidc.");
        }

        var clientId = !string.IsNullOrWhiteSpace(authOptions.ClientId)
            ? authOptions.ClientId
            : authOptions.Audience?.Replace("api://", "");

        authentication.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.Authority = authOptions.Authority;
            options.ClientId = clientId;
            if (!string.IsNullOrWhiteSpace(authOptions.ClientSecret))
            {
                options.ClientSecret = authOptions.ClientSecret;
            }
            options.ResponseType = "code";
            options.UsePkce = true;
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";
            options.RequireHttpsMetadata = authOptions.RequireHttpsMetadata;
            options.SaveTokens = true;
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "roles";
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                if (context.Request.Path.StartsWithSegments("/admin") || context.Request.Path.StartsWithSegments("/auth"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.HandleResponse();
                }

                return Task.CompletedTask;
            };
        });

        authentication.AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.Authority = authOptions.Authority;
            options.Audience = authOptions.Audience;
            options.RequireHttpsMetadata = authOptions.RequireHttpsMetadata;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKeyResolver = (token, securityToken, kid, validationParameters) =>
                {
                    // Accept internally signed tokens (kid="internal-1") as well as OIDC tokens
                    if (kid == "internal-1" || securityToken?.SigningKey?.KeyId == "internal-1")
                    {
                        return [signingKey];
                    }

                    try
                    {
                        var configManager = options.ConfigurationManager;
                        if (configManager is not null)
                        {
                            var config = configManager.GetConfigurationAsync(CancellationToken.None).GetAwaiter().GetResult();
                            if (config?.SigningKeys is not null && config.SigningKeys.Count > 0)
                            {
                                return config.SigningKeys;
                            }
                        }
                    }
                    catch
                    {
                        // Fallback
                    }

                    return [signingKey];
                },
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var principal = context.Principal;
                    if (principal is null) return;

                    // If token already carries internal PrincipalId claim, we're done
                    if (principal.HasClaim(c => c.Type == ProxyClaimTypes.PrincipalId))
                    {
                        return;
                    }

                    var subject = principal.FindFirst("sub")?.Value
                        ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? principal.FindFirst("preferred_username")?.Value;

                    if (string.IsNullOrEmpty(subject))
                    {
                        return;
                    }

                    var db = context.HttpContext.RequestServices.GetRequiredService<ProxyDbContext>();
                    var user = await db.Users.AsNoTracking()
                        .SingleOrDefaultAsync(x => (x.ExternalSubject == subject || x.Username == subject) && x.Enabled);

                    if (user is not null)
                    {
                        var identity = new System.Security.Claims.ClaimsIdentity(
                        [
                            new System.Security.Claims.Claim(ProxyClaimTypes.PrincipalKind, "user"),
                            new System.Security.Claims.Claim(ProxyClaimTypes.PrincipalId, user.Id.ToString())
                        ]);
                        principal.AddIdentity(identity);
                    }
                    // Unmapped tokens pass through; PermissionService handles RBAC via ClaimRoleMappings
                }
            };
        });
    }
    else
    {
        authentication.AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var principal = context.Principal;
                    if (principal is null || principal.HasClaim(c => c.Type == ProxyClaimTypes.PrincipalId))
                    {
                        return;
                    }

                    var subject = principal.FindFirst("sub")?.Value
                        ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? principal.FindFirst("preferred_username")?.Value;

                    if (string.IsNullOrEmpty(subject))
                    {
                        return;
                    }

                    var db = context.HttpContext.RequestServices.GetRequiredService<ProxyDbContext>();
                    var user = await db.Users.AsNoTracking()
                        .SingleOrDefaultAsync(x => (x.ExternalSubject == subject || x.Username == subject) && x.Enabled);

                    if (user is not null)
                    {
                        var identity = new System.Security.Claims.ClaimsIdentity(
                        [
                            new System.Security.Claims.Claim(ProxyClaimTypes.PrincipalKind, "user"),
                            new System.Security.Claims.Claim(ProxyClaimTypes.PrincipalId, user.Id.ToString())
                        ]);
                        principal.AddIdentity(identity);
                    }
                }
            };
        });
    }
}

builder.Services.AddAuthorization();

builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "mcp-proxy", Version = "2.0.0" };
    })
    .WithHttpTransport(options => options.Stateless = true)
    .WithListToolsHandler(async (context, cancellationToken) =>
    {
        var gateway = context.Services!.GetRequiredService<GatewayService>();
        var scope = context.Services!.GetRequiredService<McpEndpointScope>();
        return new ListToolsResult { Tools = [.. await gateway.ListToolsAsync(context.User!, scope.NamespacePrefix, cancellationToken)] };
    })
    .WithCallToolHandler(async (context, cancellationToken) =>
    {
        var gateway = context.Services!.GetRequiredService<GatewayService>();
        var scope = context.Services!.GetRequiredService<McpEndpointScope>();
        return await gateway.CallToolAsync(
            context.User!,
            context.Params!.Name,
            context.Params.Arguments is null ? null : new Dictionary<string, JsonElement>(context.Params.Arguments, StringComparer.Ordinal),
            scope.NamespacePrefix,
            cancellationToken);
    })
    .WithListResourcesHandler(async (context, cancellationToken) =>
    {
        var gateway = context.Services!.GetRequiredService<GatewayService>();
        var scope = context.Services!.GetRequiredService<McpEndpointScope>();
        return new ListResourcesResult { Resources = [.. await gateway.ListResourcesAsync(context.User!, scope.NamespacePrefix, cancellationToken)] };
    })
    .WithReadResourceHandler(async (context, cancellationToken) =>
    {
        var gateway = context.Services!.GetRequiredService<GatewayService>();
        var scope = context.Services!.GetRequiredService<McpEndpointScope>();
        return await gateway.ReadResourceAsync(context.User!, context.Params!.Uri, scope.NamespacePrefix, cancellationToken);
    })
    .WithListPromptsHandler(async (context, cancellationToken) =>
    {
        var gateway = context.Services!.GetRequiredService<GatewayService>();
        var scope = context.Services!.GetRequiredService<McpEndpointScope>();
        return new ListPromptsResult { Prompts = [.. await gateway.ListPromptsAsync(context.User!, scope.NamespacePrefix, cancellationToken)] };
    })
    .WithGetPromptHandler(async (context, cancellationToken) =>
    {
        var gateway = context.Services!.GetRequiredService<GatewayService>();
        var scope = context.Services!.GetRequiredService<McpEndpointScope>();
        return await gateway.GetPromptAsync(
            context.User!,
            context.Params!.Name,
            context.Params.Arguments is null ? null : new Dictionary<string, JsonElement>(context.Params.Arguments, StringComparer.Ordinal),
            scope.NamespacePrefix,
            cancellationToken);
    });

builder.Logging.ClearProviders();
if (Enum.TryParse<LogLevel>(loggingOptions.MinimumLevel, true, out var minimumLevel))
{
    builder.Logging.SetMinimumLevel(minimumLevel);
}

switch (loggingOptions.Provider.ToLowerInvariant())
{
    case "none":
        break;
    case "debug":
        builder.Logging.AddDebug();
        break;
    case "eventsource":
        builder.Logging.AddEventSourceLogger();
        break;
    default:
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
        break;
}

var app = builder.Build();

await DatabaseSeeder.SeedAsync(app.Services, app.Configuration);

app.UseDefaultFiles();
app.UseStaticFiles();

if (authOptions.Enabled)
{
    app.UseAuthentication();
}

app.UseAuthorization();

app.MapAuthEndpoints();
app.MapAdminEndpoints();

// The combined endpoint carries no scope; the per-server endpoint's route value is read by
// McpEndpointScope and narrows every handler above to that one server's catalog.
if (authOptions.Enabled)
{
    app.MapMcp("/mcp").RequireAuthorization();
    app.MapMcp("/servers/{serverScope}/mcp").RequireAuthorization();
}
else
{
    app.MapMcp("/mcp");
    app.MapMcp("/servers/{serverScope}/mcp");
}

app.Run();

