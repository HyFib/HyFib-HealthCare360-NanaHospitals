using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// An access key for an S3-compatible store. A class rather than a record, so that the secret never ends up in a log
/// line through a generated <c>ToString</c>: only signatures made with it leave the server.
/// </summary>
internal sealed class S3Credentials(string accessKeyId, string secretAccessKey)
{
    public string AccessKeyId { get; } = accessKeyId;

    public string SecretAccessKey { get; } = secretAccessKey;

    public override string ToString() => $"S3 access key {AccessKeyId}";
}

/// <summary>
/// AWS Signature Version 4, the <c>AWS4-HMAC-SHA256</c> Authorization header that S3 and every S3-compatible store
/// (OCI, MinIO and the stores hosted in India) take. HC360 has no AWS SDK: this is the whole of what an upload needs, and
/// it is checked against the test vectors AWS publishes (SigV4Tests). Paths are URI-encoded once, as S3 wants (other AWS
/// services encode them twice, which no store HC360 talks to does).
/// </summary>
internal static class SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string DateHeader = "x-amz-date";
    public const string ContentSha256Header = "x-amz-content-sha256";
    public const string S3Service = "s3";

    /// <summary>The SHA-256 of an empty payload, in hex, as a request without a body signs it.</summary>
    public static readonly string EmptyPayloadHash = HexSha256([]);

    /// <summary>The <c>x-amz-date</c> form of <paramref name="at"/>: <c>20261114T013000Z</c>.</summary>
    public static string AmzDate(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The Authorization header for a request: <paramref name="headers"/> are the headers it signs (always <c>host</c>
    /// and <c>x-amz-date</c>, which must equal <see cref="AmzDate"/> of <paramref name="at"/>), with the values the request
    /// sends; <paramref name="canonicalPath"/> is the path as sent, already encoded (<see cref="EncodePath"/>), and
    /// <paramref name="canonicalQuery"/> the query in canonical form (<see cref="CanonicalQuery"/>).
    /// </summary>
    public static string Authorization(
        S3Credentials credentials, string region, string service, DateTimeOffset at, string method, string canonicalPath, string canonicalQuery,
        IEnumerable<KeyValuePair<string, string>> headers, string payloadHash)
    {
        var signed = headers
            .Select(h => new KeyValuePair<string, string>(h.Key.Trim().ToLowerInvariant(), Collapse(h.Value)))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .ToList();
        var signedHeaders = string.Join(';', signed.Select(h => h.Key));
        var canonicalRequest = new StringBuilder()
            .Append(method).Append('\n')
            .Append(canonicalPath).Append('\n')
            .Append(canonicalQuery).Append('\n');
        foreach (var (name, value) in signed)
        {
            canonicalRequest.Append(name).Append(':').Append(value).Append('\n');
        }

        canonicalRequest.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);

        var amzDate = AmzDate(at);
        var scope = $"{amzDate[..8]}/{region}/{service}/aws4_request";
        var stringToSign = $"{Algorithm}\n{amzDate}\n{scope}\n{HexSha256(Encoding.UTF8.GetBytes(canonicalRequest.ToString()))}";
        var key = SigningKey(credentials.SecretAccessKey, amzDate[..8], region, service);
        try
        {
            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
            return $"{Algorithm} Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The query as SigV4 signs it: each name and value encoded, sorted by name then value, a bare name as <c>name=</c>.</summary>
    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join('&', parameters
            .Select(p => (Name: Encode(p.Key), Value: Encode(p.Value)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}"));

    /// <summary>A path encoded as S3 wants it in the request and the signature: every byte but the unreserved ones and <c>/</c>.</summary>
    public static string EncodePath(string path) => Encode(path, keepSlash: true);

    /// <summary>RFC 3986 encoding of everything but the unreserved characters (A-Z, a-z, 0-9, <c>-</c>, <c>_</c>, <c>.</c>, <c>~</c>), in upper-case hex.</summary>
    public static string Encode(string value, bool keepSlash = false)
    {
        var encoded = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' || (keepSlash && c == '/'))
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }

    public static string HexSha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] SigningKey(string secret, string date, string region, string service)
    {
        var dateKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secret), Encoding.UTF8.GetBytes(date));
        var regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(region));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(service));
        var signingKey = HMACSHA256.HashData(serviceKey, "aws4_request"u8);
        CryptographicOperations.ZeroMemory(dateKey);
        CryptographicOperations.ZeroMemory(regionKey);
        CryptographicOperations.ZeroMemory(serviceKey);
        return signingKey;
    }

    /// <summary>A header value as signed: trimmed, with each run of spaces inside it made one.</summary>
    private static string Collapse(string value)
    {
        var trimmed = value.Trim();
        var collapsed = new StringBuilder(trimmed.Length);
        var space = false;
        foreach (var c in trimmed)
        {
            if (c == ' ')
            {
                if (!space)
                {
                    collapsed.Append(c);
                }

                space = true;
                continue;
            }

            space = false;
            collapsed.Append(c);
        }

        return collapsed.ToString();
    }
}

/// <summary>
/// Where objects go: the store's https address, its region, the bucket, and whether the bucket is named in the path
/// (<c>https://store/bucket/key</c>, which MinIO-based stores and OCI want, and buckets with dots need) or in the host
/// (<c>https://bucket.s3.ap-south-1.amazonaws.com/key</c>, AWS's default).
/// </summary>
internal sealed record S3Location(Uri Endpoint, string Region, string Bucket, bool PathStyle)
{
    /// <summary>The request address of <paramref name="key"/> with <paramref name="query"/>, and the encoded path it is signed with.</summary>
    public (Uri Uri, string Path) Address(string key, string query)
    {
        var path = PathStyle ? $"/{SigV4.EncodePath(Bucket)}/{SigV4.EncodePath(key)}" : $"/{SigV4.EncodePath(key)}";
        var host = PathStyle ? Endpoint.Authority : $"{Bucket}.{Endpoint.Authority}";
        var uri = new Uri($"https://{host}{path}{(query.Length == 0 ? string.Empty : "?" + query)}", UriKind.Absolute);
        return (uri, path);
    }

    /// <summary>A name for the location in messages and in a multipart upload's saved state, without credentials.</summary>
    public string Describe(string key) => PathStyle ? $"{Endpoint.Authority}/{Bucket}/{key}" : $"{Bucket}.{Endpoint.Authority}/{key}";
}

/// <summary>Object Lock in compliance mode: nobody, the hospital's own credentials and the store's root account included, deletes or overwrites the object before <paramref name="RetainUntil"/>.</summary>
internal sealed record S3ObjectLock(DateTimeOffset RetainUntil);

/// <summary>What a store says of an object: its size, its ETag (unquoted) and the <c>x-amz-meta-*</c> metadata it was given.</summary>
internal sealed record S3ObjectInfo(long Length, string? ETag, IReadOnlyDictionary<string, string> Metadata);

/// <summary>A part of a multipart upload as the store acknowledged it.</summary>
internal sealed record S3Part(int Number, string ETag);

/// <summary>An S3 error answer: the HTTP status, and the store's code (<c>NoSuchUpload</c>, <c>AccessDenied</c>, <c>BadDigest</c>) and message.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only the client makes these, always with the store's answer.")]
internal sealed class S3Exception(HttpStatusCode status, string? code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    public string? Code { get; } = code;

    /// <summary>The multipart upload is gone: aborted, or removed by the bucket's clean-up of unfinished uploads.</summary>
    public bool NoSuchUpload => string.Equals(Code, "NoSuchUpload", StringComparison.Ordinal);
}

/// <summary>
/// The few S3 operations an off-site backup copy needs (<see cref="BackupOffsiteUploader"/>), over HTTPS only: a small
/// file in one PUT, a large one in parts (create, upload each part, complete, or abort), and HEAD to check what arrived.
/// Every request is signed with SigV4 and carries its payload's SHA-256 (<c>x-amz-content-sha256</c>), and every request
/// with a body its MD5 (<c>Content-MD5</c>), which Object Lock requires and the store checks: a body damaged on the way is
/// refused (<c>BadDigest</c>), not stored. Redirects are answers, never followed (<see cref="Http.OutboundHttp"/>).
/// </summary>
internal sealed class S3Client
{
    public const string MetadataPrefix = "x-amz-meta-";

    private const string LockModeHeader = "x-amz-object-lock-mode";
    private const string LockUntilHeader = "x-amz-object-lock-retain-until-date";
    private const string ContentType = "application/octet-stream";

    private readonly HttpClient _http;
    private readonly S3Location _location;
    private readonly S3Credentials _credentials;
    private readonly TimeProvider _time;

    public S3Client(HttpClient http, S3Location location, S3Credentials credentials, TimeProvider time)
    {
        if (location.Endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Off-site copies go over https only.", nameof(location));
        }

        _http = http;
        _location = location;
        _credentials = credentials;
        _time = time;
    }

    public S3Location Location => _location;

    /// <summary>Stores <paramref name="length"/> bytes of <paramref name="body"/> as <paramref name="key"/> in one request; returns the store's ETag.</summary>
    public async Task<string?> PutObjectAsync(
        string key, byte[] body, int length, S3ObjectLock? objectLock, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, key, [], body, length, ObjectHeaders(objectLock, metadata), ct);
        return ETagOf(response);
    }

    /// <summary>Starts a multipart upload of <paramref name="key"/>, locked and described as it will be once complete; returns its upload id.</summary>
    public async Task<string> CreateMultipartUploadAsync(
        string key, S3ObjectLock? objectLock, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, key, [new("uploads", string.Empty)], null, 0, ObjectHeaders(objectLock, metadata), ct);
        var result = await ReadXmlAsync(response, ct);
        return Child(result, "UploadId") is { Length: > 0 } uploadId
            ? uploadId
            : throw new S3Exception(response.StatusCode, null, "The store started the upload but gave no upload id.");
    }

    /// <summary>Uploads part <paramref name="number"/> (1 to 10,000) of an upload: <paramref name="length"/> bytes of <paramref name="body"/>. Returns its ETag.</summary>
    public async Task<string> UploadPartAsync(string key, string uploadId, int number, byte[] body, int length, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, key,
            [new("partNumber", number.ToString(CultureInfo.InvariantCulture)), new("uploadId", uploadId)], body, length, [], ct);
        return ETagOf(response) ?? throw new S3Exception(response.StatusCode, null, $"The store took part {number} but gave no ETag for it.");
    }

    /// <summary>Joins the parts into the object; returns its ETag. A store may answer 200 with an error in the body, which is thrown.</summary>
    public async Task<string?> CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<S3Part> parts, CancellationToken ct)
    {
        var document = new XDocument(new XElement("CompleteMultipartUpload",
            parts.OrderBy(p => p.Number).Select(p => new XElement("Part",
                new XElement("PartNumber", p.Number.ToString(CultureInfo.InvariantCulture)),
                new XElement("ETag", Quoted(p.ETag))))));
        var body = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        using var response = await SendAsync(HttpMethod.Post, key, [new("uploadId", uploadId)], body, body.Length, [], ct);
        var result = await ReadXmlAsync(response, ct);
        if (result.Name.LocalName == "Error")
        {
            throw Failure(response.StatusCode, result);
        }

        return Child(result, "ETag") is { } etag ? Unquoted(etag) : null;
    }

    /// <summary>Gives up an upload: the store drops the parts it holds. A completed object is not touched.</summary>
    public async Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, [new("uploadId", uploadId)], null, 0, [], ct);
    }

    /// <summary>What the store holds as <paramref name="key"/>, or null when it holds nothing there.</summary>
    public async Task<S3ObjectInfo?> HeadObjectAsync(string key, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Head, key, [], null, 0, [], ct);
            var metadata = response.Headers
                .Where(h => h.Key.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key[MetadataPrefix.Length..].ToLowerInvariant(), h => string.Join(',', h.Value), StringComparer.Ordinal);
            return new S3ObjectInfo(response.Content.Headers.ContentLength ?? -1, ETagOf(response), metadata);
        }
        catch (S3Exception ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>The headers that lock and describe an object, on the request that creates it.</summary>
    private static List<KeyValuePair<string, string>> ObjectHeaders(S3ObjectLock? objectLock, IReadOnlyDictionary<string, string> metadata)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (objectLock is not null)
        {
            headers.Add(new(LockModeHeader, "COMPLIANCE"));
            headers.Add(new(LockUntilHeader, objectLock.RetainUntil.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        }

        headers.AddRange(metadata.Select(m => new KeyValuePair<string, string>(MetadataPrefix + m.Key, m.Value)));
        return headers;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string key, IReadOnlyList<KeyValuePair<string, string>> query, byte[]? body, int length,
        IReadOnlyList<KeyValuePair<string, string>> extra, CancellationToken ct)
    {
        var canonicalQuery = SigV4.CanonicalQuery(query);
        var (uri, path) = _location.Address(key, canonicalQuery);
        var payloadHash = body is null ? SigV4.EmptyPayloadHash : SigV4.HexSha256(body.AsSpan(0, length));

        // One moment for the header and the signature: they must name the same second.
        var now = _time.GetUtcNow();
        var amzDate = SigV4.AmzDate(now);
        var host = uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";

        var request = new HttpRequestMessage(method, uri);
        var signed = new List<KeyValuePair<string, string>>
        {
            new("host", host),
            new(SigV4.DateHeader, amzDate),
            new(SigV4.ContentSha256Header, payloadHash),
        };
        signed.AddRange(extra);
        if (body is not null)
        {
            var content = new ByteArrayContent(body, 0, length);
            content.Headers.ContentType = new MediaTypeHeaderValue(method == HttpMethod.Post ? "application/xml" : ContentType);
            content.Headers.ContentMD5 = Md5(body.AsSpan(0, length));
            request.Content = content;
            signed.Add(new("content-md5", Convert.ToBase64String(content.Headers.ContentMD5)));
            signed.Add(new("content-type", content.Headers.ContentType.ToString()));
        }

        request.Headers.Host = host;
        foreach (var (name, value) in signed.Where(h => h.Key is not ("host" or "content-md5" or "content-type")))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.TryAddWithoutValidation("Authorization",
            SigV4.Authorization(_credentials, _location.Region, SigV4.S3Service, now, method.Method, path, canonicalQuery, signed, payloadHash));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        finally
        {
            request.Dispose();
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            XElement? error = null;
            if (method != HttpMethod.Head)
            {
                try
                {
                    error = await ReadXmlAsync(response, ct);
                }
                catch (S3Exception)
                {
                    // Not XML: the status alone says what went wrong.
                }
            }

            throw error is null
                ? new S3Exception(response.StatusCode, null, string.Create(CultureInfo.InvariantCulture,
                    $"The store answered {(int)response.StatusCode} {response.ReasonPhrase} for {method.Method} {_location.Describe(key)}."))
                : Failure(response.StatusCode, error);
        }
    }

    private static S3Exception Failure(HttpStatusCode status, XElement error)
    {
        var code = Child(error, "Code");
        var message = Child(error, "Message");
        var region = Child(error, "Region");
        return new S3Exception(status, code, string.Create(CultureInfo.InvariantCulture,
            $"The store answered {(int)status} {code ?? "error"}{(message is null ? string.Empty : ": " + message)}{(region is null ? string.Empty : $" (the bucket is in {region})")}"));
    }

    private static async Task<XElement> ReadXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // S3 sends spaces while it joins a large upload, to keep the connection open, before the XML and its declaration.
        var text = (await response.Content.ReadAsStringAsync(ct)).TrimStart();
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XElement.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new S3Exception(response.StatusCode, null, string.Create(CultureInfo.InvariantCulture,
                $"The store answered {(int)response.StatusCode} {response.ReasonPhrase} with something other than S3's XML ({ex.Message})."));
        }
    }

    /// <summary>A child element's text, whatever namespace the store writes its XML in.</summary>
    private static string? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static string? ETagOf(HttpResponseMessage response) =>
        response.Headers.ETag?.Tag is { } tag ? Unquoted(tag)
        : response.Headers.TryGetValues("ETag", out var values) ? Unquoted(values.First())
        : null;

    private static string Unquoted(string etag) => etag.Trim().Trim('"');

    private static string Quoted(string etag) => $"\"{Unquoted(etag)}\"";

    /// <summary>The MD5 S3 checks a body against (<c>Content-MD5</c>), and builds an object's ETag from.</summary>
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Primitives",
        Justification = "S3's Content-MD5 and ETag are MD5 by the protocol: a check against damage on the way, beside the SHA-256 each request is signed with.")]
    public static byte[] Md5(ReadOnlySpan<byte> data) => MD5.HashData(data);
}
