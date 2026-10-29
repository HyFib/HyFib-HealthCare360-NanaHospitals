using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Billing.Documents;

/// <summary>
/// The hospital's rate list, to display at the counter and in the wards (Clinical Establishments Rules 2012, r.9(i): the
/// rates of each service and facility, displayed in the local language as well as in English). Its own words are in
/// English with the local language after them, when HC360 writes that language (<see cref="RateListLanguage"/>); each
/// service's local name, where the hospital has entered one, is printed under its English name. It lists what a
/// self-paying patient is charged: registration, each doctor's consultation, a day in each class of room with its nursing
/// and doctors' visits, every other service by category, and the packages.
/// </summary>
internal static class RateListDocument
{
    private const string Styles = """
        .rates h2 { font-size: 11pt; color: #045E80; margin: 12px 0 4px; }
        .rates .as-on { text-align: center; margin-bottom: 8px; }
        .rates .registration { border: 1px solid #999; border-radius: 4px; padding: 6px 8px; margin: 8px 0; display: flex; justify-content: space-between; font-size: 11pt; }
        table.rate { margin-bottom: 4px; }
        table.rate th { border-bottom: 1px solid #555; background: #f2f2f2; font-size: 9pt; white-space: normal; }
        table.rate td { border-bottom: 1px solid #e3e3e3; }
        .num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
        .code { white-space: nowrap; color: #555; font-size: 8.5pt; }
        .local { display: block; color: #333; }
        .section-note { font-size: 8.5pt; color: #444; margin: 2px 0 6px; }
        .notes { margin-top: 12px; font-size: 8.5pt; color: #333; padding-left: 16px; }

        """;

    /// <summary>The rate list under the facility's letterhead, as its "other documents" template says.</summary>
    public static async Task<PrintableDocument> BuildAsync(
        Guid facilityId, RateList rates, string? language, DocumentAssets assets, CancellationToken cancellationToken) =>
        Build(rates, await assets.LetterheadAsync(facilityId, PrintTemplateCatalog.Other, cancellationToken), language);

    public static PrintableDocument Build(RateList rates, DocumentLetterhead letterhead, string? language)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(letterhead);
        var labels = new Labels(language is null || language == DocumentText.English.Language ? null : DocumentText.For(language));
        var asOn = DocumentFormat.Date(rates.AsOn);
        return new PrintableDocument
        {
            Title = $"{DocumentText.English["doc.rates.title"]} {asOn}",
            FileName = $"rate-list-{rates.AsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            Footer = $"{letterhead.Name} · {DocumentText.English.Format("doc.rates.as_on", ("date", asOn))}",
            Styles = DocumentParts.Styles + Styles,
            Template = letterhead.Template,
            Body = Html.Of($"""
                <div class="rates">
                  {DocumentParts.Letterhead(letterhead)}
                  <h1 class="doc-title">{labels["doc.rates.title"]}</h1>
                  <div class="as-on">{labels.Format("doc.rates.as_on", ("date", asOn))}</div>
                  {Registration(rates.Registration, labels)}
                  {Doctors(rates.Doctors, labels)}
                  {Rooms(rates.Rooms, labels)}
                  {rates.Sections.Select(section => Section(section, labels))}
                  {Packages(rates.Packages, labels)}
                  {Notes(rates, labels)}
                </div>
                """),
        };
    }

    /// <summary>The words of a category's heading; a category added later without words of its own prints its name.</summary>
    public static string? CategoryKey(ServiceCategory category) => category switch
    {
        ServiceCategory.Registration => "doc.rates.category.registration",
        ServiceCategory.Consultation => "doc.rates.category.consultation",
        ServiceCategory.Laboratory => "doc.rates.category.laboratory",
        ServiceCategory.Radiology => "doc.rates.category.radiology",
        ServiceCategory.Cardiology => "doc.rates.category.cardiology",
        ServiceCategory.Procedure => "doc.rates.category.procedure",
        ServiceCategory.Surgery => "doc.rates.category.surgery",
        ServiceCategory.Nursing => "doc.rates.category.nursing",
        ServiceCategory.DoctorVisit => "doc.rates.category.doctor_visit",
        ServiceCategory.HealthCheckup => "doc.rates.category.health_checkup",
        ServiceCategory.Physiotherapy => "doc.rates.category.physiotherapy",
        ServiceCategory.Dialysis => "doc.rates.category.dialysis",
        ServiceCategory.Dietary => "doc.rates.category.dietary",
        ServiceCategory.BloodBank => "doc.rates.category.blood_bank",
        ServiceCategory.Ambulance => "doc.rates.category.ambulance",
        ServiceCategory.Consumable => "doc.rates.category.consumable",
        ServiceCategory.Miscellaneous => "doc.rates.category.miscellaneous",
        _ => null,
    };

    private static Html Registration(RateLine? registration, Labels labels) => registration is null
        ? Html.Empty
        : Html.Of($"""<div class="registration keep"><span>{labels["doc.rates.registration"]}</span><b>{Payable(registration, labels)}</b></div>""");

    private static Html Doctors(IReadOnlyList<DoctorFeeLine> doctors, Labels labels)
    {
        if (doctors.Count == 0)
        {
            return Html.Empty;
        }

        var video = doctors.Any(d => d.Video is not null);
        var rows = doctors.Select(d => Html.Of($"""
            <tr>
              <td>{d.Name}</td>
              <td>{d.Department ?? DocumentFormat.Missing}</td>
              <td class="num">{Payable(d.Fee, labels)}</td>
              {(video ? Html.Of($"<td class=\"num\">{(d.Video is { } v ? Payable(v, labels) : Html.Text(DocumentFormat.Missing))}</td>") : Html.Empty)}
              <td>{(d.FreeFollowUpDays > 0 ? labels.Format("doc.rates.follow_up_free", ("days", d.FreeFollowUpDays.ToString(CultureInfo.InvariantCulture))) : labels["doc.rates.follow_up_charged"])}</td>
            </tr>
            """));
        return Html.Of($"""
            <h2 class="keep-next">{labels["doc.rates.consultations"]}</h2>
            <table class="rate">
              <thead><tr><th>{labels["doc.rates.doctor"]}</th><th>{labels["doc.rates.department"]}</th><th class="num">{labels["doc.rates.in_person"]}</th>{(video ? Html.Of($"<th class=\"num\">{labels["doc.rates.by_video"]}</th>") : Html.Empty)}<th>{labels["doc.rates.follow_up"]}</th></tr></thead>
              <tbody>{rows}</tbody>
            </table>
            """);
    }

    private static Html Rooms(IReadOnlyList<RoomRateLine> rooms, Labels labels)
    {
        if (rooms.Count == 0)
        {
            return Html.Empty;
        }

        var rows = rooms.Select(r => Html.Of($"""
            <tr>
              <td>{Name(r.Day)}{(r.IntensiveCare ? Html.Of($"<span class=\"local\">{labels["doc.rates.intensive_care"]}</span>") : Html.Empty)}</td>
              <td>{string.Join(", ", r.Wards)}</td>
              <td class="num">{Payable(r.Day, labels)}{Gst(r.Day, labels)}</td>
              <td class="num">{(r.Nursing is { } nursing ? Payable(nursing, labels) : Html.Text(DocumentFormat.Missing))}</td>
              <td class="num">{(r.Visit is { } visit ? Payable(visit, labels) : Html.Text(DocumentFormat.Missing))}</td>
              <td class="num">{Times(r.Multiplier)}</td>
            </tr>
            """));
        return Html.Of($"""
            <h2 class="keep-next">{labels["doc.rates.rooms"]}</h2>
            <table class="rate">
              <thead><tr><th>{labels["doc.rates.room"]}</th><th>{labels["doc.rates.wards"]}</th><th class="num">{labels["doc.rates.bed"]}</th><th class="num">{labels["doc.rates.nursing"]}</th><th class="num">{labels["doc.rates.visit"]}</th><th class="num">{labels["doc.rates.multiplier"]}</th></tr></thead>
              <tbody>{rows}</tbody>
            </table>
            <div class="section-note">{labels["doc.rates.note.multiplier"]}</div>
            """);
    }

    private static Html Section(RateSection section, Labels labels)
    {
        var heading = CategoryKey(section.Category) is { } key ? labels[key] : Html.Text(section.Category.ToString());
        var taxed = section.Lines.Any(l => l.GstRate > 0);
        var rows = section.Lines.Select(line => Html.Of($"""
            <tr>
              <td class="code">{line.Code}</td>
              <td>{Name(line)}</td>
              {(taxed ? Html.Of($"<td class=\"num\">{Rate(line, labels)}</td><td class=\"num\">{GstRate(line, labels)}</td>") : Html.Empty)}
              <td class="num">{Payable(line, labels)}</td>
            </tr>
            """));
        var note = section.Category == ServiceCategory.Consultation
            ? Html.Of($"<div class=\"section-note\">{labels["doc.rates.note.emergency_fee"]}</div>")
            : Html.Empty;
        return Html.Of($"""
            <h2 class="keep-next">{heading}</h2>
            <table class="rate">
              <thead><tr><th>{labels["doc.rates.code"]}</th><th>{labels["doc.rates.service"]}</th>{(taxed ? Html.Of($"<th class=\"num\">{labels["doc.rates.rate"]}</th><th class=\"num\">GST</th>") : Html.Empty)}<th class="num">{labels["doc.rates.payable"]}</th></tr></thead>
              <tbody>{rows}</tbody>
            </table>
            {note}
            """);
    }

    private static Html Packages(IReadOnlyList<PackageLine> packages, Labels labels)
    {
        if (packages.Count == 0)
        {
            return Html.Empty;
        }

        var rows = packages.Select(p => Html.Of($"""
            <tr>
              <td class="code">{p.Price.Code}</td>
              <td>{p.Price.Name}</td>
              <td>{(p.LengthOfStayDays is { } days ? labels.Format("doc.rates.package_stay", ("days", days.ToString(CultureInfo.InvariantCulture))) : Html.Text(DocumentFormat.Missing))}</td>
              <td>{labels[UseKey(p.UsedFor)]}</td>
              <td class="num">{Payable(p.Price, labels)}</td>
            </tr>
            """));
        return Html.Of($"""
            <h2 class="keep-next">{labels["doc.rates.packages"]}</h2>
            <table class="rate">
              <thead><tr><th>{labels["doc.rates.code"]}</th><th>{labels["doc.rates.package"]}</th><th>{labels["doc.rates.stay"]}</th><th>{labels["doc.rates.package_for"]}</th><th class="num">{labels["doc.rates.payable"]}</th></tr></thead>
              <tbody>{rows}</tbody>
            </table>
            <div class="section-note">{labels["doc.rates.note.packages"]}</div>
            """);
    }

    private static Html Notes(RateList rates, Labels labels)
    {
        var lines = Lines(rates).ToList();
        var notes = new List<Html> { labels["doc.rates.note.self_pay"] };
        if (rates.ConcessionPercent > 0)
        {
            notes.Add(labels.Format("doc.rates.note.concession", ("percent", Percent(rates.ConcessionPercent))));
        }

        notes.Add(labels["doc.rates.note.gst"]);
        if (lines.Any(l => l.MayVary))
        {
            notes.Add(labels["doc.rates.note.may_vary"]);
        }

        notes.Add(labels["doc.rates.note.medicines"]);
        return Html.Of($"""<ul class="notes keep">{notes.Select(note => Html.Of($"<li>{note}</li>"))}</ul>""");
    }

    private static IEnumerable<RateLine> Lines(RateList rates) =>
        new[] { rates.Registration }.OfType<RateLine>()
            .Concat(rates.Doctors.SelectMany(d => new[] { d.Fee, d.Video }.OfType<RateLine>()))
            .Concat(rates.Rooms.SelectMany(r => new[] { r.Day, r.Nursing, r.Visit }.OfType<RateLine>()))
            .Concat(rates.Sections.SelectMany(s => s.Lines))
            .Concat(rates.Packages.Select(p => p.Price));

    // The English name, with the local name under it when the hospital has entered one.
    private static Html Name(RateLine line) => string.IsNullOrWhiteSpace(line.LocalName)
        ? Html.Text(line.Name)
        : Html.Of($"{line.Name}<span class=\"local\">{line.LocalName}</span>");

    // What the patient pays. A service the counter prices by what is used, with no price of its own, has no amount to
    // show; one with a price the counter may change is marked.
    private static Html Payable(RateLine line, Labels labels) => line.MayVary
        ? line.Payable == 0 ? labels["doc.rates.as_used"] : Html.Of($"{DocumentFormat.Money(line.Payable)} *")
        : Html.Text(DocumentFormat.Money(line.Payable));

    private static Html Rate(RateLine line, Labels labels) =>
        line.MayVary && line.Rate == 0 ? labels["doc.rates.as_used"] : Html.Text(DocumentFormat.Money(line.Rate));

    private static Html GstRate(RateLine line, Labels labels) => line.GstRate > 0 ? Html.Text(Percent(line.GstRate)) : labels["doc.rates.exempt"];

    // A room's GST, under its price, when its rent bears GST.
    private static Html Gst(RateLine line, Labels labels) =>
        line.GstRate > 0 ? Html.Of($"<span class=\"local\">{labels.Format("doc.rates.with_gst", ("percent", Percent(line.GstRate)))}</span>") : Html.Empty;

    private static string UseKey(PackageUse use) => use switch
    {
        PackageUse.OutPatient => "doc.rates.package_use.out_patient",
        PackageUse.Both => "doc.rates.package_use.both",
        _ => "doc.rates.package_use.in_patient",
    };

    // "×1", "×1.5": at most two decimals.
    private static string Times(decimal multiplier) => "×" + multiplier.ToString("0.##", CultureInfo.InvariantCulture);

    // "18%", "2.5%": at most two decimals.
    private static string Percent(decimal percent) => percent.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>The list's words in English, with the local language after them when there is one.</summary>
    private sealed class Labels(DocumentText? second)
    {
        public Html this[string key] => Format(key);

        public Html Format(string key, params ReadOnlySpan<(string Name, string? Value)> values) =>
            second is { } local ? DocumentText.Bilingual(local, key, values) : Html.Text(DocumentText.English.Format(key, values));
    }
}
