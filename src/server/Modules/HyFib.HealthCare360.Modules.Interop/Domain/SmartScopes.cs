namespace HyFib.HealthCare360.Modules.Interop.Domain;

/// <summary>
/// The context a SMART on FHIR scope is granted in: one patient chosen when the app was authorized (<c>patient/</c>),
/// whatever the signed-in user may read (<c>user/</c>), or a backend service acting as its own account (<c>system/</c>).
/// </summary>
public enum SmartContext
{
    Patient,
    User,
    System,
}

/// <summary>
/// One granted resource scope: a context, a resource type (or every type) and whether it reads by id, searches, or
/// both. This API is read-only, so only SMART v2's <c>r</c> and <c>s</c> (and v1's <c>read</c>, which is both) are ever
/// granted.
/// </summary>
public sealed record SmartScope(SmartContext Context, string ResourceType, bool Read, bool Search)
{
    public const string AnyType = "*";

    public bool Covers(string type) => ResourceType == AnyType || string.Equals(ResourceType, type, StringComparison.Ordinal);

    /// <summary>The scope as granted, in SMART v2's form (<c>patient/Observation.rs</c>).</summary>
    public override string ToString() =>
        $"{SmartScopes.ContextName(Context)}/{ResourceType}.{(Read ? "r" : string.Empty)}{(Search ? "s" : string.Empty)}";
}

/// <summary>
/// SMART App Launch 2.0 scopes (<c>patient/*.rs</c>, <c>user/Observation.read</c>, <c>system/*.read</c>, plus
/// <c>launch/patient</c>): parsed strictly, and narrowed to what this read-only server can grant. A scope asking to
/// create, update or delete, or one with a v2 query (<c>?category=…</c>), is not granted; a scope this server does not
/// know is not granted either. What is granted is said back in the token response, as OAuth 2.0 requires when it differs
/// from what was asked.
/// </summary>
public static class SmartScopes
{
    /// <summary>Asks to choose a patient when the app is authorized (a standalone launch).</summary>
    public const string LaunchPatient = "launch/patient";

    public static string ContextName(SmartContext context) => context switch
    {
        SmartContext.Patient => "patient",
        SmartContext.User => "user",
        _ => "system",
    };

    /// <summary>
    /// Parses one resource scope. True with the scope when it can be granted (read, search or both); false for anything
    /// else: an unknown context or type, a write permission alone, a v2 query, or a malformed string.
    /// </summary>
    public static bool TryParse(string value, out SmartScope scope)
    {
        scope = null!;
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var dot = value.LastIndexOf('.');
        if (slash <= 0 || dot <= slash + 1 || dot == value.Length - 1 || value.Contains('?', StringComparison.Ordinal))
        {
            return false;
        }

        SmartContext? context = value[..slash] switch
        {
            "patient" => SmartContext.Patient,
            "user" => SmartContext.User,
            "system" => SmartContext.System,
            _ => null,
        };
        var type = value[(slash + 1)..dot];
        var permissions = value[(dot + 1)..];
        if (context is null || (type != SmartScope.AnyType && !FhirResourceTypes.IsServed(type)))
        {
            return false;
        }

        bool read, search;
        if (permissions == "read")
        {
            (read, search) = (true, true);
        }
        else if (permissions is "write" or "*")
        {
            return false;
        }
        else
        {
            // SMART v2: the letters of c, r, u, d, s in that order, each at most once. Only r and s can be granted here.
            const string order = "cruds";
            var at = -1;
            foreach (var letter in permissions)
            {
                var next = order.IndexOf(letter, StringComparison.Ordinal);
                if (next <= at)
                {
                    return false;
                }

                at = next;
            }

            (read, search) = (permissions.Contains('r', StringComparison.Ordinal), permissions.Contains('s', StringComparison.Ordinal));
            if (!read && !search)
            {
                return false;
            }
        }

        scope = new SmartScope(context.Value, type, read, search);
        return true;
    }

    /// <summary>
    /// What may be granted of a space-separated scope string, given the contexts the grant allows and the scopes the
    /// client was registered for (a registered scope covers a requested one when it is the same context, covers its type
    /// and allows each of its permissions). Unknown and ungrantable scopes are dropped.
    /// </summary>
    public static SmartGrantRequest Narrow(string? requested, IReadOnlyCollection<SmartContext> contexts, IReadOnlyCollection<SmartScope> registered)
    {
        var granted = new List<SmartScope>();
        var launchPatient = false;
        foreach (var item in (requested ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal))
        {
            if (item == LaunchPatient)
            {
                launchPatient = contexts.Contains(SmartContext.Patient);
                continue;
            }

            if (!TryParse(item, out var scope) || !contexts.Contains(scope.Context))
            {
                continue;
            }

            var allowed = registered.FirstOrDefault(r => r.Context == scope.Context && r.Covers(scope.ResourceType) && (!scope.Read || r.Read) && (!scope.Search || r.Search));
            if (allowed is null)
            {
                continue;
            }

            if (!granted.Contains(scope))
            {
                granted.Add(scope);
            }
        }

        return new SmartGrantRequest(granted, launchPatient);
    }

    /// <summary>The scope string said back to the client: the granted scopes and <c>launch/patient</c> when it was used.</summary>
    public static string Format(IEnumerable<SmartScope> scopes, bool launchPatient) =>
        string.Join(' ', (launchPatient ? [LaunchPatient] : Array.Empty<string>()).Concat(scopes.Select(s => s.ToString())));

    /// <summary>Parses a stored scope string (one this server formatted); <c>launch/patient</c> is skipped.</summary>
    public static IReadOnlyList<SmartScope> ParseStored(string stored) =>
        [.. stored.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => TryParse(s, out var scope) ? scope : null).OfType<SmartScope>()];
}

/// <summary>The scopes that can be granted of a request, and whether the app asked to choose a patient.</summary>
public sealed record SmartGrantRequest(IReadOnlyList<SmartScope> Scopes, bool LaunchPatient);

/// <summary>
/// What a SMART access token lets its bearer do through the FHIR API, on top of everything the account it acts as may
/// do (its permissions, its facility, and each patient's record-sharing consent, which still apply to every read): the
/// resource types it may read and search, and the one patient whose record it is limited to, for a <c>patient/</c>
/// grant.
/// </summary>
public sealed record SmartAccess(Guid ClientId, IReadOnlyList<SmartScope> Scopes, Guid? PatientId)
{
    /// <summary>
    /// True when a granted scope lets the bearer read (or search) the type, for the record of <paramref name="patientId"/>
    /// (null: before the patient is known, or a directory entry, which is no patient's). A <c>patient/</c> scope reaches
    /// only the patient chosen when the app was authorized.
    /// </summary>
    public bool Allows(string type, bool search, Guid? patientId) =>
        Scopes.Any(s => (search ? s.Search : s.Read) && s.Covers(type)
                        && (s.Context != SmartContext.Patient || patientId is null || patientId == PatientId));

    /// <summary>True when only <c>patient/</c> scopes reach the type: every read of it is limited to the chosen patient.</summary>
    public bool LimitedToPatient(string type, bool search) =>
        PatientId is not null && !Scopes.Any(s => s.Context != SmartContext.Patient && (search ? s.Search : s.Read) && s.Covers(type));
}
