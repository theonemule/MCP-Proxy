using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AIGovernanceGateway.Models;

/// <summary>Minimal AWS Signature Version 4 signer used for Bedrock Runtime HTTP requests.</summary>
public static class AwsSigV4Signer
{
    /// <summary>Signs an HTTP request for the specified AWS service using the supplied payload bytes.</summary>
    public static void Sign(
        HttpRequestMessage request,
        ReadOnlySpan<byte> payload,
        AwsCredentialSet credentials,
        string service,
        DateTimeOffset now)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required for AWS signing.");
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = HexSha256(payload);

        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        if (!string.IsNullOrWhiteSpace(credentials.SessionToken))
        {
            request.Headers.Remove("x-amz-security-token");
            request.Headers.TryAddWithoutValidation("x-amz-security-token", credentials.SessionToken);
        }

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.Host : uri.Authority,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate
        };
        if (!string.IsNullOrWhiteSpace(credentials.SessionToken))
        {
            headers["x-amz-security-token"] = credentials.SessionToken!;
        }

        var canonicalHeaders = string.Concat(headers.Select(x => $"{x.Key}:{NormalizeHeaderValue(x.Value)}\n"));
        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalRequest = string.Join("\n",
            request.Method.Method.ToUpperInvariant(),
            CanonicalizePath(uri),
            CanonicalizeQuery(uri),
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var algorithm = "AWS4-HMAC-SHA256";
        var scope = $"{dateStamp}/{credentials.Region}/{service}/aws4_request";
        var stringToSign = string.Join("\n", algorithm, amzDate, scope, HexSha256(Encoding.UTF8.GetBytes(canonicalRequest)));

        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretKey), dateStamp);
        var kRegion = Hmac(kDate, credentials.Region);
        var kService = Hmac(kRegion, service);
        var kSigning = Hmac(kService, "aws4_request");
        var signature = Convert.ToHexString(Hmac(kSigning, stringToSign)).ToLowerInvariant();

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            algorithm,
            $"Credential={credentials.AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static string CanonicalizePath(Uri uri)
    {
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        return string.IsNullOrEmpty(path) ? "/" : "/" + path;
    }

    private static string CanonicalizeQuery(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query) || uri.Query == "?")
        {
            return "";
        }

        return string.Join("&", uri.Query[1..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var split = part.Split('=', 2);
                var name = Uri.UnescapeDataString(split[0]);
                var value = split.Length == 2 ? Uri.UnescapeDataString(split[1]) : "";
                return (Name: AwsEncode(name), Value: AwsEncode(value));
            })
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Value, StringComparer.Ordinal)
            .Select(x => $"{x.Name}={x.Value}"));
    }

    private static string AwsEncode(string value) =>
        Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHeaderValue(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string HexSha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
}