using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The off-site uploads sign every request with AWS Signature Version 4, written in HC360 without an AWS SDK. These are
/// AWS's own published test vectors: four from the Signature Version 4 test suite (service "service", region us-east-1,
/// 30 August 2015) and the four worked examples of S3's documentation on signing the Authorization header (bucket
/// examplebucket, 24 May 2013). Each signature here is AWS's, not one this code computed.
/// </summary>
public sealed class SigV4Tests
{
    private static readonly S3Credentials SuiteKey = new("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");
    private static readonly S3Credentials S3DocsKey = new("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
    private static readonly DateTimeOffset SuiteTime = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset S3DocsTime = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("get-vanilla", "GET", "", "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31")]
    [InlineData("post-vanilla", "POST", "", "5da7c1a2acd57cee7505fc6676e4e544621c30862966e37dddb68e92efbe5d6b")]
    [InlineData("get-vanilla-query-order-key-case", "GET", "Param2=value2&Param1=value1", "b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500")]
    [InlineData("get-vanilla-empty-query-key", "GET", "Param1=value1", "a67d582fa61cc504c4bae71f336f98b97f1ea3c7a6bfe1b6e45aec72011b9aeb")]
    public void The_signature_version_4_test_suite(string vector, string method, string query, string signature)
    {
        var parameters = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).Select(p => new KeyValuePair<string, string>(p[0], p[1]));

        var authorization = SigV4.Authorization(SuiteKey, "us-east-1", "service", SuiteTime, method, "/", SigV4.CanonicalQuery(parameters),
            [new("Host", "example.amazonaws.com"), new("X-Amz-Date", "20150830T123600Z")], SigV4.EmptyPayloadHash);

        authorization.ShouldBe(
            $"AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature={signature}", vector);
    }

    [Fact]
    public void S3_get_object_with_a_range()
    {
        var authorization = SigV4.Authorization(S3DocsKey, "us-east-1", "s3", S3DocsTime, "GET", "/test.txt", string.Empty,
        [
            new("Host", "examplebucket.s3.amazonaws.com"),
            new("Range", "bytes=0-9"),
            new("x-amz-content-sha256", SigV4.EmptyPayloadHash),
            new("x-amz-date", "20130524T000000Z"),
        ], SigV4.EmptyPayloadHash);

        authorization.ShouldBe("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");
    }

    /// <summary>The object key has a <c>$</c> in it, which S3 wants encoded once, in the request and in the signature alike.</summary>
    [Fact]
    public void S3_put_object_whose_key_needs_encoding()
    {
        var payload = SigV4.HexSha256(Encoding.UTF8.GetBytes("Welcome to Amazon S3."));
        payload.ShouldBe("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072");
        var (uri, path) = new S3Location(new Uri("https://s3.amazonaws.com"), "us-east-1", "examplebucket", PathStyle: false).Address("test$file.text", string.Empty);
        (uri.Host, path).ShouldBe(("examplebucket.s3.amazonaws.com", "/test%24file.text"));

        var authorization = SigV4.Authorization(S3DocsKey, "us-east-1", "s3", S3DocsTime, "PUT", path, string.Empty,
        [
            new("Date", "Fri, 24 May 2013 00:00:00 GMT"),
            new("Host", uri.Host),
            new("x-amz-date", "20130524T000000Z"),
            new("x-amz-storage-class", "REDUCED_REDUNDANCY"),
            new("x-amz-content-sha256", payload),
        ], payload);

        authorization.ShouldBe("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class, Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd");
    }

    /// <summary>A sub-resource without a value (<c>?lifecycle</c>, as <c>?uploads</c> starts a multipart upload) is signed as <c>lifecycle=</c>.</summary>
    [Fact]
    public void S3_get_bucket_lifecycle()
    {
        var query = SigV4.CanonicalQuery([new("lifecycle", string.Empty)]);
        query.ShouldBe("lifecycle=");

        var authorization = SigV4.Authorization(S3DocsKey, "us-east-1", "s3", S3DocsTime, "GET", "/", query,
            [new("Host", "examplebucket.s3.amazonaws.com"), new("x-amz-content-sha256", SigV4.EmptyPayloadHash), new("x-amz-date", "20130524T000000Z")],
            SigV4.EmptyPayloadHash);

        authorization.ShouldEndWith("Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543");
    }

    [Fact]
    public void S3_list_objects_with_parameters_in_order()
    {
        var authorization = SigV4.Authorization(S3DocsKey, "us-east-1", "s3", S3DocsTime, "GET", "/",
            SigV4.CanonicalQuery([new("prefix", "J"), new("max-keys", "2")]),
            [new("Host", "examplebucket.s3.amazonaws.com"), new("x-amz-content-sha256", SigV4.EmptyPayloadHash), new("x-amz-date", "20130524T000000Z")],
            SigV4.EmptyPayloadHash);

        authorization.ShouldEndWith("Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7");
    }

    [Fact]
    public void Values_are_encoded_as_signature_version_4_asks()
    {
        SigV4.Encode("a b/c+d=e~f_g.h-i").ShouldBe("a%20b%2Fc%2Bd%3De~f_g.h-i");
        SigV4.EncodePath("hc360/2026 11/ünïcode").ShouldBe("hc360/2026%2011/%C3%BCn%C3%AFcode");
        SigV4.CanonicalQuery([new("uploadId", "a/b+c=="), new("partNumber", "7")]).ShouldBe("partNumber=7&uploadId=a%2Fb%2Bc%3D%3D");
        SigV4.AmzDate(new DateTimeOffset(2026, 11, 14, 7, 0, 5, TimeSpan.FromHours(5.5))).ShouldBe("20261114T013005Z");
    }

    /// <summary>The bucket goes in the path for path-style stores (MinIO, OCI), and in the host otherwise; https always.</summary>
    [Fact]
    public void A_location_names_the_bucket_in_the_host_or_the_path()
    {
        var hosted = new S3Location(new Uri("https://s3.ap-south-1.amazonaws.com"), "ap-south-1", "nana-backups", PathStyle: false)
            .Address("hc360/hc360-20261114-013000-scheduled.hc360bak", "uploads=");
        hosted.Uri.ToString().ShouldBe("https://nana-backups.s3.ap-south-1.amazonaws.com/hc360/hc360-20261114-013000-scheduled.hc360bak?uploads=");

        var pathStyle = new S3Location(new Uri("https://objects.example.in:9000"), "in-west-1", "nana.backups", PathStyle: true)
            .Address("hc360-20261114-013000-scheduled.hc360bak", string.Empty);
        (pathStyle.Uri.ToString(), pathStyle.Path).ShouldBe(
            ("https://objects.example.in:9000/nana.backups/hc360-20261114-013000-scheduled.hc360bak", "/nana.backups/hc360-20261114-013000-scheduled.hc360bak"));

        Should.Throw<ArgumentException>(() => new S3Client(new HttpClient(), new S3Location(new Uri("http://s3.example.in"), "x", "b", true),
            S3DocsKey, TimeProvider.System));
    }

    /// <summary>The access key never reaches a log line through ToString.</summary>
    [Fact]
    public void Credentials_do_not_write_out_their_secret()
    {
        S3DocsKey.ToString().ShouldNotContain(S3DocsKey.SecretAccessKey);
        string.Create(CultureInfo.InvariantCulture, $"{S3DocsKey}").ShouldContain("AKIAIOSFODNN7EXAMPLE");
        (new BackupOffsiteSecrets { AccessKeyId = "AKIA", SecretAccessKey = "very-secret" }.ToString() ?? string.Empty).ShouldNotContain("very-secret");
    }
}
