using System.Globalization;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;

namespace HyFib.HealthCare360.Platform.Localization;

/// <summary>
/// Implemented by the Patients module: the free-text "preferred language" on a patient's record, or null when the
/// patient (or the field) is missing.
/// </summary>
public interface IPatientLanguageSource
{
    Task<string?> PreferredLanguageAsync(Guid patientId, CancellationToken cancellationToken);
}

internal sealed class NullPatientLanguageSource : IPatientLanguageSource
{
    public Task<string?> PreferredLanguageAsync(Guid patientId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary>The language of the text the server writes for the person behind this request (set by <see cref="RequestLanguageMiddleware"/>).</summary>
public sealed record RequestLanguage(string Code);

public static class RequestLanguageExtensions
{
    /// <summary>
    /// The language to write this request's answer in for a patient: the patient's (or the portal's) language, English
    /// for staff and for anything the middleware did not see.
    /// </summary>
    public static string PatientLanguage(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<RequestLanguage>()?.Code ?? PatientLanguages.Default;
    }

    /// <summary>
    /// The language to write for a patient who is not on the line (a document sent in a message): the one their record's
    /// preference names, else English.
    /// </summary>
    public static async Task<string> LanguageOfAsync(this IPatientLanguageSource source, Guid? patientId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var preference = patientId is { } id ? await source.PreferredLanguageAsync(id, cancellationToken) : null;
        return PatientLanguages.FromPreference(preference) ?? PatientLanguages.Default;
    }
}

/// <summary>
/// Picks the language of the server's text for patient-facing requests (SRS PP-06): the portal's API
/// (<c>/api/v1/portal</c>), the public pages' (<c>/api/v1/public</c>), OTP sign-in, any call the portal app makes
/// (<c>X-HC360-Client: portal</c>) and any call made with a patient's sign-in. The language is the signed-in patient's
/// preferred language on their record when HC360 writes it, else the most preferred one of Accept-Language that it
/// writes, else English. Staff requests stay in English. The preference is cached for five minutes per patient.
/// It runs after authentication, so it knows the patient, and sets <see cref="CultureInfo.CurrentUICulture"/> for the
/// request (the validators' built-in messages follow it); number and date formats (CurrentCulture) are left alone.
/// </summary>
public sealed class RequestLanguageMiddleware(RequestDelegate next, IMemoryCache cache)
{
    private static readonly TimeSpan PreferenceLifetime = TimeSpan.FromMinutes(5);
    private static readonly string[] PatientPaths = ["/api/v1/portal", "/api/v1/public", "/api/v1/auth/otp"];

    public async Task InvokeAsync(HttpContext context, IPatientLanguageSource preferences)
    {
        if (!IsForPatients(context))
        {
            await next(context);
            return;
        }

        var language = await PreferenceAsync(context, preferences)
            ?? PatientLanguages.FromAcceptLanguage(context.Request.Headers.AcceptLanguage)
            ?? PatientLanguages.Default;
        context.Features.Set(new RequestLanguage(language));

        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = PatientLanguages.CultureOf(language);
        try
        {
            await next(context);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    internal static bool IsForPatients(HttpContext context) =>
        IsPatient(context)
        || string.Equals(context.Request.Headers[AuthService.ClientHeader], "portal", StringComparison.OrdinalIgnoreCase)
        || PatientPaths.Any(path => context.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase));

    private static bool IsPatient(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
        && string.Equals(context.User.FindFirst(HcClaims.UserType)?.Value, HcClaims.PatientType, StringComparison.Ordinal);

    private async Task<string?> PreferenceAsync(HttpContext context, IPatientLanguageSource preferences)
    {
        if (!IsPatient(context) || !Guid.TryParse(context.User.FindFirst(HcClaims.Patient)?.Value, out var patientId))
        {
            return null;
        }

        var key = ("patient-language", patientId);
        if (!cache.TryGetValue(key, out string? preference))
        {
            preference = await preferences.PreferredLanguageAsync(patientId, context.RequestAborted);
            cache.Set(key, preference, PreferenceLifetime);
        }

        return PatientLanguages.FromPreference(preference);
    }
}

/// <summary>
/// Writes the Problem Details of a patient-facing request in the patient's language (<see cref="RequestLanguage"/>):
/// the title, the detail and every field message that <see cref="PatientTexts"/> translates. The machine-readable
/// <c>code</c>, the field names and anything without a translation stay as they are, and the answer says which language
/// it is in (Content-Language). Staff requests are not touched.
/// </summary>
public static class ProblemLocalization
{
    public static void Localize(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var language = context.HttpContext.Features.Get<RequestLanguage>()?.Code;
        if (language is null || language == PatientLanguages.English)
        {
            return;
        }

        Translate(context.ProblemDetails, language, PatientTexts.Catalog);
        context.HttpContext.Response.Headers[HeaderNames.ContentLanguage] = language;
    }

    /// <summary>Translates the problem's texts in place.</summary>
    public static void Translate(ProblemDetails problem, string language, PatientTexts texts)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(texts);
        problem.Title = texts.TranslateMessage(language, problem.Title) ?? problem.Title;
        problem.Detail = texts.TranslateMessage(language, problem.Detail) ?? problem.Detail;
        if (problem is HttpValidationProblemDetails validation)
        {
            foreach (var (field, messages) in validation.Errors.ToList())
            {
                validation.Errors[field] = [.. messages.Select(message => texts.TranslateMessage(language, message) ?? message)];
            }
        }
    }
}
