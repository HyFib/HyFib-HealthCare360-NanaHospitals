using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// An S3-compatible store in memory, for the off-site uploads: one bucket, with single PUTs, multipart uploads and HEAD.
/// It checks each request as S3 does: the SigV4 signature recomputed from what arrived, the payload's SHA-256 against
/// <c>x-amz-content-sha256</c>, and the body's MD5 against <c>Content-MD5</c> (refused as <c>BadDigest</c>), and it keeps
/// the Object Lock headers each object was created with. <see cref="Fault"/> makes a request fail, as a store or a network
/// does; <see cref="TamperETag"/> makes HEAD answer with a copy that does not match.
/// </summary>
internal sealed class FakeS3(string bucket, S3Credentials credentials, string region = "ap-south-1") : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private int _nextUpload;

    /// <summary>The objects stored, by key.</summary>
    public Dictionary<string, StoredObject> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every request that reached the store, in order (those a fault failed included).</summary>
    public List<SeenRequest> Requests { get; } = [];

    /// <summary>Answers instead of the store when it returns a response, or throws (a network failure); null lets the store answer.</summary>
    public Func<SeenRequest, HttpResponseMessage?>? Fault { get; set; }

    /// <summary>When set, HEAD reports this ETag for every object: what the store holds is not what was sent.</summary>
    public string? TamperETag { get; set; }

    /// <summary>
    /// Joining an upload answers as S3 does for a large one: spaces first, sent to keep the connection open while it works,
    /// then the XML with its declaration.
    /// </summary>
    public bool KeepAliveWhileJoining { get; set; }

    /// <summary>The parts a multipart upload holds now, by upload id.</summary>
    public IReadOnlyDictionary<string, int> PartsHeld
    {
        get
        {
            lock (_gate)
            {
                return _uploads.ToDictionary(u => u.Key, u => u.Value.Parts.Count, StringComparer.Ordinal);
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var uri = request.RequestUri!;
        var query = ParseQuery(uri.Query);
        var key = KeyOf(uri);
        var seen = new SeenRequest(request.Method.Method, key, query, Headers(request), body.Length);
        lock (_gate)
        {
            Requests.Add(seen);
        }

        if (Fault?.Invoke(seen) is { } faulted)
        {
            return faulted;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return Error(HttpStatusCode.BadRequest, "InsecureTransport", "Requests must use https.");
        }

        if (Unsigned(request, uri, query, body) is { } refused)
        {
            return refused;
        }

        lock (_gate)
        {
            return request.Method.Method switch
            {
                "PUT" when query.ContainsKey("partNumber") => UploadPart(query, body),
                "PUT" => Put(key, seen.Headers, body),
                "POST" when query.ContainsKey("uploads") => Create(key, seen.Headers),
                "POST" when query.ContainsKey("uploadId") => Complete(key, query["uploadId"], body),
                "DELETE" when query.ContainsKey("uploadId") => Abort(query["uploadId"]),
                "HEAD" => Head(key),
                _ => Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed", "Not in this fake."),
            };
        }
    }

    /// <summary>A failure as the store would answer it.</summary>
    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message) => new(status)
    {
        Content = new StringContent($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{message}</Message></Error>", Encoding.UTF8, "application/xml"),
    };

    private HttpResponseMessage Put(string key, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        var md5 = Convert.ToHexStringLower(Md5(body));
        Objects[key] = new StoredObject(body, md5, Metadata(headers), LockOf(headers));
        return WithETag(new HttpResponseMessage(HttpStatusCode.OK), md5);
    }

    private HttpResponseMessage Create(string key, IReadOnlyDictionary<string, string> headers)
    {
        var id = $"upload-{++_nextUpload}.{Guid.NewGuid():N}";
        _uploads[id] = new Upload(key, Metadata(headers), LockOf(headers), []);
        return Xml(new XElement("InitiateMultipartUploadResult", new XElement("Bucket", bucket), new XElement("Key", key), new XElement("UploadId", id)));
    }

    private HttpResponseMessage UploadPart(IReadOnlyDictionary<string, string> query, byte[] body)
    {
        if (!_uploads.TryGetValue(query["uploadId"], out var upload))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchUpload", "The specified upload does not exist.");
        }

        var number = int.Parse(query["partNumber"], CultureInfo.InvariantCulture);
        upload.Parts[number] = body;
        return WithETag(new HttpResponseMessage(HttpStatusCode.OK), Convert.ToHexStringLower(Md5(body)));
    }

    private HttpResponseMessage Complete(string key, string uploadId, byte[] body)
    {
        if (!_uploads.Remove(uploadId, out var upload))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchUpload", "The specified upload does not exist.");
        }

        var listed = XElement.Parse(Encoding.UTF8.GetString(body)).Elements("Part")
            .Select(p => (Number: int.Parse(p.Element("PartNumber")!.Value, CultureInfo.InvariantCulture), ETag: p.Element("ETag")!.Value.Trim('"')))
            .ToList();
        foreach (var (number, etag) in listed)
        {
            if (!upload.Parts.TryGetValue(number, out var part) || Convert.ToHexStringLower(Md5(part)) != etag)
            {
                return Error(HttpStatusCode.BadRequest, "InvalidPart", $"Part {number} was not uploaded with that ETag.");
            }
        }

        var data = listed.SelectMany(p => upload.Parts[p.Number]).ToArray();
        var digests = listed.SelectMany(p => Md5(upload.Parts[p.Number])).ToArray();
        var etagAll = $"{Convert.ToHexStringLower(Md5(digests))}-{listed.Count}";
        Objects[key] = new StoredObject(data, etagAll, upload.Metadata, upload.Lock);
        var result = new XElement("CompleteMultipartUploadResult", new XElement("Bucket", bucket), new XElement("Key", key), new XElement("ETag", $"\"{etagAll}\""));
        return KeepAliveWhileJoining
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"      \n      \n<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n{result}", Encoding.UTF8, "application/xml"),
            }
            : Xml(result);
    }

    private HttpResponseMessage Abort(string uploadId)
    {
        _uploads.Remove(uploadId);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Head(string key)
    {
        if (!Objects.TryGetValue(key, out var stored))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        }

        var response = WithETag(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }, TamperETag ?? stored.ETag);
        response.Content.Headers.ContentLength = stored.Data.Length;
        foreach (var (name, value) in stored.Metadata)
        {
            response.Headers.TryAddWithoutValidation(S3Client.MetadataPrefix + name, value);
        }

        return response;
    }

    /// <summary>The store's refusal of a request that is not signed, or whose body does not match its checksums; null when it is fine.</summary>
    private HttpResponseMessage? Unsigned(HttpRequestMessage request, Uri uri, IReadOnlyDictionary<string, string> query, byte[] body)
    {
        var headers = Headers(request);
        if (!headers.TryGetValue("authorization", out var authorization) || !headers.TryGetValue(SigV4.DateHeader, out var amzDate)
            || !headers.TryGetValue(SigV4.ContentSha256Header, out var payloadHash))
        {
            return Error(HttpStatusCode.Forbidden, "AccessDenied", "Unsigned request.");
        }

        if (payloadHash != SigV4.HexSha256(body))
        {
            return Error(HttpStatusCode.BadRequest, "XAmzContentSHA256Mismatch", "The provided 'x-amz-content-sha256' header does not match what was computed.");
        }

        if (body.Length > 0 && (!headers.TryGetValue("content-md5", out var md5) || md5 != Convert.ToBase64String(Md5(body))))
        {
            return Error(HttpStatusCode.BadRequest, "BadDigest", "The Content-MD5 you specified did not match what we received.");
        }

        var signedNames = authorization.Split("SignedHeaders=")[1].Split(',')[0].Split(';');
        var signed = signedNames.Select(name => new KeyValuePair<string, string>(name, headers.GetValueOrDefault(name) ?? string.Empty)).ToList();
        var at = DateTimeOffset.ParseExact(amzDate, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var expected = SigV4.Authorization(credentials, region, SigV4.S3Service, at, request.Method.Method, uri.AbsolutePath, SigV4.CanonicalQuery(query), signed, payloadHash);
        return expected == authorization ? null : Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", "The request signature we calculated does not match the signature you provided.");
    }

    private string KeyOf(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        return uri.Host.StartsWith(bucket + ".", StringComparison.Ordinal) ? path : path[(bucket.Length + 1)..];
    }

    private static Dictionary<string, string> Headers(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name.ToLowerInvariant()] = string.Join(',', values);
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name.ToLowerInvariant()] = string.Join(',', values);
            }
        }

        headers["host"] = request.Headers.Host ?? request.RequestUri!.Authority;
        return headers;
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty, StringComparer.Ordinal);

    private static Dictionary<string, string> Metadata(IReadOnlyDictionary<string, string> headers) =>
        headers.Where(h => h.Key.StartsWith(S3Client.MetadataPrefix, StringComparison.Ordinal))
            .ToDictionary(h => h.Key[S3Client.MetadataPrefix.Length..], h => h.Value, StringComparer.Ordinal);

    private static ObjectLock? LockOf(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue("x-amz-object-lock-mode", out var mode) ? new ObjectLock(mode, headers.GetValueOrDefault("x-amz-object-lock-retain-until-date")) : null;

    private static HttpResponseMessage WithETag(HttpResponseMessage response, string etag)
    {
        response.Headers.TryAddWithoutValidation("ETag", $"\"{etag}\"");
        return response;
    }

    private static HttpResponseMessage Xml(XElement element) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new XDocument(element).ToString(), Encoding.UTF8, "application/xml"),
    };

    /// <summary>An object as the store holds it, with the metadata and the lock it was created with.</summary>
    public sealed record StoredObject(byte[] Data, string ETag, IReadOnlyDictionary<string, string> Metadata, ObjectLock? Lock);

    /// <summary>The Object Lock headers an object was created with.</summary>
    public sealed record ObjectLock(string Mode, string? RetainUntil);

    /// <summary>A request as it arrived: method, object key, query and headers (names in lower case), and its body's length.</summary>
    public sealed record SeenRequest(string Method, string Key, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, int BodyLength)
    {
        public int? PartNumber => Query.TryGetValue("partNumber", out var n) ? int.Parse(n, CultureInfo.InvariantCulture) : null;
    }

    private sealed record Upload(string Key, IReadOnlyDictionary<string, string> Metadata, ObjectLock? Lock, Dictionary<int, byte[]> Parts);

    /// <summary>The MD5 S3 checks bodies against and builds ETags from, computed here apart from the client under test.</summary>
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Primitives", Justification = "S3's Content-MD5 and ETags are MD5 by the protocol.")]
    public static byte[] Md5(byte[] data) => MD5.HashData(data);
}
