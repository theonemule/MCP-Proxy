using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using McpClient.Configuration;
using McpClient.Models;
using McpClient.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OidcOptions>(builder.Configuration.GetSection(OidcOptions.SectionName));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var oidc = builder.Configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
if (!string.IsNullOrWhiteSpace(oidc.ClientSecret) && oidc.ClientSecret.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
{
    var key = oidc.ClientSecret[4..];
    oidc.ClientSecret = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(oidc.ClientSecret))
    {
        throw new InvalidOperationException($"Secret '{key}' is not configured.");
    }
}

// ---------------------------------------------------------------- Authentication
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "mcpclient.auth.v2";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.HttpOnly = true;
        options.SlidingExpiration = true;

        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = oidc.Authority;
        if (!string.IsNullOrWhiteSpace(oidc.MetadataAddress))
        {
            options.MetadataAddress = oidc.MetadataAddress;
        }

        options.ClientId = oidc.ClientId;
        options.ClientSecret = oidc.ClientSecret;
        options.ResponseType = oidc.ResponseType;
        options.CallbackPath = oidc.CallbackPath;
        options.SignedOutCallbackPath = oidc.SignedOutCallbackPath;
        options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        options.GetClaimsFromUserInfoEndpoint = oidc.GetClaimsFromUserInfoEndpoint;
        options.UsePkce = oidc.UsePkce;
        options.SaveTokens = true; // required so the user's credential can be forwarded to MCP servers
        options.MapInboundClaims = false;

        options.Scope.Clear();
        foreach (var scope in oidc.Scopes)
        {
            options.Scope.Add(scope);
        }

        options.TokenValidationParameters.NameClaimType = oidc.NameClaimType;
        options.TokenValidationParameters.RoleClaimType = oidc.RoleClaimType;

        // API calls must fail with a status code the SPA can handle, not a redirect to the IdP.
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.HandleResponse();
            }

            return Task.CompletedTask;
        };

        options.Events.OnAuthorizationCodeReceived = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OidcDiagnostics");
            logger.LogInformation(
                "OIDC Token Request: ClientId='{ClientId}', SecretLength={Length}, RedirectUri='{RedirectUri}', GrantType='{GrantType}'",
                context.TokenEndpointRequest?.ClientId,
                context.TokenEndpointRequest?.ClientSecret?.Length ?? 0,
                context.TokenEndpointRequest?.RedirectUri,
                context.TokenEndpointRequest?.GrantType);
            return Task.CompletedTask;
        };

        options.Events.OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OidcDiagnostics");
            logger.LogError(context.Exception, "OIDC AuthenticationFailed: {Message}", context.Exception.Message);
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    if (oidc.Enabled)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
});

// ---------------------------------------------------------------- LLM
// The chat client is built per request from LlmSettingsStore so Settings-page edits apply
// immediately, so nothing is registered here beyond the store and factory themselves.
builder.Services.AddSingleton<LlmSettingsStore>();
builder.Services.AddSingleton<LlmClientFactory>();

// ---------------------------------------------------------------- App services
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("mcp");
builder.Services.AddHttpClient("llm-discovery", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<McpServerRegistry>();
builder.Services.AddScoped<UserTokenProvider>();
builder.Services.AddScoped<McpSessionFactory>();
builder.Services.AddScoped<ChatService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errors => errors.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
    }));
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------- Auth endpoints
app.MapGet("/account/login", (string? returnUrl) =>
    Results.Challenge(
        new AuthenticationProperties { RedirectUri = LocalRedirect(returnUrl) },
        [OpenIdConnectDefaults.AuthenticationScheme]))
    .AllowAnonymous();

app.MapPost("/account/logout", () =>
    Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));

// ---------------------------------------------------------------- API
var api = app.MapGroup("/api");
if (oidc.Enabled)
{
    api.RequireAuthorization();
}

api.MapPost("/chat", async (
    ChatRequest request,
    ChatService chat,
    ClaimsPrincipal user,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Message is required." });
    }

    var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
        ? Guid.NewGuid().ToString("n")
        : request.ConversationId;

    try
    {
        var response = await chat.SendAsync(UserId(user), conversationId, request.Message, cancellationToken);
        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message, detail = ex.ToString() },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

api.MapPost("/chat/{conversationId}/reset", (
    string conversationId,
    ConversationStore store,
    ClaimsPrincipal user) =>
{
    store.Clear(UserId(user), conversationId);
    return Results.NoContent();
});

api.MapGet("/servers", async (ChatService chat, CancellationToken cancellationToken) =>
    Results.Ok(await chat.DiscoverAsync(cancellationToken)));

api.MapPost("/servers/{serverName}/resources/read", async (
    string serverName,
    ReadResourceRequest request,
    McpSessionFactory sessions,
    CancellationToken cancellationToken) =>
{
    var server = sessions.Find(serverName);
    if (server is null) return Results.NotFound(new { error = "MCP server was not found or is disabled." });
    await using var session = await sessions.ConnectAsync(server, cancellationToken);
    return Results.Ok(await session.Client.ReadResourceAsync(request.Uri, cancellationToken: cancellationToken));
});

api.MapPost("/servers/{serverName}/prompts/get", async (
    string serverName,
    GetPromptRequest request,
    McpSessionFactory sessions,
    CancellationToken cancellationToken) =>
{
    var server = sessions.Find(serverName);
    if (server is null) return Results.NotFound(new { error = "MCP server was not found or is disabled." });
    await using var session = await sessions.ConnectAsync(server, cancellationToken);
    return Results.Ok(await session.Client.GetPromptAsync(
        request.Name,
        request.Arguments,
        cancellationToken: cancellationToken));
});

api.MapGet("/server-configurations", (McpServerRegistry registry) =>
    Results.Ok(registry.GetAll()));

api.MapPost("/server-configurations", (McpServerOptions server, McpServerRegistry registry) =>
{
    var error = ValidateServer(server);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    NormalizeServer(server);
    try
    {
        var created = registry.Add(server);
        return Results.Created($"/api/server-configurations/{created.Id}", created);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
});

api.MapPut("/server-configurations/{id}", (string id, McpServerOptions server, McpServerRegistry registry) =>
{
    var error = ValidateServer(server);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    NormalizeServer(server);
    try
    {
        var updated = registry.Update(id, server);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
});

api.MapDelete("/server-configurations/{id}", (string id, McpServerRegistry registry) =>
    registry.Delete(id) ? Results.NoContent() : Results.NotFound());

api.MapGet("/llm-settings", (LlmSettingsStore store) => Results.Ok(PublicLlmSettings(store.Get())));

api.MapPut("/llm-settings", (LlmOptions settings, LlmSettingsStore store) =>
{
    var existing = store.Get();
    settings.Gateway ??= new LlmConnectionOptions();
    settings.Hosted ??= new LlmConnectionOptions();

    // Password fields are intentionally write-only. Empty means keep the existing credential.
    if (string.IsNullOrWhiteSpace(settings.Gateway.ApiKey))
    {
        settings.Gateway.ApiKey = existing.Gateway.ApiKey;
    }
    if (string.IsNullOrWhiteSpace(settings.Hosted.ApiKey))
    {
        settings.Hosted.ApiKey = existing.Hosted.ApiKey;
    }

    NormalizeLlmSettings(settings);
    var error = ValidateLlmSettings(settings);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    return Results.Ok(PublicLlmSettings(store.Update(settings)));
});

api.MapPost("/llm-models", async (
    LlmModelDiscoveryRequest discovery,
    LlmSettingsStore store,
    UserTokenProvider tokenProvider,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    if (!Uri.TryCreate(discovery.Endpoint?.Trim(), UriKind.Absolute, out var endpoint)
        || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest(new { error = $"{discovery.Source} endpoint is not a valid HTTP/HTTPS URL." });
    }

    var persisted = store.Get();
    var savedConnection = discovery.Source == LlmSource.Gateway ? persisted.Gateway : persisted.Hosted;
    var credential = string.IsNullOrWhiteSpace(discovery.ApiKey)
        ? savedConnection.ApiKey
        : discovery.ApiKey.Trim();

    var authMode = "api-key";
    if (discovery.Source == LlmSource.Gateway && string.IsNullOrWhiteSpace(credential))
    {
        credential = await tokenProvider.GetTokenAsync(ForwardedToken.AccessToken);
        authMode = "signed-in-oauth-token";
    }
    else if (discovery.Source == LlmSource.Gateway)
    {
        authMode = credential.StartsWith("aigw_", StringComparison.Ordinal)
            ? "gateway-api-key"
            : "oauth-bearer-token";
    }

    if (string.IsNullOrWhiteSpace(credential))
    {
        return Results.BadRequest(new
        {
            error = discovery.Source == LlmSource.Gateway
                ? "No gateway API key is configured and no signed-in access token is available."
                : "No hosted API key is configured."
        });
    }

    var modelsUri = new Uri(discovery.Endpoint.TrimEnd('/') + "/models", UriKind.Absolute);
    using var request = new HttpRequestMessage(HttpMethod.Get, modelsUri);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);

    try
    {
        var client = httpClientFactory.CreateClient("llm-discovery");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var safeBody = body.Length > 1200 ? body[..1200] : body;
            return Results.Json(new
            {
                error = $"Model discovery returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
                detail = safeBody,
                endpoint = modelsUri.ToString(),
                source = discovery.Source.ToString()
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        using var document = JsonDocument.Parse(body);
        var models = document.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
                .Where(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("id").GetString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        return Results.Ok(new
        {
            source = discovery.Source.ToString(),
            endpoint = modelsUri.ToString(),
            authMode,
            models
        });
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
    {
        return Results.Json(new
        {
            error = exception.Message,
            endpoint = modelsUri.ToString(),
            source = discovery.Source.ToString()
        }, statusCode: StatusCodes.Status502BadGateway);
    }
});

// The SPA polls this before rendering, so it must be reachable while signed out.
app.MapGet("/api/session", (ClaimsPrincipal user) => Results.Ok(new
{
    authenticated = !oidc.Enabled || user.Identity?.IsAuthenticated == true,
    name = oidc.Enabled
        ? user.Identity?.Name ?? user.FindFirst("preferred_username")?.Value
        : "Local user",
})).AllowAnonymous();

app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

static string UserId(ClaimsPrincipal user) =>
    user.FindFirst("sub")?.Value
    ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
    ?? user.Identity?.Name
    ?? "anonymous";

static string LocalRedirect(string? returnUrl) =>
    !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
        ? returnUrl
        : "/";

static string? ValidateServer(McpServerOptions server)
{
    if (string.IsNullOrWhiteSpace(server.Name))
    {
        return "Server name is required.";
    }

    if (server.Name.Trim().Length > 100)
    {
        return "Server name must be 100 characters or fewer.";
    }

    if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var endpoint)
        || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
    {
        return "Endpoint must be an absolute HTTP or HTTPS URL.";
    }

    if (!new[] { "AutoDetect", "StreamableHttp", "Sse" }.Contains(server.TransportMode, StringComparer.OrdinalIgnoreCase))
    {
        return "Transport mode must be AutoDetect, StreamableHttp, or Sse.";
    }

    if (server.ConnectionTimeoutSeconds is < 1 or > 300)
    {
        return "Connection timeout must be between 1 and 300 seconds.";
    }

    if (server.ForwardToken is ForwardedToken.AccessToken or ForwardedToken.IdToken
        && string.IsNullOrWhiteSpace(server.AuthorizationScheme))
    {
        return "Authorization scheme is required when forwarding a token.";
    }

    if (server.ForwardToken == ForwardedToken.ApiKey && string.IsNullOrWhiteSpace(server.ApiKeyHeaderName))
    {
        return "A header name is required when using an API key.";
    }

    return null;
}

static object PublicLlmSettings(LlmOptions settings) => new
{
    source = settings.Source,
    gateway = new
    {
        endpoint = settings.Gateway.Endpoint,
        model = settings.Gateway.Model,
        apiKeyConfigured = !string.IsNullOrWhiteSpace(settings.Gateway.ApiKey)
    },
    hosted = new
    {
        endpoint = settings.Hosted.Endpoint,
        model = settings.Hosted.Model,
        apiKeyConfigured = !string.IsNullOrWhiteSpace(settings.Hosted.ApiKey)
    },
    settings.SystemPrompt,
    settings.Temperature,
    settings.MaxOutputTokens,
    settings.MaxToolIterations
};

static string? ValidateLlmSettings(LlmOptions settings)
{
    var connection = settings.ActiveConnection;
    if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint)
        || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
    {
        return $"{settings.Source} endpoint must be an absolute HTTP or HTTPS URL.";
    }

    if (string.IsNullOrWhiteSpace(connection.Model))
    {
        return $"{settings.Source} model is required.";
    }

    if (settings.MaxToolIterations < 1)
    {
        return "Max tool iterations must be at least 1.";
    }

    return null;
}

static void NormalizeLlmSettings(LlmOptions settings)
{
    settings.Gateway ??= new LlmConnectionOptions();
    settings.Hosted ??= new LlmConnectionOptions();

    settings.Gateway.Endpoint = settings.Gateway.Endpoint.Trim();
    settings.Gateway.Model = settings.Gateway.Model.Trim();
    settings.Gateway.ApiKey = settings.Gateway.ApiKey?.Trim() ?? string.Empty;

    settings.Hosted.Endpoint = settings.Hosted.Endpoint.Trim();
    settings.Hosted.Model = settings.Hosted.Model.Trim();
    settings.Hosted.ApiKey = settings.Hosted.ApiKey?.Trim() ?? string.Empty;
}

static void NormalizeServer(McpServerOptions server)
{
    server.Name = server.Name.Trim();
    server.Description = string.IsNullOrWhiteSpace(server.Description) ? null : server.Description.Trim();
    server.Endpoint = server.Endpoint.Trim();
    server.TransportMode = server.TransportMode.Trim();
    server.AuthorizationScheme = server.AuthorizationScheme.Trim();
    server.ApiKey = string.IsNullOrWhiteSpace(server.ApiKey) ? null : server.ApiKey.Trim();
    server.ApiKeyHeaderName = string.IsNullOrWhiteSpace(server.ApiKeyHeaderName) ? "X-Api-Key" : server.ApiKeyHeaderName.Trim();
    server.AdditionalHeaders = (server.AdditionalHeaders ?? [])
        .Where(header => !string.IsNullOrWhiteSpace(header.Key))
        .ToDictionary(header => header.Key.Trim(), header => header.Value.Trim(), StringComparer.OrdinalIgnoreCase);
}