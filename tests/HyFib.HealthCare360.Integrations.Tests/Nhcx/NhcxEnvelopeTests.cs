using System.Security.Cryptography;
using HyFib.HealthCare360.Integrations.Nhcx;

namespace HyFib.HealthCare360.Integrations.Tests.Nhcx;

/// <summary>
/// The NHCX envelope against node-jose 2.2.0, the library the HCX reference applications encrypt with
/// (<c>hcx-platform/demo-app/server/util/jose.js</c>): the two messages below were sealed by it, one under each key
/// algorithm the HCX sources use, and must open here with their headers and Tamil text intact.
/// </summary>
public sealed class NhcxEnvelopeTests
{
    /// <summary>A key made for these tests only (RSA 2048), the recipient's.</summary>
    private const string RecipientKey = """
        -----BEGIN PRIVATE KEY-----
        MIIEvAIBADANBgkqhkiG9w0BAQEFAASCBKYwggSiAgEAAoIBAQCyi1RFLomOVu22
        GuEgH72h876cKl5jyshtb3O3YYpezbuGR9zX+Omeu0voPANw1gRUc2vorjslNeSO
        L7l/6IE7OB4KsCEqvqGFyLZNzzR58i+WRTLOyMRTDJSR0oGRlXQWklARjJMu5Eat
        Mdqd97H8kwyIZEXrJMqoRn/8pUZd36QnG6Au5VS8IXfsUHuYYf1aZYOSJmXfk3vS
        knK8D4m9ANRhVGbCFmAxW/2XLEjxKWGcxJmz1GS/N7WSi2gw25aB+xy5Ycl3FhWB
        YJqAiTyg6dg/BUZYrx3UsSAn8pMoULF+fvAGCOjhbX6LBILShBUBSIjfyKQQf/ZK
        /zQT67pPAgMBAAECggEAAhBq91Mevf7dSAIENkXdAGFRWSbYPoPFUME9b5JOHOMj
        Vt2/0jNg0rf28bFil3cHR8lUfF/Uno0tSO4zpnnpUeZH2qHHUGIx0IUjLmpwgIm/
        Pm0rCt72qMB4JejEHLXDSNDaN3RjmI2FD6gy5H6iQnmaEdLu8qqojnd6L/vigLVP
        NSFO0ZIeisgQ3aLsjLSa0rwztg6MV2cPXIDHIbvUcJ15k5uRR8lwweccK0Esgbd8
        +HXxEe72VBa0qVACx4ITTLNGx+wtgz9Kk5dw+fj+DIhdvf4Wl/HY09U9xY3PSXvC
        3qaahh0nLPqZmmy9L4HY3c62yYVlIEmAMRyr/QG1MQKBgQDxvXHtGpp0F4UGOw+a
        hyIjCaJqGnyVIZu4Kj96EclHQsE5eloRi2pPVr6eMqcrtFwL+6H/dNdmqv2Miv28
        4LVRoTvh1Xz3XMVwSQhfxdrzfJG5TACL4XiyLd1Bbg8EGWRGH240zqi13+yHzHFl
        8oaS2edvTCT5cb3rQ4VmW6m/GQKBgQC9E44Cv0fMZ1y0PhuJdN8nPLTB+1lD+b/e
        Yo+wEs4HT5eAaDlxBnMeQM/OGAsblcdbuoF63Y4xMblJkohYPqD7+MRcYrmmfEDv
        7lfP7py1bIKZ0PMvlgk4vXE67HysBFIAdlTsh4/3uEJ8FfhFOYRr25xdouUb6stX
        hVNdM6+5pwKBgFSL5nF/sW9S3oJ8W4u3IXhRoU0ssE/ULkst8391Awl6TmwU37Vt
        JiRTeV2I57DAZsUGH/VhXwiYWw8xeWb6zE+dbX0hlpBJEQChel1MrLQl6C1/aF2D
        4RuoMM72eCf93XUs/vVXJeheRbKFDVATj3+C2I+zeOcJIdxLhDd1bZvZAoGASEOp
        CzPAZUc8D0eNjsO0ZnK1Q06ll21k/8H72PfW9sRcyx8KlUA5z15GDibyV+CvCUgH
        JMZ5CiRPBOeUZOXK8YLqycSR4CetSgZ2WWqBSnXmlJNf3tghPiZpmHO6UqR0Eukn
        HB/TzoIGJxbehNau3epZoRAZMMpUXWb/raeLihECgYBktbaxGjg/8IxzTYjqYqV/
        lg6iYa3p+ezYbGSn3GIcqeiMwNnA+SFqr9/bAzKd4faGNtImvPrFvlOLbZsVWnL6
        TKrUr+Ad37ONyopfYcWKVAYu1ic5l7kDrvoxUWXNy/phD4guchKlHE6plcRTQUEd
        p5O8KLq6ITP1U0STeUVD1Q==
        -----END PRIVATE KEY-----
        """;

    private const string Payload = "{\"resourceType\":\"Bundle\",\"type\":\"collection\",\"entry\":[{\"resource\":{\"resourceType\":\"Patient\",\"name\":[{\"text\":\"மீனா குமார்\"}]}}]}";

    private const string SealedRsaOaep =
        "eyJhbGciOiJSU0EtT0FFUCIsIngtaGN4LXNlbmRlcl9jb2RlIjoiaG9zcC1uYW5hLTAxQGhjeCIsIngtaGN4LXJlY2lwaWVudF9jb2RlIjoicG"
        + "F5ZXItc3Rhci0wMUBoY3giLCJ4LWhjeC1hcGlfY2FsbF9pZCI6IjI2YjEwNjBjLTFlODMtNDYwMC05NjEyLWVhMzFlMGNhNTA5MSIsIngtaGN4"
        + "LWNvcnJlbGF0aW9uX2lkIjoiNWU5MzRmOTAtMTExZC00ZjBiLWIwMTYtYzIyZDgyMDY3NGUxIiwieC1oY3gtd29ya2Zsb3dfaWQiOiIyOWMwNm"
        + "U2OC04M2E5LTQzNDAtYjAwMi1iYTNiOGFmNmZmOWYiLCJ4LWhjeC10aW1lc3RhbXAiOiIyMDI2LTEwLTI2VDEwOjE1OjMwKzA1OjMwIiwieC1o"
        + "Y3gtc3RhdHVzIjoicmVxdWVzdC5xdWV1ZWQiLCJlbmMiOiJBMjU2R0NNIiwia2lkIjoiSEMzNjAgTkhDWCB0ZXN0IHJlY2lwaWVudCJ9.VS6je"
        + "JVaPfu3t6V6XdTZ6z2uAXzBFezIHCLDBtGShnCLKaAN5CL7tZK-hLO64-3_NGtIUw0SP0De8V_Uu5PuAllWY1hK9rPqSNoabvQvV8ouP7AdMen"
        + "_dEJi8tDnktHm8cfM3-LkUtBhFdSznTNHD5LMoAGnW76fy-XnfIF0MUL1I_Zi-_cGxp7bEuNKVvJWkvWMrmfWl5vSSBKpbTfA3dVTzJhLf6YwX"
        + "NiUhO9-eeS65sfVPUei0-q_cTR4UgSb-DH4Hr_y7NaA4GgTGboXHCEF9tbGk0ovE4Cutdoc_J3d_iJcZCXuRnZQoEnnDRrovADWyqsybpCwj5k"
        + "AsyVg1A.tV1dFilv37fx8YSD.AuOQw_2mF8SXwsTsPSipbxMVvLkwwl9phrV67ApimeJ_Ad1GazVkEAJ4jbom9mRgI_QwYo2e37Vt77UbMDf8o"
        + "yfug1zCjfVoXU0C5g3I-envRRNsKojKq1RR0x7nnwf8_8W0smpU3n21_PrilFhM-WGUurxPMzVzJHs0giqJw7t_MABQlx8dalvuOAonEo0ITnW"
        + "G.nQdpBk7IP9_xKvzOf9vFDw";

    private const string SealedRsaOaep256 =
        "eyJhbGciOiJSU0EtT0FFUC0yNTYiLCJ4LWhjeC1zZW5kZXJfY29kZSI6Imhvc3AtbmFuYS0wMUBoY3giLCJ4LWhjeC1yZWNpcGllbnRfY29kZS"
        + "I6InBheWVyLXN0YXItMDFAaGN4IiwieC1oY3gtYXBpX2NhbGxfaWQiOiIyNmIxMDYwYy0xZTgzLTQ2MDAtOTYxMi1lYTMxZTBjYTUwOTEiLCJ4"
        + "LWhjeC1jb3JyZWxhdGlvbl9pZCI6IjVlOTM0ZjkwLTExMWQtNGYwYi1iMDE2LWMyMmQ4MjA2NzRlMSIsIngtaGN4LXdvcmtmbG93X2lkIjoiMj"
        + "ljMDZlNjgtODNhOS00MzQwLWIwMDItYmEzYjhhZjZmZjlmIiwieC1oY3gtdGltZXN0YW1wIjoiMjAyNi0xMC0yNlQxMDoxNTozMCswNTozMCIs"
        + "IngtaGN4LXN0YXR1cyI6InJlcXVlc3QucXVldWVkIiwiZW5jIjoiQTI1NkdDTSIsImtpZCI6IkhDMzYwIE5IQ1ggdGVzdCByZWNpcGllbnQifQ"
        + ".o3Yhgmsj8sAowghmbj5YBws9AlT001NXJfWYXaF9e2h2AvDji4aCCcGoJUIq2G8rrQR7oWFLdvZr15U7UOgr1P34v4vuKNAUiSItjDIko8ZEE"
        + "90ckdvmmpBkC7oKUYhGD4AZ34SrvIcBEmTCDFsSTDvSUKIqaFi2SZvog48sQ_V4jB1hvTKSuotgix3sc3ddgc9PVi0kTFh3jY-xHg3nCWtkeyO"
        + "S2PMjklrj1vnQj3K-ZQuKEdqMIi9_7yU3bYDVb1pd55JGRmXyLXKCY1OnlASqtLcLC9lPdHmxRbj_ZLhKq_TAYDQzHXwN0JzAywpCakLkYVClO"
        + "axREPl9IEZldA.gU9uFs2JVyYi3_RW.-HUfAqsMtaeMusPGvHaZVugWBYHAYbVJTk9NXKnKtEeYVM_rXY_PyLzQlt2cndtMykVF46GS0aXPvkw"
        + "EoqDkpk7Hn4FGcf2zlnprbCAgkD41F7xRdGDc0QJyt7IhaZlqM1oIzWOZ8uROnzRwF-_nIRy9fc4qBkjiZlKog9IPi2b_h2qoQtJn4IrqhYyhF"
        + "7P_ro-W.8X45L_fP02Lay0xAQNWgIA";

    private static readonly DateTimeOffset Sealed = new(2026, 10, 26, 10, 15, 30, TimeSpan.FromHours(5.5));

    private static RSA Key()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(RecipientKey);
        return rsa;
    }

    [Theory]
    [InlineData(SealedRsaOaep, "RSA-OAEP")]
    [InlineData(SealedRsaOaep256, "RSA-OAEP-256")]
    public void A_message_sealed_by_the_reference_library_opens_here(string sealedMessage, string algorithm)
    {
        using var key = Key();
        var opened = NhcxEnvelope.Open(sealedMessage, key).Value;
        opened.Payload.ShouldBe(Payload);
        ((string?)opened.ProtectedHeader["alg"]).ShouldBe(algorithm);
        opened.Headers.ShouldBe(new NhcxHeaders("hosp-nana-01@hcx", "payer-star-01@hcx", Guid.Parse("26b1060c-1e83-4600-9612-ea31e0ca5091"),
            Guid.Parse("5e934f90-111d-4f0b-b016-c22d820674e1"), Guid.Parse("29c06e68-83a9-4340-b002-ba3b8af6ff9f"), Sealed, "request.queued"));
    }

    [Theory]
    [InlineData(NhcxKeyAlgorithm.RsaOaep256)]
    [InlineData(NhcxKeyAlgorithm.RsaOaep)]
    public void A_message_sealed_here_opens_with_the_recipients_key_only(NhcxKeyAlgorithm algorithm)
    {
        using var key = Key();
        var headers = new NhcxHeaders("hosp-nana-01@hcx", "payer-star-01@hcx", Guid.NewGuid(), Guid.NewGuid(), null, Sealed, NhcxEnvelope.RequestQueued);
        var sealedMessage = NhcxEnvelope.Seal(Payload, headers, key, algorithm);
        NhcxEnvelope.Open(sealedMessage, key).Value.ShouldSatisfyAllConditions(
            o => o.Payload.ShouldBe(Payload),
            o => o.Headers.ShouldBe(headers),
            o => o.ProtectedHeader.ContainsKey("x-hcx-workflow_id").ShouldBeFalse());

        using var someoneElse = RSA.Create(2048);
        NhcxEnvelope.Open(sealedMessage, someoneElse).Error!.Code.ShouldBe("nhcx.not_for_us");
    }

    [Fact]
    public void A_changed_header_or_payload_is_detected()
    {
        using var key = Key();
        var parts = SealedRsaOaep256.Split('.');

        // The recipient code changed in transit: the tag covers the protected header.
        var header = System.Text.Json.Nodes.JsonNode.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]))!;
        header["x-hcx-recipient_code"] = "payer-other@hcx";
        var forged = string.Join('.', [System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(header.ToJsonString())), .. parts[1..]]);
        NhcxEnvelope.Open(forged, key).Error!.Code.ShouldBe("nhcx.altered");

        var cipher = System.Buffers.Text.Base64Url.DecodeFromChars(parts[3]);
        cipher[0] ^= 1;
        NhcxEnvelope.Open(string.Join('.', parts[0], parts[1], parts[2], System.Buffers.Text.Base64Url.EncodeToString(cipher), parts[4]), key)
            .Error!.Code.ShouldBe("nhcx.altered");
        NhcxEnvelope.Open("a.b.c", key).Error!.Code.ShouldBe("nhcx.malformed");
    }

    [Fact]
    public void The_headers_are_checked_as_the_gateway_checks_them()
    {
        var now = Sealed.AddMinutes(1);
        var headers = new NhcxHeaders("hosp", "payer", Guid.NewGuid(), Guid.NewGuid(), null, Sealed, NhcxEnvelope.RequestQueued);
        NhcxEnvelope.Validate(headers, isResponse: false, now, TimeSpan.FromHours(24)).IsSuccess.ShouldBeTrue();
        NhcxEnvelope.Validate(headers with { Status = "response.complete" }, isResponse: false, now, TimeSpan.FromHours(24)).IsFailure.ShouldBeTrue();
        NhcxEnvelope.Validate(headers with { Status = null }, isResponse: true, now, TimeSpan.FromHours(24)).IsFailure.ShouldBeTrue();
        NhcxEnvelope.Validate(headers with { Timestamp = now.AddSeconds(5) }, isResponse: false, now, TimeSpan.FromHours(24))
            .Error!.Code.ShouldBe("nhcx.timestamp");
        NhcxEnvelope.Validate(headers with { Timestamp = now.AddHours(-25) }, isResponse: false, now, TimeSpan.FromHours(24))
            .Error!.Code.ShouldBe("nhcx.timestamp");
        NhcxEnvelope.Validate(headers with { RecipientCode = " " }, isResponse: false, now, TimeSpan.FromHours(24)).IsFailure.ShouldBeTrue();
    }
}
