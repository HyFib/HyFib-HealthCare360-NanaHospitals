using HyFib.HealthCare360.Modules.Interop.Abdm;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// The ABDM callback check against tokens signed by jwcrypto 1.5.6 (Python), independent of .NET: a key set and a valid
/// gateway token, and the tokens that must be refused (expired, signed by a key not in the set, an unknown key id,
/// <c>alg: none</c>, and HS256 keyed with the public key, the algorithm-confusion attack).
/// </summary>
public sealed class AbdmCallbackVerifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793000000);

    private const string Jwks = "{\"keys\":[{\"alg\":\"RS256\",\"e\":\"AQAB\",\"kid\":\"gateway-2026\",\"kty\":\"RSA\",\"n\":\"18295dYhIDZim_TGFvX6csGU0c3EmO47kH3Aqh-3uImEJIT3wnSI0YnV3aCdf-sygY-J0YjTBFq2Y4_Svu5AFOBqwLh9vOlzqIaRlUaYKdUIjkz8Uc6VfjelhRNx6p6Yy4iwCh8Ytat9YR9iGSjS51UohEEtXliZRv5bBhxYbDl9o4s0DuDIrjmHI7pT7Nq3KfArP3Cyq0qvThdsXRzgBwjfA9DqwbDIJ7wYDO7-y6moEzm84bgt6w7C9FMGYNGVHPpu-Pnkr4UoAjhe_C-QiQEGlRmQBDfHFbuQTeu0SmZs_wOHMtXVkf6ZO41XPQL4kzj_3VE0vQhCibLgt7uWwQ\",\"use\":\"sig\"}]}";

    private const string Valid =
        "eyJhbGciOiJSUzI1NiIsImtpZCI6ImdhdGV3YXktMjAyNiIsInR5cCI6IkpXVCJ9.eyJjbGllbnRJZCI6ImdhdGV3YXkiLCJleHAiOjE3OTMwM"
        + "DA2MDAsImlhdCI6MTc5Mjk5OTk0MCwiaXNzIjoiaHR0cHM6Ly9kZXYuYWJkbS5nb3YuaW4vZ2F0ZXdheSIsInN1YiI6ImdhdGV3YXkifQ.aTP_"
        + "363-2POFd2HVubgX7h381yXKPkuuMQ_n9H4I-mE_1-4qYxHp8fpNvb_0mvyDzBT8MZzFJgYFz_XfJZJn-i5NiIPekbMEx9CLVb0YZ4V1Ym1R2n"
        + "rHslJweqoQv8Ha_XR2hU5GavoZ4TU9XurAoVR5n79zE5B7emntJmE8RIPCo2xtu3rM0OFBK0qFAvJ68vyjFHAso3xqeVdYIZ96oHoSQ6uqMHWl"
        + "_YNGkW1t5vLAfUQ_e_GAlqHPBs95P1RC1raNZ_uWnHUhy-M6aQLubpUHrS_aTzYvu8yZgI_iBL16y9UDulvPjPIi0N4OhZNc00Bd83Y9YbdNCW"
        + "i7JZcNmg";

    private const string Expired =
        "eyJhbGciOiJSUzI1NiIsImtpZCI6ImdhdGV3YXktMjAyNiIsInR5cCI6IkpXVCJ9.eyJjbGllbnRJZCI6ImdhdGV3YXkiLCJleHAiOjE3OTI5O"
        + "TY0MDAsImlhdCI6MTc5Mjk5MjgwMCwiaXNzIjoiaHR0cHM6Ly9kZXYuYWJkbS5nb3YuaW4vZ2F0ZXdheSIsInN1YiI6ImdhdGV3YXkifQ.ammk"
        + "zu2SMg8n7UxojhuFN-HHLsX4JaE0KnbBjcWUrb8TPKPyMC0idV5YpXN8VmjiBz3zUV7B6WCV2KNttdFVVaNdPpoR6G16jHuvMHfMbfO5KWAFbo"
        + "tQ2TIKqfM5CiKceLPFh8FPupg0eO_R4idjAM0HnXxOsw7AJdafFkVmU1sO15G_x1D6YogpBNZtWW5Ej37yhRlj6KI5OSVDPRpH8Xl41qythTGK"
        + "PHmhYgDlS440etZ9U2qq9nSbMIkElylxLtVQz3FVDWsHXTVsYORMJwfNqiygwYvgCw_8xf8wwoN91zGwI_T9WbmUJDUomu4KYSGZGQSc_s7nnX"
        + "kM4oWy4Q";

    private const string WrongKey =
        "eyJhbGciOiJSUzI1NiIsImtpZCI6ImdhdGV3YXktMjAyNiIsInR5cCI6IkpXVCJ9.eyJjbGllbnRJZCI6ImdhdGV3YXkiLCJleHAiOjE3OTMwM"
        + "DA2MDAsImlhdCI6MTc5Mjk5OTk0MCwiaXNzIjoiaHR0cHM6Ly9kZXYuYWJkbS5nb3YuaW4vZ2F0ZXdheSIsInN1YiI6ImdhdGV3YXkifQ.WyZL"
        + "pXNnlxMVg-ilWycxFwxqN2dm7fLc_wmjcYjJ4bHXLvaCP8Pc7ZrnHXJFxfSC_Keht5C7HVeAoya9ThCFShVSPI9G17dUYLmL1tgDuvBIcWsQCr"
        + "M3M3rJ5S2J_WjiYwndlu8V4M7Mvw862kNgYb_GBxkILbX7cBGffhBSgx2CKYPhSfz7kKDw-dHiI1FOhUVeWkyTBpMANMNkxWgHQ_HXjfABl6yt"
        + "R05rZehPaAp6IcRSwkADPmTr-ryMfZS0focOlW_hbZ2uDHZP-v3tD71A168lSKzSNZBCmo3qGdsA17BKvPcpA_TgzMkoJhzuJmbzK6shscah7Q"
        + "WJ3wf_8Q";

    private const string UnknownKid =
        "eyJhbGciOiJSUzI1NiIsImtpZCI6InJldGlyZWQtMjAyNSIsInR5cCI6IkpXVCJ9.eyJjbGllbnRJZCI6ImdhdGV3YXkiLCJleHAiOjE3OTMwM"
        + "DA2MDAsImlhdCI6MTc5Mjk5OTk0MCwiaXNzIjoiaHR0cHM6Ly9kZXYuYWJkbS5nb3YuaW4vZ2F0ZXdheSIsInN1YiI6ImdhdGV3YXkifQ.hvP9"
        + "uhXmXpwGGGX9U4PoRlizDnavt21UZeV9PBDCOFeJI1WqaugFq6H7OAvXA-3jUnrXnbADz77iYDJU65WCojhgwazbJI7LCa4b-G16J67zXfmn5T"
        + "6sBeV9_T0VE-XMKBGrAJmap_WbiT1HjHOL_P2umNotmb_s163HNraff3Eblfs74s3LfFc3Stzquh0InFOoP75RRNEZcR8IA-XhHPVvqG9S28eF"
        + "8ymcnuCciDIE_mWTsm-Mnxztc8-BLM0Gcu_60POdccHydf7Zv0vfanXYU1l0b3n6-qqTC_-xVGwufJ3hGinPjvAhQs0eA3SjQlUr2a3UcsAdQd"
        + "5tlL6eYA";

    private const string AlgNone =
        "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJpc3MiOiJodHRwczovL2Rldi5hYmRtLmdvdi5pbi9nYXRld2F5Iiwic3ViIjoiZ2F0ZXdheS"
        + "IsImlhdCI6MTc5Mjk5OTk0MCwiZXhwIjoxNzkzMDAwNjAwLCJjbGllbnRJZCI6ImdhdGV3YXkifQ.";

    private const string HmacWithPublicKey =
        "eyJhbGciOiJIUzI1NiIsImtpZCI6ImdhdGV3YXktMjAyNiIsInR5cCI6IkpXVCJ9.eyJjbGllbnRJZCI6ImdhdGV3YXkiLCJleHAiOjE3OTMwM"
        + "DA2MDAsImlhdCI6MTc5Mjk5OTk0MCwiaXNzIjoiaHR0cHM6Ly9kZXYuYWJkbS5nb3YuaW4vZ2F0ZXdheSIsInN1YiI6ImdhdGV3YXkifQ.BQmF"
        + "wDSvFaFKZIuURYoiIv4T1QA-01HAuwOzsd1-Dmg";

    [Fact]
    public async Task The_gateways_token_is_accepted_with_its_claims()
    {
        var claims = (await AbdmCallbackVerifier.VerifyAsync($"Bearer {Valid}", Jwks, null, null, Now)).Value;
        claims["clientId"].ShouldBe("gateway");
        (await AbdmCallbackVerifier.VerifyAsync($"bearer  {Valid}", Jwks, "https://dev.abdm.gov.in/gateway", null, Now)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Expired, "expired")]
    [InlineData(WrongKey, "signature")]
    [InlineData(UnknownKid, "key")]
    [InlineData(AlgNone, "")]
    [InlineData(HmacWithPublicKey, "")]
    public async Task Any_other_token_is_refused(string token, string reason)
    {
        var refused = await AbdmCallbackVerifier.VerifyAsync($"Bearer {token}", Jwks, null, null, Now);
        refused.Error!.Code.ShouldBe("abdm.callback_unauthenticated");
        refused.Error.Message.ShouldContain(reason);
    }

    [Fact]
    public async Task A_call_without_a_token_or_from_another_issuer_is_refused()
    {
        (await AbdmCallbackVerifier.VerifyAsync(null, Jwks, null, null, Now)).IsFailure.ShouldBeTrue();
        (await AbdmCallbackVerifier.VerifyAsync(Valid, Jwks, null, null, Now)).IsFailure.ShouldBeTrue();
        (await AbdmCallbackVerifier.VerifyAsync($"Bearer {Valid}", Jwks, "https://someone.else", null, Now)).Error!.Message.ShouldContain("issued");
        (await AbdmCallbackVerifier.VerifyAsync($"Bearer {Valid}", Jwks, null, null, Now.AddMinutes(12))).Error!.Message.ShouldContain("expired");
        (await AbdmCallbackVerifier.VerifyAsync($"Bearer {Valid}", "not json", null, null, Now)).IsFailure.ShouldBeTrue();
    }
}
