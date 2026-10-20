using System.Globalization;
using System.Text;
using System.Xml;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;

/// <summary>
/// The human-readable text of a document section (FHIR narrative, XHTML): what the doctor wrote, as written. Blank lines
/// separate paragraphs and line breaks are kept; nothing is added, reworded or formatted beyond that, and characters XML
/// cannot carry (control characters) are dropped. The status is <c>additional</c>: the text says more than the coded
/// entries of the section, where it has any.
/// </summary>
internal sealed class SectionText
{
    private readonly StringBuilder _html = new();

    public bool IsEmpty => _html.Length == 0;

    /// <summary>Paragraphs of free text; nothing for empty text.</summary>
    public SectionText Paragraphs(string? text)
    {
        if (FhirElements.Clean(text) is not { } value)
        {
            return this;
        }

        var paragraph = new List<string>();
        foreach (var line in Lines(value))
        {
            if (line.Length == 0)
            {
                Paragraph(paragraph);
                continue;
            }

            paragraph.Add(line);
        }

        Paragraph(paragraph);
        return this;
    }

    /// <summary>One paragraph that starts with a label in bold ("Follow-up: 27 Oct 2026"), its line breaks kept.</summary>
    public SectionText Labelled(string label, string? text)
    {
        if (FhirElements.Clean(text) is { } value)
        {
            _html.Append("<p><b>").Append(Escape(label)).Append(":</b> ")
                .AppendJoin("<br/>", Lines(value).Where(line => line.Length > 0).Select(Escape)).Append("</p>");
        }

        return this;
    }

    /// <summary>A bulleted list, after its caption; nothing when there are no items.</summary>
    public SectionText List(string caption, IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
        {
            return this;
        }

        _html.Append("<p>").Append(Escape(caption)).Append("</p><ul>");
        foreach (var item in items)
        {
            _html.Append("<li>").Append(Escape(item)).Append("</li>");
        }

        _html.Append("</ul>");
        return this;
    }

    /// <summary>The narrative, or null when nothing was written.</summary>
    public R4.Narrative? ToNarrative() =>
        IsEmpty
            ? null
            : new R4.Narrative
            {
                Status = R4.Narrative.NarrativeStatus.Additional,
                Div = string.Create(CultureInfo.InvariantCulture, $"<div xmlns=\"http://www.w3.org/1999/xhtml\">{_html}</div>"),
            };

    /// <summary>The lines of a text, trimmed, whatever its line endings.</summary>
    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(line => line.Trim());

    private void Paragraph(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        _html.Append("<p>").AppendJoin("<br/>", lines.Select(Escape)).Append("</p>");
        lines.Clear();
    }

    /// <summary>XML text: markup characters escaped, characters XML does not allow (control characters, lone surrogates) dropped.</summary>
    internal static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
            {
                escaped.Append(c).Append(text[++i]);
                continue;
            }

            switch (c)
            {
                case '<':
                    escaped.Append("&lt;");
                    break;
                case '>':
                    escaped.Append("&gt;");
                    break;
                case '&':
                    escaped.Append("&amp;");
                    break;
                case '"':
                    escaped.Append("&quot;");
                    break;
                default:
                    if (XmlConvert.IsXmlChar(c))
                    {
                        escaped.Append(c);
                    }

                    break;
            }
        }

        return escaped.ToString();
    }
}
