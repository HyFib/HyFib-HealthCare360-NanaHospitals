using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// Masks personal identifiers and secrets in free text that is about to be written somewhere people read later: every
/// server log line, and provider error text kept in the database. It reaches what code does not control, such as an SMS
/// provider's error message quoting the phone number, or an exception carrying a request's values.
/// <list type="bullet">
/// <item>Mobile, Aadhaar and ABHA numbers keep their last digits: enough to tell records apart, not to use them.</item>
/// <item>E-mail and ABHA addresses keep their domain.</item>
/// <item>Tokens, passwords and keys are replaced entirely.</item>
/// </list>
/// Identifiers inside GUIDs and longer codes are left alone, so log lines still correlate.
/// </summary>
public static partial class SensitiveText
{
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = Jwt().Replace(text, "[token]");
        redacted = AuthorizationValue().Replace(redacted, "${scheme} ***");
        redacted = SecretValue().Replace(redacted, "${name}${sep}${quote}***");
        redacted = Email().Replace(redacted, m => m.Value[0] + "***@" + m.Groups["domain"].Value);
        redacted = AbhaAddress().Replace(redacted, "***@${domain}");
        // Longest first, and mobiles before Aadhaar: +91 and a mobile number is twelve digits too.
        redacted = AbhaNumber().Replace(redacted, m => "**-****-****-" + LastDigits(m.Value, 4));
        redacted = Mobile().Replace(redacted, m => "******" + LastDigits(m.Value, 4));
        redacted = Aadhaar().Replace(redacted, m => "XXXX-XXXX-" + LastDigits(m.Value, 4));
        return redacted;
    }

    /// <summary><see cref="Redact(string)"/> for optional text.</summary>
    public static string? RedactOrNull(string? text) => text is null ? null : Redact(text);

    private static string LastDigits(string value, int count)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length <= count ? digits : digits[^count..];
    }

    // A JSON Web Token: three base64url parts, the first two starting as JSON objects do.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.None, 1000)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AuthorizationValue();

    // name=value, name: value or "name":"value" for the names secrets travel under (query strings, forms, JSON, headers).
    [GeneratedRegex(
        @"(?<name>\b(?:access_token|refresh_token|id_token|token|api_?key|apikey|key_secret|client_secret|secret|password|passwd|pwd|auth_token|signature|sig|otp)\b""?)(?<sep>\s*[=:]\s*)(?<quote>[""']?)[^\s&""',;}]+",
        RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SecretValue();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@(?<domain>[A-Za-z0-9.-]+\.[A-Za-z]{2,})", RegexOptions.None, 1000)]
    private static partial Regex Email();

    // ABHA address (Health ID): name@abdm, or @sbx on the ABDM sandbox.
    [GeneratedRegex(@"(?<![\w.@])[A-Za-z0-9._]+@(?<domain>abdm|sbx)\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AbhaAddress();

    // ABHA number: 14 digits, written 12-3456-7890-1234 or run together.
    [GeneratedRegex(@"(?<![\w-])(?:\d{2}[ -]\d{4}[ -]\d{4}[ -]\d{4}|\d{14})(?![\w-])", RegexOptions.None, 1000)]
    private static partial Regex AbhaNumber();

    // Aadhaar: 12 digits starting 2–9, written in groups of four or run together.
    [GeneratedRegex(@"(?<![\w-])[2-9]\d{3}[ -]?\d{4}[ -]?\d{4}(?![\w-])", RegexOptions.None, 1000)]
    private static partial Regex Aadhaar();

    // Indian mobile: 10 digits starting 6–9, with or without +91 or 0, run together or as 98765 43210.
    [GeneratedRegex(@"(?<![\w+-])(?:\+91[ -]?|0)?[6-9]\d{4}[ -]?\d{5}(?![\w-])", RegexOptions.None, 1000)]
    private static partial Regex Mobile();
}
