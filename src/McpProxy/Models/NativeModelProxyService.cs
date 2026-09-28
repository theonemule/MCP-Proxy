using System.Net.Http.Headers;
using McpProxy.Data;
using McpProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Models;

/// <summary>Transparent native HTTP proxy for registered model providers with gateway RBAC in front.</summary>
public sealed class NativeModelProxyService(
    ProxyDbContext db,
    IPermissionService permissions,
    IHttpClientFactory httpClientFactory,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions)
{
    private static readonly HashSet<string> RequestHeadersToDrop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "Proxy-Authorization", "Cookie", "Connection", "Keep-Alive",
        "Proxy-Authenticate", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Forwarded",
        "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Api-Key"
    };

    private static readonly HashSet<string> ResponseHeadersToDrop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Set-Cookie"
    };

    /// <summary>Forwards one request to a provider while replacing northbound credentials with downstream credentials.</summary>
    public async Task ProxyAsync(
        HttpContext context,
        string providerScope,
        string? path,
        CancellationToken cancellationToken)
    {
        var provider = await db.ModelProviders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Slug == providerScope && x.Enabled, cancellationToken);

        if (provider is null ||
            !await permissions.CanAccessModelProviderAsync(context.User, provider.Id, cancellationToken))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var target = BuildTargetUri(provider, path, context.Request.QueryString.Value);
        var payload = await ReadPayloadAsync(context.Request, cancellationToken);
        using var downstream = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        if (payload is not null)
        {
            downstream.Content = new ByteArrayContent(payload);
        }

        CopyRequestHeaders(context.Request, downstream);

        if (provider.Kind == ModelProviderKind.AwsBedrock)
        {
            var bearerHeaders = ModelCredentialResolver.ResolveBedrockBearerHeaders(provider);
            if (bearerHeaders is null)
            {
                var credentials = ModelCredentialResolver.ResolveAwsCredentials(provider);
                AwsSigV4Signer.Sign(downstream, payload ?? [], credentials, "bedrock", DateTimeOffset.UtcNow);
            }
            else
            {
                ApplyHeaders(downstream, bearerHeaders);
            }
        }
        else
        {
            ApplyHeaders(downstream, ModelCredentialResolver.ResolveHeaders(provider));
        }

        var client = httpClientFactory.CreateClient("model-downstream");
        using var response = await client.SendAsync(downstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        context.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, context.Response);
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private static Uri BuildTargetUri(ModelProvider provider, string? path, string? query)
    {
        string endpoint;
        if (provider.Kind == ModelProviderKind.AwsBedrock && string.IsNullOrWhiteSpace(provider.BaseEndpoint))
        {
            var region = ModelCredentialResolver.ResolveAwsRegion(provider);
            endpoint = $"https://bedrock-runtime.{region}.amazonaws.com";
        }
        else
        {
            endpoint = provider.BaseEndpoint;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Model provider '{provider.Name}' has an invalid HTTP base endpoint.");
        }

        var target = baseUri.ToString().TrimEnd('/');
        if (!string.IsNullOrEmpty(path))
        {
            target += "/" + path.TrimStart('/');
        }
        if (!string.IsNullOrEmpty(query))
        {
            target += query.StartsWith('?') ? query : "?" + query;
        }

        return new Uri(target, UriKind.Absolute);
    }

    private static async Task<byte[]?> ReadPayloadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0)
        {
            return null;
        }

        if (request.Body == Stream.Null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        await request.Body.CopyToAsync(memory, cancellationToken);
        return memory.Length == 0 ? null : memory.ToArray();
    }

    private void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
    {
        foreach (var header in source.Headers)
        {
            if (RequestHeadersToDrop.Contains(header.Key) ||
                header.Key.Equals(authOptions.Value.ApiKeyHeaderName, StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!destination.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) &&
                destination.Content is not null)
            {
                destination.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            request.Headers.Remove(header.Key);
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (var header in source.Headers)
        {
            if (!ResponseHeadersToDrop.Contains(header.Key))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in source.Content.Headers)
        {
            if (!ResponseHeadersToDrop.Contains(header.Key))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }

        destination.Headers.Remove("transfer-encoding");
    }
}
