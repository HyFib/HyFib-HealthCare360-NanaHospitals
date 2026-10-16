using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>The bytes a doctor signs with their DSC (ERX-04): RFC 8785 canonical JSON of the prescription.</summary>
public sealed class PrescriptionCanonicalFormTests
{
    internal static readonly Guid FacilityId = Guid.Parse("0190f000-0000-7000-8000-000000000001");
    internal static readonly Guid PatientId = Guid.Parse("0190f000-0000-7000-8000-000000000002");
    internal static readonly Guid PractitionerId = Guid.Parse("0190f000-0000-7000-8000-000000000003");
    internal static readonly Guid PrescriptionId = Guid.Parse("0190f000-0000-7000-8000-000000000004");
    internal static readonly Guid EncounterId = Guid.Parse("0190f000-0000-7000-8000-000000000005");

    // 10:15:42.123456 UTC: the signed form keeps whole seconds, as the database round trip may not keep ticks.
    internal static readonly DateTimeOffset SignedAt = new DateTimeOffset(2026, 9, 25, 10, 15, 42, TimeSpan.Zero).AddTicks(1_234_567);

    internal static readonly PrescriptionSigningParties Parties = new(
        "Nana Hospitals", "NH-000123", "Asha Rao", "Dr Meera Shah", "MBBS, MD", "MMC-55821", "Maharashtra Medical Council");

    [Fact]
    public void The_signed_bytes_are_sorted_compact_utf8_json()
    {
        var json = Encoding.UTF8.GetString(PrescriptionCanonicalForm.Build(Signed(), Parties));

        json.ShouldBe(
            "{\"facility\":{\"id\":\"0190f000-0000-7000-8000-000000000001\",\"name\":\"Nana Hospitals\"},"
            + "\"format\":\"hc360.prescription.v1\","
            + "\"patient\":{\"id\":\"0190f000-0000-7000-8000-000000000002\",\"name\":\"Asha Rao\",\"uhid\":\"NH-000123\"},"
            + "\"prescriber\":{\"id\":\"0190f000-0000-7000-8000-000000000003\",\"name\":\"Dr Meera Shah\",\"qualification\":\"MBBS, MD\","
            + "\"registrationCouncil\":\"Maharashtra Medical Council\",\"registrationNumber\":\"MMC-55821\"},"
            + "\"prescription\":{\"admissionId\":null,\"advice\":\"Walk daily.\\nAvoid sweets.\",\"encounterId\":\"0190f000-0000-7000-8000-000000000005\","
            + "\"id\":\"0190f000-0000-7000-8000-000000000004\",\"isInpatient\":false,"
            + "\"items\":["
            + "{\"controlledSchedule\":null,\"customFrequency\":null,\"dosageForm\":\"Tablet\",\"dose\":\"1 tab\",\"drugName\":\"Metformin\","
            + "\"durationDays\":30,\"frequency\":\"BD\",\"genericName\":\"Metformin hydrochloride\",\"instructions\":\"After food\","
            + "\"isControlledDrug\":false,\"quantity\":\"60\",\"route\":\"Oral\",\"sequence\":1,\"strength\":\"500 mg\"},"
            + "{\"controlledSchedule\":\"H1\",\"customFrequency\":\"1-0-1\",\"dosageForm\":null,\"dose\":\"½ tab\",\"drugName\":\"Tramadol \\\"SR\\\"\","
            + "\"durationDays\":3,\"frequency\":\"Custom\",\"genericName\":null,\"instructions\":null,"
            + "\"isControlledDrug\":true,\"quantity\":\"1.5\",\"route\":null,\"sequence\":2,\"strength\":\"50 mg\"}"
            + "],"
            + "\"notes\":null,\"number\":\"NHRX260000123\",\"refillOfPrescriptionId\":null,\"refillsAllowed\":2,"
            + "\"signedAt\":\"2026-09-25T10:15:42Z\",\"verificationToken\":\"Zk3_9aB-qR7tLm2xWv5cAbCd\",\"visitId\":null}}");
    }

    [Fact]
    public void Text_is_written_as_is_and_only_json_specials_are_escaped()
    {
        var prescription = Signed(advice: "दिन में दो बार\t\"quoted\" \\ back\u0001", items: [Item(1, "Crocin <b>&</b>", 10m)]);

        var json = Encoding.UTF8.GetString(PrescriptionCanonicalForm.Build(prescription, Parties));

        json.ShouldContain("\"advice\":\"दिन में दो बार\\t\\\"quoted\\\" \\\\ back\\u0001\"");
        json.ShouldContain("\"drugName\":\"Crocin <b>&</b>\"", Case.Sensitive, "JCS does not escape HTML characters");
        PrescriptionCanonicalForm.Build(prescription, Parties).Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF }, "no byte order mark");
    }

    [Fact]
    public void The_same_prescription_always_gives_the_same_bytes_whatever_order_or_scale_it_is_loaded_in()
    {
        var first = Signed();
        var second = Signed(items: [Item(2, "Tramadol \"SR\"", 1.50m, controlled: true), Item(1, "Metformin", 60.00m)]);

        PrescriptionCanonicalForm.Build(second, Parties).ShouldBe(PrescriptionCanonicalForm.Build(first, Parties));
        PrescriptionCanonicalForm.Sha256(PrescriptionCanonicalForm.Build(first, Parties)).Length.ShouldBe(64);
    }

    [Theory]
    [InlineData("60", "60")]
    [InlineData("60.00", "60")]
    [InlineData("1.50", "1.5")]
    [InlineData("0.25", "0.25")]
    public void Quantities_are_decimal_strings_without_trailing_zeros(string value, string expected) =>
        PrescriptionCanonicalForm.Quantity(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);

    [Fact]
    public void Every_clinically_meaningful_change_changes_the_bytes()
    {
        var original = PrescriptionCanonicalForm.Build(Signed(), Parties);

        PrescriptionCanonicalForm.Build(Signed(advice: "Walk daily."), Parties).ShouldNotBe(original);
        PrescriptionCanonicalForm.Build(Signed(items: [Item(1, "Metformin", 90m)]), Parties).ShouldNotBe(original);
        PrescriptionCanonicalForm.Build(Signed(), Parties with { PatientName = "Asha R." }).ShouldNotBe(original);
        PrescriptionCanonicalForm.Build(Signed(), Parties with { RegistrationNumber = "MMC-55822" }).ShouldNotBe(original);
    }

    [Fact]
    public void The_parties_are_read_back_from_the_signed_bytes()
    {
        var content = PrescriptionCanonicalForm.Build(Signed(), Parties);

        PrescriptionCanonicalForm.ReadParties(content).ShouldBe(Parties);
        Should.Throw<System.Text.Json.JsonException>(() => PrescriptionCanonicalForm.ReadParties("{\"format\":\"other\"}"u8.ToArray()));
    }

    [Fact]
    public void Desktop_and_server_agree_on_the_format() =>
        PrescriptionSigningRequest.Format.ShouldBe(PrescriptionCanonicalForm.Format);

    internal static PrescriptionItem Item(int sequence, string name, decimal quantity, bool controlled = false) => controlled
        ? new PrescriptionItem
        {
            DrugName = name,
            Strength = "50 mg",
            Dose = "½ tab",
            Frequency = DoseFrequency.Custom,
            CustomFrequency = "1-0-1",
            DurationDays = 3,
            Quantity = quantity,
            IsControlledDrug = true,
            ControlledSchedule = "H1",
            Sequence = sequence,
        }
        : new PrescriptionItem
        {
            DrugName = name,
            GenericName = "Metformin hydrochloride",
            Strength = "500 mg",
            DosageForm = "Tablet",
            Route = "Oral",
            Dose = "1 tab",
            Frequency = DoseFrequency.BD,
            DurationDays = 30,
            Quantity = quantity,
            Instructions = "After food",
            ControlledSchedule = "H",
            Sequence = sequence,
        };

    /// <summary>A signed prescription with fixed ids; <paramref name="items"/> keep their sequence numbers.</summary>
    internal static Prescription Signed(string advice = "Walk daily.\nAvoid sweets.", PrescriptionItem[]? items = null)
    {
        var prescription = new Prescription
        {
            Id = PrescriptionId,
            FacilityId = FacilityId,
            PrescriptionNumber = "NHRX260000123",
            PatientId = PatientId,
            PractitionerId = PractitionerId,
            EncounterId = EncounterId,
            RefillsAllowed = 2,
            Advice = advice,
        };
        prescription.ReplaceItems([Item(1, "Metformin", 60m), Item(2, "Tramadol \"SR\"", 1.5m, controlled: true)]);
        prescription.Sign(new PrescriptionSignature(Guid.NewGuid(), "Zk3_9aB-qR7tLm2xWv5cAbCd", null, "[]", true), SignedAt).IsSuccess.ShouldBeTrue();
        if (items is not null)
        {
            // As loaded from the database: the stored sequence numbers, in whatever order the rows come.
            prescription.Items = [.. items];
        }

        return prescription;
    }
}
