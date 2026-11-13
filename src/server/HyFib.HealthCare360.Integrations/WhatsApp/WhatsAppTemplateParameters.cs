namespace HyFib.HealthCare360.Integrations.WhatsApp;

/// <summary>
/// The body parameters of a template message, made to match how the hospital registered its templates in WhatsApp
/// Manager (<see cref="WhatsAppSettings.Parameters"/>, <c>integrations.whatsapp</c> <c>parameters</c>):
/// <list type="bullet">
/// <item><c>text</c>, the default: the message's own body parameters, which for a message rendered from an HC360
/// template is its whole text as the one parameter, <c>{{1}}</c>.</item>
/// <item><c>positional</c>: the value of each placeholder of the HC360 template's body, in order, as <c>{{1}}</c>,
/// <c>{{2}}</c>…; a placeholder used twice is two parameters.</item>
/// <item><c>named</c>: each placeholder once, in the order it first occurs, as a named parameter: its name in lower
/// snake case, dots as underscores (<c>{{billNumber}}</c> is <c>{{bill_number}}</c>).</item>
/// </list>
/// A message without placeholders (the administrator's test) goes with its own body parameters whatever the setting.
/// </summary>
internal static class WhatsAppTemplateParameters
{
    public const string Text = "text";

    public const string Positional = "positional";

    public const string Named = "named";

    /// <summary>Why the setting can't be used, or null. Letter case and surrounding spaces don't matter; empty means text.</summary>
    public static string? Problem(WhatsAppSettings settings) =>
        Mode(settings) is null ? "The WhatsApp template parameters must be text, positional or named." : null;

    /// <summary>The body parameters to send, in order, a named one with its name, for settings <see cref="Problem"/> accepts.</summary>
    public static IReadOnlyList<WhatsAppBodyParameter> For(WhatsAppTemplateMessage message, WhatsAppSettings settings) =>
        (Mode(settings), message.Placeholders) switch
        {
            (Positional, { } placeholders) => [.. placeholders.Select(p => new WhatsAppBodyParameter(null, p.Value))],
            (Named, { } placeholders) =>
                [.. placeholders.Select(p => new WhatsAppBodyParameter(ParameterName(p.Name), p.Value)).DistinctBy(p => p.Name)],
            _ => [.. message.BodyParameters.Select(text => new WhatsAppBodyParameter(null, text))],
        };

    /// <summary>A placeholder's named parameter: lower snake case, dots as underscores (<c>billNumber</c> is <c>bill_number</c>).</summary>
    public static string ParameterName(string placeholder) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(placeholder).Replace('.', '_');

    private static string? Mode(WhatsAppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Parameters)
            ? Text
            : settings.Parameters.Trim().ToLowerInvariant() switch
            {
                Text => Text,
                Positional => Positional,
                Named => Named,
                _ => null,
            };
}

/// <summary>One body parameter of a template message: a positional one has no name.</summary>
internal sealed record WhatsAppBodyParameter(string? Name, string Value);
