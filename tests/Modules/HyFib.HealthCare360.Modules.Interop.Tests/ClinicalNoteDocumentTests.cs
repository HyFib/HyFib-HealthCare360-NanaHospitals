using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Gap #27: signed clinical notes as FHIR <c>DocumentReference</c> (<c>note-</c>): LOINC's document type for each kind
/// of note, the note as it was signed, who wrote and signed it, where it was written, addenda, and the restricted label
/// on counselling notes.
/// </summary>
public sealed class ClinicalNoteDocumentTests
{
    private const string NrcesDocumentReference = "https://nrces.in/ndhm/fhir/r4/StructureDefinition/DocumentReference";

    internal static readonly Guid NoteId = Guid.Parse("01923a4b-0000-7000-8000-00000000c001");
    private static readonly Guid OriginalId = Guid.Parse("01923a4b-0000-7000-8000-00000000c002");
    private static readonly Guid EncounterId = Guid.Parse("01923a4b-0000-7000-8000-00000000c003");
    private static readonly Guid AdmissionId = Guid.Parse("01923a4b-0000-7000-8000-00000000c004");
    private static readonly Guid VisitId = Guid.Parse("01923a4b-0000-7000-8000-00000000c005");

    /// <summary>What the doctor wrote, with a Tamil line and the spacing they gave it.</summary>
    private const string Body = "Afebrile since morning. Chest clear.\n\nPlan: continue oral amoxicillin; review in 48 h.\nஉணவு: மென்மையான உணவு.";

    /// <summary>A progress note the doctor wrote and signed in the patient's consultation at 10:30 IST.</summary>
    internal static ClinicalNoteRow Note() => new()
    {
        Id = NoteId,
        PatientId = TestData.PatientId,
        FacilityId = TestData.FacilityId,
        EncounterId = EncounterId,
        Type = ClinicalNoteType.ProgressNote,
        Title = "Day 2 review",
        Body = Body,
        AuthorPractitionerId = TestData.DoctorId,
        AuthorUserName = "Asha Rao",
        SignedByPractitionerId = TestData.DoctorId,
        SignedByUserName = "Asha Rao",
        SignedAt = TestData.Now,
        CreatedAt = TestData.Now.AddHours(-1),
        UpdatedAt = TestData.Now,
    };

    [Fact]
    public void A_signed_note_is_shared_as_it_was_signed_by_the_doctor_who_wrote_it_from_where_it_was_written()
    {
        var document = DocumentReferenceMapper.FromClinicalNote(Note(), TestData.Directory);

        document.Id.ShouldBe($"note-{NoteId:D}");
        document.Meta.LastUpdated.ShouldBe(TestData.Now);
        document.Status.ShouldBe(R4.DocumentReferenceStatus.Current);
        document.DocStatus.ShouldBe(R4.CompositionStatus.Final);
        var type = document.Type.Coding.ShouldHaveSingleItem();
        (type.System, type.Code, type.Display).ShouldBe(("http://loinc.org", "11506-3", "Provider-unspecified Progress note"));
        document.Type.Text.ShouldBe("Progress note");
        document.Subject.Reference.ShouldBe($"Patient/{TestData.PatientId:D}");
        document.Date.ShouldBe(TestData.Now, "when it was signed");
        document.Description.ShouldBe("Day 2 review");

        var author = document.Author.ShouldHaveSingleItem();
        author.Display.ShouldBe("Dr. Asha Rao");
        author.Identifier.System.ShouldBe("https://doctor.ndhm.gov.in", "the doctor's HPR ID");
        document.Authenticator.Display.ShouldBe("Dr. Asha Rao");
        document.Custodian.Display.ShouldBe("Nana Hospitals, Pune");
        document.Custodian.Identifier.Value.ShouldBe("IN2710000123", "the HFR id of the facility where it was written");
        document.Context.Encounter.ShouldHaveSingleItem().Reference.ShouldBe($"Encounter/enc-{EncounterId:D}");
        document.RelatesTo.ShouldBeEmpty();

        var attachment = document.Content.ShouldHaveSingleItem().Attachment;
        attachment.ContentType.ShouldBe("text/plain; charset=utf-8");
        Encoding.UTF8.GetString(attachment.Data).ShouldBe(Body, "the note as it was signed, nothing added");
        attachment.SizeUnsignedInt.ShouldBe(Encoding.UTF8.GetByteCount(Body));
        attachment.Title.ShouldBe("Day 2 review");
        attachment.Creation.ShouldBe("2026-09-25T10:30:00+05:30");
        attachment.Language.ShouldBeNull("HC360 does not know which language a note is written in");
    }

    [Theory]
    [InlineData(ClinicalNoteType.ProgressNote, "11506-3", "Provider-unspecified Progress note", "Progress note")]
    [InlineData(ClinicalNoteType.Procedure, "28570-0", "Provider-unspecified Procedure note", "Procedure note")]
    [InlineData(ClinicalNoteType.Referral, "57133-1", "Referral note", "Referral note")]
    [InlineData(ClinicalNoteType.Counselling, "47042-7", "Counseling note", "Counselling note")]
    [InlineData(ClinicalNoteType.Other, "34109-9", "Note", "Clinical note")]
    public void Each_kind_of_note_has_LOINCs_document_type_for_it(ClinicalNoteType kind, string code, string display, string name)
    {
        var type = DocumentReferenceMapper.NoteType(kind);

        var coding = type.Coding.ShouldHaveSingleItem();
        (coding.System, coding.Code, coding.Display).ShouldBe(("http://loinc.org", code, display));
        type.Text.ShouldBe(name);
    }

    [Fact]
    public void Every_note_type_is_in_FHIRs_document_type_value_set_under_LOINCs_name()
    {
        // FHIR R4's Document Type value set (c80-doc-typecodes), as HL7 publishes it in the specification the validator uses.
        var loincDocumentTypes = DocumentTypeValueSet();

        loincDocumentTypes.Count.ShouldBeGreaterThan(1000, "the value set lists LOINC's document types one by one");
        foreach (var kind in Enum.GetValues<ClinicalNoteType>())
        {
            var coding = DocumentReferenceMapper.NoteType(kind).Coding.ShouldHaveSingleItem();
            var code = coding.Code.ShouldNotBeNull();
            loincDocumentTypes.ShouldContainKey(code, $"{kind}: {code} is a LOINC document type");
            loincDocumentTypes[code].ShouldBe(coding.Display, $"{kind}: LOINC's name for {code}");
        }

        Enum.GetValues<ClinicalNoteType>().Select(k => DocumentReferenceMapper.NoteType(k).Coding[0].Code).ShouldBeUnique();
    }

    [Fact]
    public void A_note_written_and_signed_by_staff_who_are_not_practitioners_names_them()
    {
        var document = DocumentReferenceMapper.FromClinicalNote(
            Note() with { AuthorPractitionerId = null, AuthorUserName = "Sr. Kavitha R", SignedByPractitionerId = null, SignedByUserName = "Sr. Kavitha R" },
            TestData.Directory);

        var author = document.Author.ShouldHaveSingleItem();
        author.Display.ShouldBe("Sr. Kavitha R");
        author.Reference.ShouldBeNull();
        author.Identifier.ShouldBeNull();
        document.Authenticator.Display.ShouldBe("Sr. Kavitha R");
    }

    [Fact]
    public void A_note_signed_by_someone_other_than_its_author_names_the_signer_as_authenticator()
    {
        var document = DocumentReferenceMapper.FromClinicalNote(Note() with { SignedByPractitionerId = null, SignedByUserName = "Dr. Vikram Iyer" }, TestData.Directory);

        document.Author.ShouldHaveSingleItem().Display.ShouldBe("Dr. Asha Rao");
        document.Authenticator.Display.ShouldBe("Dr. Vikram Iyer");
    }

    [Fact]
    public void An_addendum_appends_to_the_note_it_adds_to_which_stays_current()
    {
        var addendum = DocumentReferenceMapper.FromClinicalNote(Note() with { AmendsNoteId = OriginalId, Title = "Addendum to Day 2 review" }, TestData.Directory);

        var relation = addendum.RelatesTo.ShouldHaveSingleItem();
        relation.Code.ShouldBe(R4.DocumentRelationshipType.Appends);
        relation.Target.Reference.ShouldBe($"DocumentReference/note-{OriginalId:D}");
        addendum.Status.ShouldBe(R4.DocumentReferenceStatus.Current);
        addendum.Description.ShouldBe("Addendum to Day 2 review");
        DocumentReferenceMapper.FromClinicalNote(Note() with { Id = OriginalId }, TestData.Directory).Status
            .ShouldBe(R4.DocumentReferenceStatus.Current, "an addendum adds to a note; it does not replace it");
    }

    [Fact]
    public void A_counselling_note_is_labelled_restricted_and_no_other_note_is()
    {
        var counselling = DocumentReferenceMapper.FromClinicalNote(Note() with { Type = ClinicalNoteType.Counselling }, TestData.Directory);

        var onReference = counselling.Meta.Security.ShouldHaveSingleItem();
        (onReference.System, onReference.Code).ShouldBe(("http://terminology.hl7.org/CodeSystem/v3-Confidentiality", "R"));
        var onDocument = counselling.SecurityLabel.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem();
        (onDocument.System, onDocument.Code, onDocument.Display).ShouldBe(("http://terminology.hl7.org/CodeSystem/v3-Confidentiality", "R", "restricted"));
        foreach (var kind in Enum.GetValues<ClinicalNoteType>().Where(k => k != ClinicalNoteType.Counselling))
        {
            var note = DocumentReferenceMapper.FromClinicalNote(Note() with { Type = kind }, TestData.Directory);
            note.Meta.Security.ShouldBeEmpty(kind.ToString());
            note.SecurityLabel.ShouldBeEmpty(kind.ToString());
        }
    }

    [Fact]
    public void A_note_in_a_stay_names_the_stays_encounter_and_one_in_neither_names_none()
    {
        var directory = new ReferenceDirectory
        {
            Practitioners = TestData.Directory.Practitioners,
            Facilities = TestData.Directory.Facilities,
            AdmissionVisits = new Dictionary<Guid, Guid> { [AdmissionId] = VisitId },
        };

        DocumentReferenceMapper.FromClinicalNote(Note() with { EncounterId = null, AdmissionId = AdmissionId }, directory)
            .Context.Encounter.ShouldHaveSingleItem().Reference.ShouldBe($"Encounter/visit-{VisitId:D}");
        DocumentReferenceMapper.FromClinicalNote(Note() with { EncounterId = null, AdmissionId = null }, directory).Context.ShouldBeNull();
    }

    [Fact]
    public void A_note_without_a_title_is_described_by_its_kind()
    {
        var document = DocumentReferenceMapper.FromClinicalNote(Note() with { Type = ClinicalNoteType.Procedure, Title = null }, TestData.Directory);

        document.Description.ShouldBe("Procedure note");
        document.Content[0].Attachment.Title.ShouldBe("Procedure note");
    }

    [Fact]
    public void Every_kind_of_note_meets_FHIR_and_the_abdm_guides_document_reference_profile()
    {
        foreach (var kind in Enum.GetValues<ClinicalNoteType>())
        {
            foreach (var note in new[] { Note() with { Type = kind }, Note() with { Type = kind, AmendsNoteId = OriginalId, Title = null, AuthorPractitionerId = null } })
            {
                var validation = AbdmProfileValidator.Validate(DocumentReferenceMapper.FromClinicalNote(note, TestData.Directory), NrcesDocumentReference);

                validation.Errors.ShouldBeEmpty($"{kind}: {validation.Describe()}");
            }
        }
    }

    [Fact]
    public void A_search_by_type_finds_the_notes_of_that_kind()
    {
        R4.Resource[] notes =
        [
            DocumentReferenceMapper.FromClinicalNote(Note(), TestData.Directory),
            DocumentReferenceMapper.FromClinicalNote(Note() with { Id = OriginalId, Type = ClinicalNoteType.Referral }, TestData.Directory),
        ];

        ResourceSearch.Execute(notes, TestData.Search("DocumentReference", $"patient={TestData.PatientId:D}&type=http://loinc.org|57133-1"))
            .Resources.ShouldHaveSingleItem().Id.ShouldBe($"note-{OriginalId:D}");
        ResourceSearch.Execute(notes, TestData.Search("DocumentReference", $"patient={TestData.PatientId:D}&date=2026-09-25")).Total.ShouldBe(2);
    }

    /// <summary>The LOINC codes and names of FHIR R4's Document Type value set, read from the specification archive.</summary>
    private static Dictionary<string, string> DocumentTypeValueSet()
    {
        XNamespace fhir = "http://hl7.org/fhir";
        using var archive = ZipFile.OpenRead(Path.Combine(AppContext.BaseDirectory, "specification.zip"));
        using var stream = archive.GetEntry("valuesets.xml")!.Open();
        var valueSet = XDocument.Load(stream).Descendants(fhir + "ValueSet")
            .Single(v => (string?)v.Element(fhir + "url")?.Attribute("value") == "http://hl7.org/fhir/ValueSet/c80-doc-typecodes");
        return valueSet.Descendants(fhir + "include")
            .Where(i => (string?)i.Element(fhir + "system")?.Attribute("value") == "http://loinc.org")
            .Elements(fhir + "concept")
            .ToDictionary(c => (string)c.Element(fhir + "code")!.Attribute("value")!, c => (string)c.Element(fhir + "display")!.Attribute("value")!);
    }
}
