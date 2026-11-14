using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// Whether the patient an analyser named for a sample can be the tube's patient: the UHID decides when the analyser
/// sent a registered patient's, the names otherwise, and only evidence of another patient counts against the tube.
/// </summary>
public sealed class PatientIdentityTests
{
    private const string Tube = "Ravi Kumar Sharma";

    [Theory]
    // The UHID decides, whatever the name: names get corrected after an order is sent, and two patients share a name.
    [InlineData(AnalyserPatientId.TubePatient, "MEERA VERMA", PatientComparison.SameId)]
    [InlineData(AnalyserPatientId.TubePatient, null, PatientComparison.SameId)]
    [InlineData(AnalyserPatientId.AnotherPatient, "SHARMA^RAVI^KUMAR", PatientComparison.OtherId)]
    [InlineData(AnalyserPatientId.AnotherPatient, null, PatientComparison.OtherId)]
    // No registered patient's id (none, the analyser's own number, a typo): the name decides.
    [InlineData(AnalyserPatientId.NoPatient, "SHARMA^RAVI", PatientComparison.SameName)]
    [InlineData(AnalyserPatientId.NoPatient, "VERMA^MEERA", PatientComparison.OtherName)]
    // Nothing to compare by: the message is taken as if it named no one.
    [InlineData(AnalyserPatientId.NoPatient, null, PatientComparison.NotCompared)]
    [InlineData(AnalyserPatientId.NoPatient, "Mr.", PatientComparison.NotCompared)]
    public void The_uhid_decides_and_the_name_only_without_one(AnalyserPatientId id, string? sentName, PatientComparison expected) =>
        PatientIdentity.Compare(id, sentName, Tube).ShouldBe(expected);

    [Fact]
    public void A_tube_patient_without_a_name_to_compare_is_not_compared_by_name() =>
        PatientIdentity.Compare(AnalyserPatientId.NoPatient, "SHARMA^RAVI", "  ").ShouldBe(PatientComparison.NotCompared);

    [Theory]
    // Order, case, the analyser's field delimiters and punctuation.
    [InlineData("SHARMA^RAVI^KUMAR", Tube)]
    [InlineData("ravi kumar sharma", Tube)]
    [InlineData("Sharma, Ravi Kumar", Tube)]
    // Parts left out, initials, and a name cut at the length of the analyser's field.
    [InlineData("SHARMA^RAVI", Tube)]
    [InlineData("R. Sharma", Tube)]
    [InlineData("Ravi K Sharma", Tube)]
    [InlineData("Ravi Kumar Sharm", Tube)]
    [InlineData("Ravi Kumar Sh", Tube)]
    [InlineData("RAVI", Tube)]
    // Titles, a bed or sample number in the name field, accents.
    [InlineData("Mr. Ravi Sharma", Tube)]
    [InlineData("DR RAVI", "Ravi Kumar")]
    [InlineData("RAVI SHARMA 12", "Ravi Sharma")]
    [InlineData("José Fernandes", "JOSE FERNANDES")]
    // Initials written together, as South Indian names often are, either way round.
    [InlineData("KV RAO", "Kesava Venkata Rao")]
    [InlineData("KV Rao", "K. V. Rao")]
    [InlineData("S Manikandan", "Manikandan Subramanian")]
    // A newborn registered as "Baby of" the mother, and written B/O on the analyser.
    [InlineData("B/O LAKSHMI", "Baby of Lakshmi")]
    public void Names_that_can_be_one_persons_agree(string sent, string registered)
    {
        PatientIdentity.NamesAgree(sent, registered).ShouldBe(true);
        PatientIdentity.NamesAgree(registered, sent).ShouldBe(true);
    }

    [Theory]
    [InlineData("Ravi Verma", "Ravi Sharma")]
    [InlineData("Meera Sharma", "Ravi Sharma")]
    [InlineData("SHARMA^MEERA", Tube)]
    [InlineData("R Verma", "Ravi Sharma")]
    [InlineData("M Sharma", "Ravi Sharma")]
    [InlineData("Meera", "Ravi")]
    [InlineData("B/O MEENA", "Baby of Lakshmi")]
    public void Names_that_cannot_be_one_persons_disagree(string sent, string registered)
    {
        PatientIdentity.NamesAgree(sent, registered).ShouldBe(false);
        PatientIdentity.NamesAgree(registered, sent).ShouldBe(false);
    }

    [Theory]
    [InlineData(null, "Ravi")]
    [InlineData("", "Ravi")]
    [InlineData("Ravi", null)]
    [InlineData("Mrs.", "Ravi")]
    [InlineData("UNKNOWN", "Ravi")]
    [InlineData("Patient Name", "Ravi")]
    [InlineData("12345", "Ravi")]
    [InlineData("^^^", "Ravi")]
    [InlineData("ரவி", "Ravi")]
    public void A_name_with_no_part_to_compare_is_not_compared(string? sent, string? registered) =>
        PatientIdentity.NamesAgree(sent, registered).ShouldBeNull();

    [Theory]
    [InlineData("Mr. Ravi  Kumar-Sharma", new[] { "RAVI", "KUMAR", "SHARMA" })]
    [InlineData("SHARMA^RAVI", new[] { "SHARMA", "RAVI" })]
    [InlineData("José D'Souza", new[] { "JOSE", "D", "SOUZA" })]
    [InlineData("Baby of Lakshmi", new[] { "LAKSHMI" })]
    [InlineData("Smt. Meera 2", new[] { "MEERA" })]
    [InlineData("A B C D E F G H", new[] { "A", "B", "C", "D", "E", "F" })]
    public void A_name_is_compared_by_its_parts(string name, string[] parts) => PatientIdentity.NameParts(name).ShouldBe(parts);

    [Fact]
    public void A_name_that_is_not_well_formed_unicode_is_read_without_failing() =>
        PatientIdentity.NameParts("RAVI \uD800SHARMA").ShouldBe(["RAVI", "SHARMA"]);

    [Theory]
    [InlineData(" nan26000123 ", "NAN26000123")]
    [InlineData("NAN 2600 0123", "NAN26000123")]
    [InlineData("NH-UH-26001", "NH-UH-26001")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void A_patient_id_is_compared_without_spaces_and_in_capitals(string? id, string expected) =>
        PatientIdentity.NormaliseId(id).ShouldBe(expected);

    [Fact]
    public void A_record_merged_with_the_tube_patients_is_the_same_person()
    {
        Guid tube = Guid.CreateVersion7(), duplicate = Guid.CreateVersion7(), survivor = Guid.CreateVersion7(), other = Guid.CreateVersion7();

        PatientIdentity.SamePerson(tube, null, tube, null).ShouldBeTrue();
        PatientIdentity.SamePerson(duplicate, tube, tube, null).ShouldBeTrue();
        PatientIdentity.SamePerson(survivor, null, tube, survivor).ShouldBeTrue();
        PatientIdentity.SamePerson(duplicate, survivor, tube, survivor).ShouldBeTrue();

        PatientIdentity.SamePerson(other, null, tube, null).ShouldBeFalse();
        PatientIdentity.SamePerson(other, survivor, tube, null).ShouldBeFalse();
        PatientIdentity.SamePerson(other, survivor, tube, duplicate).ShouldBeFalse();
    }
}
