using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Validators;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// SRS PP-06: whatever the server can answer the patient portal with reaches the patient in their language. The
/// server translates a refusal's title, detail and field messages by their English text (<see cref="PatientTexts"/>),
/// so a message with no entry in a patient language arrives in English. A rule left with FluentValidation's own
/// message ("'Reason' must not be empty.") arrives half translated, with the field's English name in it.
/// <para>
/// The portal's endpoints are found from the server itself: every route under a path the server answers patients on
/// (<see cref="RequestLanguageMiddleware.PatientPathPrefixes"/>), and every endpoint the patients' portal permission
/// opens. Their messages are found from the compiled code: the checks start at each endpoint's handler and follow
/// every call into HC360's code (interface calls to each implementation, lambdas, async methods), then read the
/// messages at the source lines of the methods reached, which the debug symbols locate. A message built at run time
/// from a variable cannot be read this way; those places are listed in the test's output.
/// </para>
/// </summary>
public sealed partial class PortalMessageTests(ITestOutputHelper output)
{
    private static readonly string[] PatientLanguagesToCheck =
        [.. PatientLanguages.Codes.Where(code => code != PatientLanguages.English)];

    [Fact]
    public void Every_rule_of_a_request_the_portal_sends_answers_in_every_patient_language_without_the_fields_english_name()
    {
        using var scope = EndpointAuthorizationTests.App.Value.Services.CreateScope();
        var requests = PortalEndpoints()
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<ValidatedRequest>())
            .Select(validated => validated.RequestType)
            .Distinct()
            .ToList();
        var problems = new List<string>();
        var rules = 0;

        foreach (var request in requests)
        {
            var validator = (IValidator)scope.ServiceProvider.GetRequiredService(typeof(IValidator<>).MakeGenericType(request));
            var english = InCulture("en", () => RuleMessages(validator, scope.ServiceProvider, request.Name).ToList());
            rules += english.Count;
            foreach (var language in PatientLanguagesToCheck)
            {
                var written = InCulture(PatientLanguages.CultureOf(language).Name, () => RuleMessages(validator, scope.ServiceProvider, request.Name).ToList());
                foreach (var (rule, inLanguage) in english.Zip(written))
                {
                    if (inLanguage.Template == rule.Template)
                    {
                        // The rule's own message, written in English: the server translates it from the catalogue.
                        problems.AddRange(rule.Template.Contains("{PropertyName}", StringComparison.Ordinal)
                            ? [$"{rule.Property}: \"{rule.Template}\" names the field in English."]
                            : Untranslated(rule.Template).Where(missing => missing == language).Select(_ => $"{rule.Property}: \"{rule.Template}\" has no {language} entry."));
                    }
                    else if (inLanguage.Template.Contains("{PropertyName}", StringComparison.Ordinal))
                    {
                        problems.Add($"{rule.Property}: FluentValidation's own {language} message for {rule.Validator} names the field in English (\"{inLanguage.Template}\"). "
                            + $"Add validator.{rule.Validator} to the patient texts, or give the rule a WithMessage of its own.");
                    }
                }
            }
        }

        requests.Count.ShouldBeGreaterThan(10, "the portal's validated requests were not found");
        rules.ShouldBeGreaterThan(50);
        problems.ShouldBeEmpty(
            "Add the message to Platform/Localization/Resources/patient-texts-{en,hi,ta}.json (and to the review list in "
            + "docs/requirements/hindi-review.md):\n" + string.Join('\n', problems.Distinct().Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Every_message_the_portal_can_be_answered_with_is_in_every_patient_language()
    {
        var handlers = PortalEndpoints().Select(endpoint => endpoint.Metadata.GetMetadata<MethodInfo>()).OfType<MethodInfo>().Distinct().ToList();
        var graph = CallGraph.From(handlers);
        var found = SourceMessages(graph, out var unread);

        handlers.Count.ShouldBeGreaterThan(40, "the portal's endpoints were not found");
        found.Select(m => m.Text).ShouldContain(
            "This slot has just been booked. Choose another slot.", "the walk must reach the booking rules behind the portal's booking, or it checks nothing");
        output.WriteLine($"{handlers.Count} handlers, {graph.Reached.Count} methods reached, {found.Count} messages read.");
        var notReviewed = unread.Where(place => !BuiltAtRunTime.ContainsKey((place.File, place.Expression))).Select(place => place.ToString()).Distinct().Order(StringComparer.Ordinal).ToList();
        notReviewed.ShouldBeEmpty(
            "These messages are built at run time from something the check cannot follow. Make the message a literal or a constant, or "
            + "add the place to BuiltAtRunTime with why its text reaches no patient, or is checked elsewhere:\n" + string.Join('\n', notReviewed));
        var staleRunTime = BuiltAtRunTime.Keys.Where(key => !unread.Any(place => (place.File, place.Expression) == key)).ToList();
        staleRunTime.ShouldBeEmpty("In BuiltAtRunTime but no longer reached; take them off the list:\n"
            + string.Join('\n', staleRunTime.Select(key => $"{key.File}: {key.Expression}")));

        var stale = NeverSentToPatients.Keys.Where(key => !found.Any(m => FileOf(m) == key.File && m.Text == key.Text)).ToList();
        stale.ShouldBeEmpty("Listed as never sent to patients, but the portal's code no longer reaches them; take them off the list:\n"
            + string.Join('\n', stale.Select(key => $"{key.File}: \"{key.Text}\"")));

        var untranslated = found
            .Where(message => !NeverSentToPatients.ContainsKey((FileOf(message), message.Text)))
            .Select(message => (Message: message, Missing: Untranslated(message.Text).ToList()))
            .Where(message => message.Missing.Count > 0)
            .GroupBy(message => message.Message.Text, StringComparer.Ordinal)
            .Select(same => $"\"{same.Key}\" has no {string.Join(" or ", same.First().Missing)} entry. {same.First().Message.Where}, reached by {same.First().Message.Via}")
            .Order(StringComparer.Ordinal)
            .ToList();
        untranslated.ShouldBeEmpty(
            "Add the message to Platform/Localization/Resources/patient-texts-{en,hi,ta}.json (and to the review list in "
            + "docs/requirements/hindi-review.md):\n" + string.Join('\n', untranslated));
    }

    private const string ThePortalPaysABill =
        "The portal pays a bill by its id, with no IP account and no amount (PaymentGatewayService.CreatePortalOrderAsync).";

    private const string ThePatientGetsAMessageOfTheirOwn =
        "PrescriptionFinalCopies.PatientProblem keeps the problem's code and gives the patient a message of its own.";

    private const string ThePortalChecksAccessItself =
        "RefillService.RequestAsync checks a portal request with the portal's own access rule; this guard is for the pharmacy's requests.";

    /// <summary>
    /// Messages the portal's code reaches that are never sent to a patient, each for the reason given: the walk follows
    /// calls, not the values that decide which branch runs. Keyed by the file a message is in and its English text. A
    /// listed message the walk no longer finds fails the test, so the list stays true.
    /// </summary>
    private static readonly Dictionary<(string File, string Text), string> NeverSentToPatients = new()
    {
        [("PosService.cs", "Advances can be collected only while the patient is admitted.")] = ThePortalPaysABill,
        [("PosService.cs", "Choose a bill or an IP account.")] = ThePortalPaysABill,
        [("PosService.cs", "Enter the advance amount.")] = ThePortalPaysABill,
        [("PosService.cs", "The balance on this bill is ₹{x}.")] = ThePortalPaysABill,
        [("PosService.cs", "This stay is free of cost by law ({x}), so no advance is taken.")] = ThePortalPaysABill,
        [("PrescriptionDigitalSignatureService.cs", "HC360 cannot confirm the doctor's DSC on this prescription: its certificate does not chain to a CCA India "
            + "root installed on the server. It is issued only once that is fixed: ask IT to check the server's DSC trust folder.")] = ThePatientGetsAMessageOfTheirOwn,
        [("PrescriptionDigitalSignatureService.cs", "The doctor's DSC signature on this prescription does not check out: the prescription or its signature was "
            + "changed after signing. Do not issue it; repeat it to issue a new prescription.")] = ThePatientGetsAMessageOfTheirOwn,
        [("PrescriptionDigitalSignatureService.cs", "This hospital issues prescriptions only once the prescribing doctor has signed them with their DSC, in "
            + "HC360 Desktop.")] = ThePatientGetsAMessageOfTheirOwn,
        [("PatientQueries.cs", "Sign in first.")] = ThePortalChecksAccessItself,
        [("PatientQueries.cs", "This is a restricted record. Request emergency access (break-the-glass) if it is clinically necessary.")] = ThePortalChecksAccessItself,
    };

    /// <summary>
    /// The places whose message is built at run time from something the check cannot follow, each with why that is safe.
    /// Keyed by the file and the message's expression; a listed place no longer reached fails the test.
    /// </summary>
    private static readonly Dictionary<(string File, string Expression), string> BuiltAtRunTime = new()
    {
        [("ResultMapping.cs", "error.Message")] = "The message of the Error being answered: each one is read where the Error is made.",
        [("ResultMapping.cs", "title")] = "The title of each kind of refusal: The_answers_every_portal_request_can_get checks every one.",
        [("DocumentResults.cs", "detail")] = "Why a PDF could not be made: the portal shows its own words for pdf.unavailable (DocumentActions).",
    };

    private static string FileOf(Message message) => Path.GetFileName(message.Where[..message.Where.LastIndexOf(':')]);

    [Fact]
    public void The_walk_follows_a_call_through_an_interface_to_each_implementation()
    {
        // No portal message is reached only through an interface today; this keeps the walk able to find one that is.
        var graph = CallGraph.From([typeof(PortalMessageTests).GetMethod(nameof(AskTheProbe), BindingFlags.NonPublic | BindingFlags.Static)!]);

        SourceMessages(graph, out _).Select(m => m.Text).ShouldContain("Only the probe's implementation says this.");
    }

    private static Error? AskTheProbe(IProbe probe) => probe.Check();

    private interface IProbe
    {
        Error? Check();
    }

    private sealed class Probe : IProbe
    {
        public Error? Check() => Error.Rule("probe", "Only the probe's implementation says this.");
    }

    [Fact]
    public void The_answers_every_portal_request_can_get_are_in_every_patient_language()
    {
        // The title of each kind of refusal, the messages of a record that is not found or was changed meanwhile, the
        // answers of a switched-off feature (while the portal or video consultations are off, every patient request gets one),
        // and the answers to a write sent again with its Idempotency-Key, which every portal write carries.
        var idempotency = IdempotencyReplies().ToList();
        idempotency.ShouldContain(IdempotencyMiddleware.OutcomeUnknownDetail, "the idempotency middleware's replies must be found, or none is checked");
        string?[] texts =
        [
            .. Enum.GetValues<ErrorType>().Select(type => TitleOf(new Error("any", "Any message.", type).ToProblem())),
            Error.NotFound("Bill").Message,
            Error.NotFound("Bill", "NHOB1").Message,
            Error.ConcurrencyConflict.Message,
            .. FeatureCatalog.All.SelectMany(feature => new[] { feature.OffTitle, feature.OffDetailForPatients }),
            ValidationProblemTitle,
            MissingBodyTitle,
            .. idempotency,
        ];

        var untranslated = texts.OfType<string>()
            .SelectMany(text => Untranslated(text).Select(language => $"\"{text}\" has no {language} entry."))
            .ToList();

        untranslated.ShouldBeEmpty(string.Join('\n', untranslated));
    }

    // ---------------- the portal's endpoints

    /// <summary>The titles the validation filter answers with (Platform/Http/Validation.cs).</summary>
    private const string ValidationProblemTitle = "Please correct the highlighted fields.";

    private const string MissingBodyTitle = "A request body is required.";

    /// <summary>
    /// The titles and details the idempotency middleware answers a write with (Platform/Http/Idempotency.cs): each of its
    /// constants named *Title or *Detail, so a reply added there is checked here without being listed.
    /// </summary>
    private static IEnumerable<string> IdempotencyReplies() =>
        typeof(IdempotencyMiddleware).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)
                && (field.Name.EndsWith("Title", StringComparison.Ordinal) || field.Name.EndsWith("Detail", StringComparison.Ordinal)))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static List<RouteEndpoint> PortalEndpoints()
    {
        var portalPolicy = PermissionPolicyProvider.Prefix + PatientsPermissions.Portal;
        return
        [
            .. ((IEndpointRouteBuilder)EndpointAuthorizationTests.App.Value).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(endpoint =>
                    RequestLanguageMiddleware.PatientPathPrefixes.Any(prefix =>
                        ("/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/')).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    || endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == portalPolicy)),
        ];
    }

    private static string? TitleOf(Microsoft.AspNetCore.Http.IResult result) => result switch
    {
        ProblemHttpResult problem => problem.ProblemDetails.Title,
        ValidationProblem validation => validation.ProblemDetails.Title,
        _ => null,
    };

    /// <summary>The patient languages that have no translation of a message (its placeholders standing for any value).</summary>
    private static IEnumerable<string> Untranslated(string template)
    {
        var sample = Hole().Replace(template, "7");
        return PatientLanguagesToCheck.Where(language => PatientTexts.Catalog.TranslateMessage(language, sample) is null);
    }

    // ---------------- validators

    private sealed record RuleMessage(string Property, string Validator, string Template);

    /// <summary>The message template of every rule of a validator and of the validators it uses for its parts, in the UI culture.</summary>
    private static IEnumerable<RuleMessage> RuleMessages(IValidator validator, IServiceProvider services, string path)
    {
        foreach (var rule in validator.CreateDescriptor().Rules)
        {
            var property = $"{path}.{rule.PropertyName ?? "(whole request)"}";
            foreach (var component in rule.Components)
            {
                if (component.Validator is IChildValidatorAdaptor child)
                {
                    var inner = services.GetService(child.ValidatorType) as IValidator
                        ?? (child.ValidatorType is { IsAbstract: false, IsInterface: false } && child.ValidatorType.GetConstructor(Type.EmptyTypes) is not null
                            ? (IValidator)Activator.CreateInstance(child.ValidatorType)!
                            : throw new InvalidOperationException($"{property}: the validator {child.ValidatorType.Name} it uses cannot be created to read its messages."));
                    foreach (var message in RuleMessages(inner, services, property))
                    {
                        yield return message;
                    }
                }
                else
                {
                    yield return new RuleMessage(property, component.Validator.Name, component.GetUnformattedErrorMessage());
                }
            }
        }
    }

    private static T InCulture<T>(string culture, Func<T> read)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return read();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    // ---------------- messages in the source of the code reached

    private sealed record Message(string Text, string Where, string Via);

    /// <summary>A message built at run time that the check cannot read: where it is, and its expression.</summary>
    private sealed record Unread(string File, int Line, string Expression)
    {
        public override string ToString() => $"{File}:{Line} ({Expression})";
    }

    /// <summary>
    /// The messages at the source lines of the methods reached: the message of each <c>Error.Rule</c>,
    /// <c>Error.Conflict</c>, <c>Error.Forbidden</c>, <c>Error.Validation</c>, <c>Error.Field</c> and <c>new Error</c>, a
    /// Problem's <c>title:</c> and <c>detail:</c>, and a validator's <c>WithMessage</c>. Every text the message's
    /// expression can give is read: each branch of a <c>?:</c> or a <c>switch</c>, literals joined with + (the rest of
    /// the chain standing for any value), interpolations, <c>string.Create</c> and <c>string.Format</c>, and constants
    /// named by their class (or by name in their own file). Anything else is listed in <paramref name="unread"/>.
    /// </summary>
    private static List<Message> SourceMessages(CallGraph.Graph graph, out List<Unread> unread)
    {
        var root = RepositoryRoot();
        var constants = new Constants(root);
        var methods = new List<(string File, int First, int Last, MethodBase Method)>();
        using (var symbols = new DebugSymbols())
        {
            foreach (var method in graph.Reached)
            {
                if (symbols.SourceOf(method) is { } source && LocalPath(source.Document, root) is { } file)
                {
                    methods.Add((file, source.First, source.Last, method));
                }
            }
        }

        var found = new List<Message>();
        var notRead = new List<Unread>();
        var seen = new HashSet<(string, int)>();
        foreach (var inFile in methods.GroupBy(m => m.File, StringComparer.Ordinal))
        {
            var text = WithoutRawStrings(File.ReadAllText(inFile.Key));
            var lineStarts = LineStarts(text);
            var relative = Path.GetRelativePath(root, inFile.Key).Replace('\\', '/');

            // The innermost method first, so a message in a lambda or a local function is put down to it.
            foreach (var (_, first, last, method) in inFile.OrderBy(m => m.Last - m.First))
            {
                var from = lineStarts[Math.Min(first, lineStarts.Count) - 1];
                var to = last < lineStarts.Count ? lineStarts[last] : text.Length;
                foreach (Match call in MessageCall().Matches(text[..to], from))
                {
                    if (!seen.Add((inFile.Key, call.Index)))
                    {
                        continue;
                    }

                    var line = lineStarts.BinarySearch(call.Index) is var at && at >= 0 ? at + 1 : ~at;
                    var where = $"{relative}:{line}";
                    var arguments = Arguments(text, call.Index + call.Length - 1);
                    foreach (var expression in MessageArguments(call.Value, arguments))
                    {
                        var values = Values(expression, constants, inFile.Key, 0);
                        if (values is null)
                        {
                            notRead.Add(new Unread(Path.GetFileName(inFile.Key), line, string.Concat(expression.Select(t => t.Value)).Trim()));
                            continue;
                        }

                        found.AddRange(values.Select(value => new Message(value, where, graph.Via(method))));
                    }
                }
            }
        }

        unread = notRead;
        return found;
    }

    /// <summary>The arguments of a call that carry its message.</summary>
    private static IEnumerable<List<Token>> MessageArguments(string call, List<List<Token>> arguments)
    {
        if (call.Contains("Problem", StringComparison.Ordinal))
        {
            // TypedResults.Problem(title: …, detail: …)
            return arguments.Where(a => a.Count > 2 && a[0].Value is "title" or "detail" && a[1].Value == ":").Select(a => a[2..]);
        }

        var position = call.Contains("WithMessage", StringComparison.Ordinal) ? 0 : 1;
        return arguments.Count > position ? [arguments[position]] : [];
    }

    /// <summary>The texts an expression can give, or null when it is built from something this reading cannot follow.</summary>
    private static List<string>? Values(List<Token> expression, Constants constants, string file, int depth)
    {
        if (expression.Count == 0 || depth > 8)
        {
            return null;
        }

        if (expression is [{ Kind: TokenKind.Word, Value: "null" or "default" }] or [{ Kind: TokenKind.Word, Value: "throw" }, ..])
        {
            return [];
        }

        // x switch { A => "…", B => "…" }
        var switchAt = TopLevel(expression).FirstOrDefault(i => expression[i].Value == "switch");
        if (switchAt > 0 && switchAt + 1 < expression.Count && expression[switchAt + 1].Value == "{")
        {
            var arms = Split(expression[(switchAt + 2)..^1], ",").Where(arm => arm.Count > 0);
            return Union(arms.Select(arm => TopLevel(arm).FirstOrDefault(i => arm[i].Value == "=>") is var arrow && arrow > 0
                ? Values(arm[(arrow + 1)..], constants, file, depth + 1)
                : null));
        }

        // condition ? "…" : "…"
        var question = TopLevel(expression).Where(i => expression[i].Value == "?").DefaultIfEmpty(-1).First();
        if (question > 0)
        {
            var nested = 0;
            foreach (var i in TopLevel(expression).Where(i => i > question))
            {
                if (expression[i].Value == "?")
                {
                    nested++;
                }
                else if (expression[i].Value == ":" && nested-- == 0)
                {
                    return Union([Values(expression[(question + 1)..i], constants, file, depth + 1), Values(expression[(i + 1)..], constants, file, depth + 1)]);
                }
            }
        }

        // a ?? b
        var coalesce = TopLevel(expression).Where(i => expression[i].Value == "??").DefaultIfEmpty(-1).First();
        if (coalesce > 0)
        {
            return Union([Values(expression[..coalesce], constants, file, depth + 1), Values(expression[(coalesce + 1)..], constants, file, depth + 1)]);
        }

        // string.Create(culture, $"…"), string.Format(…, "…", …)
        if (expression is [{ Value: "string.Create" or "string.Format" or "String.Format" }, { Value: "(" }, .., { Value: ")" }])
        {
            var inner = Split(expression[2..^1], ",");
            return inner.FirstOrDefault(a => a is [{ Kind: TokenKind.Text }, ..]) is { } format ? Values(format, constants, file, depth + 1) : null;
        }

        // "…" + x + "…"
        var operands = Split(expression, "+");
        if (operands.Any(o => o is [{ Kind: TokenKind.Text }]))
        {
            return [string.Concat(operands.Select(o => o is [{ Kind: TokenKind.Text } literal] ? literal.Value : "{x}"))];
        }

        // A constant: by its class (Class.Name), else by name in this file.
        return expression is [{ Kind: TokenKind.Word } name] && constants.Find(name.Value, file) is { } declared
            ? Values(Tokens(declared, 0, stopAtClose: false), constants, file, depth + 1)
            : null;
    }

    private static List<string>? Union(IEnumerable<List<string>?> branches)
    {
        var all = new List<string>();
        foreach (var branch in branches)
        {
            if (branch is null)
            {
                return null;
            }

            all.AddRange(branch);
        }

        return all;
    }

    /// <summary>The positions of the tokens outside any brackets.</summary>
    private static IEnumerable<int> TopLevel(List<Token> tokens)
    {
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var value = tokens[i].Value;
            if (tokens[i].Kind == TokenKind.Symbol && value is ")" or "]" or "}")
            {
                depth--;
            }

            if (depth == 0)
            {
                yield return i;
            }

            if (tokens[i].Kind == TokenKind.Symbol && value is "(" or "[" or "{")
            {
                depth++;
            }
        }
    }

    private static List<List<Token>> Split(List<Token> tokens, string separator)
    {
        var parts = new List<List<Token>>();
        var start = 0;
        foreach (var i in TopLevel(tokens).Where(i => tokens[i].Kind == TokenKind.Symbol && tokens[i].Value == separator))
        {
            parts.Add(tokens[start..i]);
            start = i + 1;
        }

        parts.Add(tokens[start..]);
        return parts;
    }

    /// <summary>The arguments of the call whose opening bracket is at <paramref name="open"/>, as tokens.</summary>
    private static List<List<Token>> Arguments(string source, int open)
    {
        var inside = Tokens(source, open, stopAtClose: true);
        return inside.Count < 2 ? [] : Split(inside[1..^1], ",");
    }

    private enum TokenKind
    {
        Text,
        Word,
        Symbol,
    }

    private sealed record Token(TokenKind Kind, string Value);

    private static readonly string[] TwoCharacterSymbols = ["=>", "??", "==", "!=", "<=", ">=", "&&", "||"];

    /// <summary>
    /// The tokens of C# source from <paramref name="start"/>: string literals (their text), names with their dots
    /// (<c>Error.Field</c>, <c>x?.Y</c>), and symbols. With <paramref name="stopAtClose"/>, the bracket at
    /// <paramref name="start"/> and everything up to the one that closes it.
    /// </summary>
    private static List<Token> Tokens(string source, int start, bool stopAtClose)
    {
        var tokens = new List<Token>();
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] is '/' or '*')
            {
                var end = source[i + 1] == '/' ? source.IndexOf('\n', i) : source.IndexOf("*/", i + 2, StringComparison.Ordinal) + 1;
                i = end <= 0 ? source.Length : end;
                continue;
            }

            if (c is '"' or '$' or '@' && StringLiteral().Match(source, i) is { Success: true } literal && literal.Index == i)
            {
                tokens.Add(new Token(TokenKind.Text, Literal(literal.Value)));
                i += literal.Length - 1;
                continue;
            }

            if (c == '\'')
            {
                var close = i + 1;
                while (close < source.Length && source[close] != '\'')
                {
                    close += source[close] == '\\' ? 2 : 1;
                }

                tokens.Add(new Token(TokenKind.Word, source[i..Math.Min(close + 1, source.Length)]));
                i = close;
                continue;
            }

            if (char.IsLetterOrDigit(c) || c is '_' or '@')
            {
                var end = i;
                while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] is '_' or '@' or '.'
                       || (source[end] == '?' && end + 1 < source.Length && source[end + 1] == '.')))
                {
                    end += source[end] == '?' ? 2 : 1;
                }

                tokens.Add(new Token(TokenKind.Word, source[i..end].Replace("?.", ".", StringComparison.Ordinal)));
                i = end - 1;
                continue;
            }

            var symbol = i + 1 < source.Length && TwoCharacterSymbols.Contains(source.Substring(i, 2)) ? source.Substring(i, 2) : new string(c, 1);
            tokens.Add(new Token(TokenKind.Symbol, symbol));
            i += symbol.Length - 1;
            if (symbol is "(" or "[" or "{")
            {
                depth++;
            }
            else if (symbol is ")" or "]" or "}" && --depth == 0 && stopAtClose)
            {
                break;
            }
            else if (!stopAtClose && symbol == ";")
            {
                tokens.RemoveAt(tokens.Count - 1);
                break;
            }
        }

        return tokens;
    }

    /// <summary>The text of a C# string literal, an interpolation's holes kept as {x}.</summary>
    private static string Literal(string source)
    {
        var quote = source.IndexOf('"', StringComparison.Ordinal);
        var verbatim = source[..quote].Contains('@', StringComparison.Ordinal);
        var body = source[(quote + 1)..^1];
        body = verbatim ? body.Replace("\"\"", "\"", StringComparison.Ordinal) : Unescape(body);
        if (source[..quote].Contains('$', StringComparison.Ordinal))
        {
            body = body.Replace("{{", "\u0001", StringComparison.Ordinal).Replace("}}", "\u0002", StringComparison.Ordinal);
            body = Hole().Replace(body, "{x}").Replace('\u0001', '{').Replace('\u0002', '}');
        }

        return body;
    }

    /// <summary>The escapes of a regular C# string literal (\", \\, \n, \t, \u0000 …).</summary>
    private static string Unescape(string body)
    {
        var text = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i == body.Length - 1)
            {
                text.Append(body[i]);
                continue;
            }

            var escaped = body[++i];
            switch (escaped)
            {
                case 'n':
                    text.Append('\n');
                    break;
                case 'r':
                    text.Append('\r');
                    break;
                case 't':
                    text.Append('\t');
                    break;
                case '0':
                    text.Append('\0');
                    break;
                case 'u' when i + 4 < body.Length:
                    text.Append((char)int.Parse(body.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default:
                    text.Append(escaped);
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>The source with its raw string literals ("""…""", SQL mostly) left out and its lines kept where they were.</summary>
    private static string WithoutRawStrings(string source) =>
        RawString().Replace(source, raw => new string('\n', raw.Value.Count(c => c == '\n')));

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    /// <summary>The constant strings of the server's source: by Class.Name, and by name within their file.</summary>
    private sealed class Constants
    {
        private readonly Dictionary<string, string> _byClass = new(StringComparer.Ordinal);
        private readonly Dictionary<(string File, string Name), string> _byFile = [];

        public Constants(string root)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "server"), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = WithoutRawStrings(File.ReadAllText(file));
                var types = TypeDeclaration().Matches(text).Select(m => (m.Index, Name: m.Groups["name"].Value)).ToList();
                foreach (Match constant in ConstantDeclaration().Matches(text))
                {
                    var name = constant.Groups["name"].Value;
                    var value = text.Substring(constant.Groups["value"].Index, constant.Groups["value"].Length) + ";";
                    _byFile[(Path.GetFullPath(file), name)] = value;
                    if (types.LastOrDefault(t => t.Index < constant.Index) is { Name.Length: > 0 } type)
                    {
                        _byClass.TryAdd($"{type.Name}.{name}", value);
                    }
                }
            }
        }

        /// <summary>The source of a constant's value (up to its semicolon), or null.</summary>
        public string? Find(string reference, string file)
        {
            var parts = reference.Split('.');
            return parts.Length >= 2 && _byClass.TryGetValue($"{parts[^2]}.{parts[^1]}", out var byClass) ? byClass
                : parts.Length == 1 && _byFile.TryGetValue((file, reference), out var byFile) ? byFile
                : null;
        }
    }

    /// <summary>The file on this machine a document of the debug symbols names (CI builds name them /_/src/…).</summary>
    private static string? LocalPath(string document, string root)
    {
        var path = document.Replace('\\', '/');
        if (path.StartsWith("/_/", StringComparison.Ordinal))
        {
            path = Path.Combine(root, path[3..]);
        }

        return File.Exists(path) && path.EndsWith(".cs", StringComparison.Ordinal) ? Path.GetFullPath(path) : null;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyFib.HealthCare360.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (HyFib.HealthCare360.slnx) was not found above the test's folder.");
    }

    // A regular, verbatim or interpolated string literal; an interpolation's hole may hold quotes ({x.ToString("N2")}).
    private const string StringPattern = @"(?<!"")(?:\$@|@\$|\$|@)?""(?!"""")(?:\{\{|\}\}|\{[^{}""]*(?:""[^""]*""[^{}""]*)*\}|""""|\\.|[^""\\])*""";

    [GeneratedRegex(StringPattern)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\bError\.(?:Rule|Conflict|Forbidden|Validation|Field)\s*\(|\bnew\s+Error\s*\(|\.WithMessage\s*\(|\b(?:TypedResults|Results)\.(?:Problem|ValidationProblem)\s*\(")]
    private static partial Regex MessageCall();

    [GeneratedRegex(@"\b(?:const\s+string|static\s+readonly\s+string)\s+(?<name>\w+)\s*=\s*(?<value>(?:" + StringPattern + @"|[^;""])+);")]
    private static partial Regex ConstantDeclaration();

    [GeneratedRegex(@"\b(?:class|record|struct|interface)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"\$*(?<quotes>""{3,})[\s\S]*?\k<quotes>")]
    private static partial Regex RawString();

    /// <summary>A placeholder or an interpolation hole: {doctor}, {MaxLength}, {policy.WindowDays}.</summary>
    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Hole();

    // ---------------- the call graph

    /// <summary>The methods of HC360's code a set of methods can reach, read from their IL.</summary>
    private static class CallGraph
    {
        private static readonly OpCode[] OneByte = new OpCode[0x100];
        private static readonly OpCode[] TwoByte = new OpCode[0x100];
        private static readonly Lazy<Type[]> OurTypes = new(LoadOurTypes);
        private static readonly ConcurrentDictionary<Type, Type[]> Implementations = new();

        static CallGraph()
        {
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var code = (OpCode)field.GetValue(null)!;
                var value = unchecked((ushort)code.Value);
                if (code.Size == 1)
                {
                    OneByte[value] = code;
                }
                else
                {
                    TwoByte[value & 0xFF] = code;
                }
            }
        }

        /// <summary>The methods reached, each with the method it was first reached from.</summary>
        public sealed class Graph(List<MethodBase> reached, Dictionary<(Module, int), MethodBase> from)
        {
            public IReadOnlyList<MethodBase> Reached => reached;

            /// <summary>How a method is reached: the handler, then each call, shortest first.</summary>
            public string Via(MethodBase method)
            {
                var chain = new List<string>();
                for (MethodBase? at = method; at is not null && chain.Count < 30; at = from.GetValueOrDefault((at.Module, at.MetadataToken)))
                {
                    var name = NameOf(at);
                    if (chain.Count == 0 || chain[^1] != name)
                    {
                        chain.Add(name);
                    }
                }

                chain.Reverse();
                return string.Join(" → ", chain);
            }

            // AppointmentBookingService.BookAsync for its async state machine; PortalEndpoints.MapEndpoints (lambda).
            private static string NameOf(MethodBase method)
            {
                var type = method.DeclaringType!;
                var name = method.Name;
                while (type.IsNested && type.Name.StartsWith('<'))
                {
                    if (type.Name.IndexOf('>', StringComparison.Ordinal) is > 1 and var close)
                    {
                        name = type.Name[1..close];
                    }

                    type = type.DeclaringType!;
                }

                if (name.StartsWith('<') && name.IndexOf('>', StringComparison.Ordinal) is > 1 and var end)
                {
                    name = name[1..end] + " (lambda)";
                }

                return $"{type.Name}.{name}";
            }
        }

        public static Graph From(IEnumerable<MethodBase> roots)
        {
            var seen = new HashSet<(Module, int)>();
            var from = new Dictionary<(Module, int), MethodBase>();
            var reached = new List<MethodBase>();
            var queue = new Queue<MethodBase>(roots);
            while (queue.TryDequeue(out var method))
            {
                if (!IsOurs(method.DeclaringType) || !seen.Add((method.Module, method.MetadataToken)))
                {
                    continue;
                }

                reached.Add(method);
                var next = new List<MethodBase>();
                if (method.GetCustomAttribute<StateMachineAttribute>() is { } machine
                    && machine.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } moveNext)
                {
                    next.Add(moveNext);
                }

                foreach (var callee in Callees(method).Where(CanAnswerWithAMessage))
                {
                    next.Add(callee);
                    next.AddRange(ImplementationsOf(callee));
                }

                foreach (var callee in next)
                {
                    from.TryAdd((callee.Module, callee.MetadataToken), method);
                    queue.Enqueue(callee);
                }
            }

            return new Graph(reached, from);
        }

        /// <summary>
        /// Whether what a method returns can carry a refusal back to its caller: an <see cref="Error"/>, a
        /// <see cref="Result"/>, an HTTP result, or a task, tuple, list or generic value that may hold one. A refusal made
        /// below a method that returns anything else (a bool, nothing, a string, a constructor's object) never reaches
        /// the answer: it is kept, logged or replaced on the way.
        /// </summary>
        private static bool CanAnswerWithAMessage(MethodBase method) =>
            method is MethodInfo info && CanCarry(info.ReturnType, 0);

        private static bool CanCarry(Type type, int depth)
        {
            if (depth > 4)
            {
                return false;
            }

            if (type.IsGenericParameter || type == typeof(object) || typeof(Error).IsAssignableFrom(type) || typeof(Result).IsAssignableFrom(type)
                || typeof(Microsoft.AspNetCore.Http.IResult).IsAssignableFrom(type))
            {
                return true;
            }

            if (type == typeof(string) || !type.IsGenericType)
            {
                return false;
            }

            var definition = type.GetGenericTypeDefinition();
            return (definition == typeof(Task<>) || definition == typeof(ValueTask<>) || definition == typeof(Nullable<>)
                    || type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true
                    || type.FullName?.StartsWith("System.Tuple`", StringComparison.Ordinal) == true
                    || typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                   && type.GetGenericArguments().Any(argument => CanCarry(argument, depth + 1));
        }

        private static bool IsOurs(Type? type) =>
            type?.Assembly.GetName().Name?.StartsWith("HyFib.HealthCare360.", StringComparison.Ordinal) == true;

        /// <summary>The methods a method calls, creates or takes as a delegate (call, callvirt, newobj, ldftn).</summary>
        private static List<MethodBase> Callees(MethodBase method)
        {
            var callees = new List<MethodBase>();
            byte[]? il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch (InvalidOperationException)
            {
                return callees;
            }

            if (il is null)
            {
                return callees;
            }

            var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            for (var at = 0; at < il.Length;)
            {
                var code = il[at] == 0xFE ? TwoByte[il[at + 1]] : OneByte[il[at]];
                at += code.Size;
                switch (code.OperandType)
                {
                    case OperandType.InlineMethod:
                        try
                        {
                            if (method.Module.ResolveMethod(BitConverter.ToInt32(il, at), typeArguments, methodArguments) is { } callee && IsOurs(callee.DeclaringType))
                            {
                                callees.Add(callee);
                            }
                        }
                        catch (ArgumentException)
                        {
                            // A method of a generic instantiation this reading cannot resolve: it is framework code here.
                        }

                        at += 4;
                        break;
                    case OperandType.InlineSwitch:
                        at += 4 + (4 * BitConverter.ToInt32(il, at));
                        break;
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                        at += 1;
                        break;
                    case OperandType.InlineVar:
                        at += 2;
                        break;
                    case OperandType.InlineI8 or OperandType.InlineR:
                        at += 8;
                        break;
                    default:
                        at += 4;
                        break;
                }
            }

            return callees;
        }

        /// <summary>For a call of an interface method or a virtual one: the methods of HC360's types that implement or override it.</summary>
        private static IEnumerable<MethodBase> ImplementationsOf(MethodBase callee)
        {
            if (callee is not MethodInfo { IsVirtual: true } method || method.DeclaringType is not { } declaring)
            {
                yield break;
            }

            var definition = declaring.IsGenericType ? declaring.GetGenericTypeDefinition() : declaring;
            foreach (var type in Implementations.GetOrAdd(definition, ImplementingTypes))
            {
                if (declaring.IsInterface)
                {
                    foreach (var contract in type.GetInterfaces().Where(i => (i.IsGenericType ? i.GetGenericTypeDefinition() : i) == definition))
                    {
                        InterfaceMapping map;
                        try
                        {
                            map = type.GetInterfaceMap(contract);
                        }
                        catch (ArgumentException)
                        {
                            continue;
                        }

                        for (var i = 0; i < map.InterfaceMethods.Length; i++)
                        {
                            if (map.InterfaceMethods[i].MetadataToken == method.MetadataToken && map.TargetMethods[i].DeclaringType == type)
                            {
                                yield return map.TargetMethods[i];
                            }
                        }
                    }
                }
                else
                {
                    var baseDefinition = method.GetBaseDefinition();
                    foreach (var candidate in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (candidate.Name == method.Name && candidate.GetBaseDefinition() is var candidateBase
                            && candidateBase.MetadataToken == baseDefinition.MetadataToken && candidateBase.Module == baseDefinition.Module)
                        {
                            yield return candidate;
                        }
                    }
                }
            }
        }

        private static Type[] ImplementingTypes(Type definition) =>
        [
            .. OurTypes.Value.Where(type => type is { IsInterface: false } && type != definition && (definition.IsInterface
                ? type.GetInterfaces().Any(i => (i.IsGenericType ? i.GetGenericTypeDefinition() : i) == definition)
                : Inherits(type, definition))),
        ];

        private static bool Inherits(Type type, Type definition)
        {
            for (var parent = type.BaseType; parent is not null; parent = parent.BaseType)
            {
                if ((parent.IsGenericType ? parent.GetGenericTypeDefinition() : parent) == definition)
                {
                    return true;
                }
            }

            return false;
        }

        private static Type[] LoadOurTypes()
        {
            var types = new List<Type>();
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "HyFib.HealthCare360.*.dll"))
            {
                if (Path.GetFileNameWithoutExtension(file).EndsWith("Tests", StringComparison.Ordinal))
                {
                    continue;
                }

                Assembly assembly;
                try
                {
                    assembly = Assembly.Load(AssemblyName.GetAssemblyName(file));
                }
                catch (BadImageFormatException)
                {
                    continue;
                }

                try
                {
                    types.AddRange(assembly.GetTypes());
                }
                catch (ReflectionTypeLoadException partial)
                {
                    types.AddRange(partial.Types.OfType<Type>());
                }
            }

            // This test's own probe of the walk (an interface and its implementation) as well.
            types.AddRange(typeof(PortalMessageTests).Assembly.GetTypes());
            return [.. types];
        }
    }

    /// <summary>Where a method's code is in the source, from its assembly's portable debug symbols.</summary>
    private sealed class DebugSymbols : IDisposable
    {
        private readonly Dictionary<Assembly, MetadataReaderProvider?> _providers = [];

        public (string Document, int First, int Last)? SourceOf(MethodBase method)
        {
            var assembly = method.Module.Assembly;
            if (!_providers.TryGetValue(assembly, out var provider))
            {
                var pdb = Path.ChangeExtension(assembly.Location, ".pdb");
                provider = _providers[assembly] = File.Exists(pdb) ? MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdb)) : null;
            }

            if (provider is null)
            {
                return null;
            }

            var reader = provider.GetMetadataReader();
            var information = reader.GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle(method.MetadataToken));
            var (document, first, last) = (default(DocumentHandle), int.MaxValue, 0);
            foreach (var point in information.GetSequencePoints())
            {
                if (point.IsHidden)
                {
                    continue;
                }

                document = point.Document;
                first = Math.Min(first, point.StartLine);
                last = Math.Max(last, point.EndLine);
            }

            return last == 0 ? null : (reader.GetString(reader.GetDocument(document).Name), first, last);
        }

        public void Dispose()
        {
            foreach (var provider in _providers.Values)
            {
                provider?.Dispose();
            }
        }
    }
}
