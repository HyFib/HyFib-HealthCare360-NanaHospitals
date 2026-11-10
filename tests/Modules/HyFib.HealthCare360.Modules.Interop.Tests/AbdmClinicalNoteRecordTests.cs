using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Gap #27: a signed clinical note as an ABDM health document (the guide's HealthDocumentRecord), with the addenda signed
/// to it since. The sample document is in Fixtures/Abdm with the other kinds (<see cref="AbdmDocumentTests"/>, which
/// validates it against the guide); these tests say what it holds, and how a note is named, sent and refused.
/// </summary>
public sealed class AbdmClinicalNoteRecordTests
{
    private static readonly DateTimeOffset NoteSigned = AbdmTestData.ProgressNote().SignedAt;
    private static readonly DateTimeOffset AddendumSigned = AbdmTestData.Addendum().SignedAt;

    private static R4.Bundle Build(ClinicalNoteData data) => AbdmDocumentBuilder.Build(AbdmRecords.ClinicalNote(data));

    private static R4.Composition Composition(R4.Bundle bundle) => bundle.Entry[0].Resource.ShouldBeOfType<R4.Composition>();

    private static T Single<T>(R4.Bundle bundle, Func<T, bool>? match = null)
        where T : R4.Resource =>
        bundle.Entry.Select(e => e.Resource).OfType<T>().Single(r => match?.Invoke(r) ?? true);

    private static string FullUrl(R4.Bundle bundle, R4.Resource resource) => bundle.Entry.Single(e => ReferenceEquals(e.Resource, resource)).FullUrl!;

    private static R4.Practitioner Doctor(R4.Bundle bundle, string name) => Single<R4.Practitioner>(bundle, p => p.Name[0].Text == name);

    private static List<R4.DocumentReference> Documents(R4.Bundle bundle) =>
    [
        .. Composition(bundle).Section.ShouldHaveSingleItem().Entry
            .Select(e => bundle.Entry.Single(x => x.FullUrl == e.Reference).Resource).Cast<R4.DocumentReference>(),
    ];

    private static void ShouldMeetTheGuide(R4.Bundle bundle)
    {
        var validation = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);
        validation.Errors.ShouldBeEmpty(validation.Describe());
    }

    [Fact]
    public void A_signed_note_is_the_hospitals_health_document_holding_the_note_as_it_was_signed()
    {
        var note = AbdmTestData.ProgressNote();
        var document = AbdmRecords.ClinicalNote(AbdmTestData.ClinicalNoteRecord());
        var bundle = AbdmDocumentBuilder.Build(document);
        var composition = Composition(bundle);
        var doctor = Doctor(bundle, "Dr. Asha Rao");
        var hospital = Single<R4.Organization>(bundle);

        // Saved under its kind, the UHID and the day it was signed, in IST.
        document.FileName.ShouldBe($"progress-note-{AbdmTestData.Patient().Uhid}-2026-09-25-abdm.json");
        composition.Meta.Profile.ShouldBe([AbdmProfiles.HealthDocumentRecord]);
        var type = composition.Type.Coding.ShouldHaveSingleItem();
        (type.System, type.Code, type.Display).ShouldBe(("http://snomed.info/sct", "419891008", "Record artifact"));
        composition.Type.Text.ShouldBe("Progress note");
        composition.Title.ShouldBe("Progress note");
        composition.Status.ShouldBe(R4.CompositionStatus.Final);
        composition.Date.ShouldBe(FhirTime.DateTime(note.SignedAt), "a note is the hospital's record from when it is signed");

        // Written by the doctor, who signed it and so attests it, kept by the facility, in the consultation it was written in.
        composition.Author.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl(bundle, doctor));
        var attester = composition.Attester.ShouldHaveSingleItem();
        attester.Mode.ShouldBe(R4.Composition.CompositionAttestationMode.Legal);
        attester.Party.Reference.ShouldBe(FullUrl(bundle, doctor));
        attester.Time.ShouldBe(FhirTime.DateTime(note.SignedAt));
        composition.Custodian.Reference.ShouldBe(FullUrl(bundle, hospital));
        composition.Encounter.Reference.ShouldBe(FullUrl(bundle, Single<R4.Encounter>(bundle)));

        // One section, of the record the note is, holding the note as the FHIR API serves it.
        var section = composition.Section.ShouldHaveSingleItem();
        var code = section.Code.Coding.ShouldHaveSingleItem();
        (code.System, code.Code, code.Display).ShouldBe(("http://snomed.info/sct", "419891008", "Record artifact"));
        section.Title.ShouldBe("Fever review", "the doctor's title for the note");
        var shared = Documents(bundle).ShouldHaveSingleItem();
        section.Entry.ShouldHaveSingleItem().Type.ShouldBe("DocumentReference");
        shared.Meta.Profile.ShouldBe([AbdmProfiles.DocumentReference]);
        shared.Status.ShouldBe(R4.DocumentReferenceStatus.Current);
        shared.DocStatus.ShouldBe(R4.CompositionStatus.Final);
        var kind = shared.Type.Coding.ShouldHaveSingleItem();
        (kind.System, kind.Code, kind.Display).ShouldBe(("http://loinc.org", "11506-3", "Provider-unspecified Progress note"));
        shared.Subject.Reference.ShouldBe(composition.Subject.Reference);
        shared.Author.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl(bundle, doctor));
        shared.Authenticator.Reference.ShouldBe(FullUrl(bundle, doctor));
        shared.Custodian.Reference.ShouldBe(FullUrl(bundle, hospital));
        shared.Context.Encounter.ShouldHaveSingleItem().Reference.ShouldBe(composition.Encounter.Reference);
        shared.RelatesTo.ShouldBeEmpty();
        var attachment = shared.Content.ShouldHaveSingleItem().Attachment;
        attachment.ContentType.ShouldBe("text/plain; charset=utf-8");
        Encoding.UTF8.GetString(attachment.Data).ShouldBe(note.Body, "the note exactly as it was signed");

        // What a person reads: who signed it and when, in IST, then the note, paragraph by paragraph, and nothing twice.
        section.Text.Status.ShouldBe(R4.Narrative.NarrativeStatus.Generated);
        section.Text.Div.ShouldBe(
            "<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Progress note, signed by Dr. Asha Rao on 25 Sep 2026, 9:20 am</p>"
            + "<p>Fever settled since last evening; tolerating oral fluids.</p>"
            + "<p>Plan: paracetamol only when needed. Review with the CBC report.</p></div>");
        ShouldMeetTheGuide(bundle);
    }

    [Fact]
    public void An_addendum_signed_since_goes_with_the_note_and_amends_it()
    {
        var alone = Build(AbdmTestData.ClinicalNoteRecord());
        var bundle = Build(AbdmTestData.ClinicalNoteRecord(AbdmTestData.Addendum()));
        var composition = Composition(bundle);
        var writer = Doctor(bundle, "Dr. Asha Rao");
        var cardiologist = Doctor(bundle, "Dr. Vikram Shah");

        // Amended, as of the addendum: dated when it was signed, attested by the doctor who signed it, who wrote part of it.
        composition.Status.ShouldBe(R4.CompositionStatus.Amended);
        composition.Date.ShouldBe(FhirTime.DateTime(AddendumSigned));
        composition.Author.Select(a => a.Reference).ShouldBe([FullUrl(bundle, writer), FullUrl(bundle, cardiologist)]);
        var attester = composition.Attester.ShouldHaveSingleItem();
        attester.Party.Reference.ShouldBe(FullUrl(bundle, cardiologist));
        attester.Time.ShouldBe(FhirTime.DateTime(AddendumSigned));

        // The note, then the addendum, which appends to it and is written, signed and kept as itself.
        var documents = Documents(bundle);
        documents.Select(d => d.Description).ShouldBe(["Fever review", "Addendum to Fever review"]);
        var appends = documents[1].RelatesTo.ShouldHaveSingleItem();
        appends.Code.ShouldBe(R4.DocumentRelationshipType.Appends);
        appends.Target.Reference.ShouldBe(FullUrl(bundle, documents[0]));
        documents[1].Author.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl(bundle, cardiologist));
        documents[1].Authenticator.Reference.ShouldBe(FullUrl(bundle, cardiologist));
        documents[0].Status.ShouldBe(R4.DocumentReferenceStatus.Current, "the note an addendum adds to stays current");

        Composition(bundle).Section[0].Text.Div.ShouldEndWith(
            "<p>Addendum, signed by Dr. Vikram Shah on 26 Sep 2026, 8:30 am</p>"
            + "<p>ECG reviewed: sinus rhythm, no ischaemic change. No cardiology follow-up needed.</p></div>");
        bundle.Meta.VersionId.ShouldNotBe(alone.Meta.VersionId, "a note with an addendum is a new version of its document");
        bundle.Id.ShouldNotBe(alone.Id);
        ShouldMeetTheGuide(bundle);
    }

    [Fact]
    public void Addenda_are_in_the_order_they_were_signed_an_addendum_to_an_addendum_included()
    {
        var first = AbdmTestData.Addendum();
        var second = first with
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ab01a"),
            Title = "Addendum to Addendum to Fever review",
            Body = "CBC normal.",
            AuthorPractitionerId = TestData.DoctorId,
            AuthorUserName = "Asha Rao",
            SignedByPractitionerId = TestData.DoctorId,
            SignedByUserName = "Asha Rao",
            AmendsNoteId = first.Id,
            SignedAt = first.SignedAt.AddHours(3),
            CreatedAt = first.SignedAt.AddHours(2),
            UpdatedAt = first.SignedAt.AddHours(3),
        };

        var bundle = Build(AbdmTestData.ClinicalNoteRecord(second, first));
        var documents = Documents(bundle);

        documents.Select(d => d.Description).ShouldBe(["Fever review", "Addendum to Fever review", "Addendum to Addendum to Fever review"]);
        documents[2].RelatesTo.ShouldHaveSingleItem().Target.Reference.ShouldBe(FullUrl(bundle, documents[1]));
        var composition = Composition(bundle);
        composition.Date.ShouldBe(FhirTime.DateTime(second.SignedAt));
        composition.Attester.ShouldHaveSingleItem().Party.Reference.ShouldBe(FullUrl(bundle, Doctor(bundle, "Dr. Asha Rao")));
        composition.Author.ShouldHaveSingleItem("the latest addendum's writer wrote the note too");
        FhirJson.Serialize(bundle).ShouldBe(FhirJson.Serialize(Build(AbdmTestData.ClinicalNoteRecord(first, second))), "whatever order the rows came in");
        ShouldMeetTheGuide(bundle);
    }

    [Fact]
    public void A_note_written_by_staff_who_are_not_doctors_is_the_hospitals_and_attested_by_no_one()
    {
        var nurses = AbdmTestData.ProgressNote() with
        {
            AuthorPractitionerId = null,
            AuthorUserName = "Sr. Meena Pillai",
            SignedByPractitionerId = null,
            SignedByUserName = "Sr. Meena Pillai",
        };

        var bundle = Build(AbdmTestData.ClinicalNoteRecord() with { Note = nurses });
        var composition = Composition(bundle);
        var shared = Documents(bundle).ShouldHaveSingleItem();

        composition.Author.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl(bundle, Single<R4.Organization>(bundle)));
        composition.Attester.ShouldBeEmpty("only a doctor answers for a document");
        (shared.Author.ShouldHaveSingleItem().Reference, shared.Author[0].Display).ShouldBe((null, "Sr. Meena Pillai"));
        (shared.Authenticator.Reference, shared.Authenticator.Display).ShouldBe((null, "Sr. Meena Pillai"));
        composition.Section[0].Text.Div.ShouldContain("<p>Progress note, signed by Sr. Meena Pillai on 25 Sep 2026, 9:20 am</p>");
        ShouldMeetTheGuide(bundle);
    }

    [Fact]
    public void An_addendum_by_staff_who_are_not_doctors_amends_the_note_with_no_one_answering_for_it()
    {
        var addendum = AbdmTestData.Addendum() with
        {
            AuthorPractitionerId = null,
            AuthorUserName = "Sr. Meena Pillai",
            SignedByPractitionerId = null,
            SignedByUserName = "Sr. Meena Pillai",
        };

        var bundle = Build(AbdmTestData.ClinicalNoteRecord(addendum));
        var composition = Composition(bundle);

        composition.Status.ShouldBe(R4.CompositionStatus.Amended);
        composition.Author.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl(bundle, Doctor(bundle, "Dr. Asha Rao")));
        composition.Attester.ShouldBeEmpty("the version is the addendum's, and its signer is not a doctor");
        ShouldMeetTheGuide(bundle);
    }

    [Fact]
    public void A_note_whose_encounter_the_document_does_not_hold_points_to_none()
    {
        // Written outside an encounter, or in one the export could not open: the FHIR API still names the encounter, but a
        // document refers only to what it holds.
        foreach (var note in new[] { AbdmTestData.ProgressNote() with { EncounterId = null }, AbdmTestData.ProgressNote() })
        {
            var data = AbdmTestData.ClinicalNoteRecord() with { Note = note };
            var bundle = Build(data with { Context = data.Context with { Encounter = null } });

            Composition(bundle).Encounter.ShouldBeNull();
            Documents(bundle).ShouldHaveSingleItem().Context.ShouldBeNull();
            bundle.Entry.Select(e => e.Resource).OfType<R4.Encounter>().ShouldBeEmpty();
            ShouldMeetTheGuide(bundle);
        }
    }

    [Fact]
    public void A_note_written_in_a_stay_is_in_the_stay()
    {
        var note = AbdmTestData.ProgressNote() with { EncounterId = null, AdmissionId = AbdmTestData.AdmissionId };
        var data = AbdmTestData.ClinicalNoteRecord() with { Note = note };

        var bundle = Build(data with { Context = data.Context with { Encounter = null, Visit = AbdmTestData.StayVisit() } });

        var stay = Single<R4.Encounter>(bundle);
        Composition(bundle).Encounter.Reference.ShouldBe(FullUrl(bundle, stay));
        Documents(bundle).ShouldHaveSingleItem().Context.Encounter.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl(bundle, stay));
        ShouldMeetTheGuide(bundle);
    }

    [Fact]
    public void Each_kind_of_note_is_saved_under_its_own_name()
    {
        foreach (var (type, file) in new[]
                 {
                     (ClinicalNoteType.ProgressNote, "progress-note"), (ClinicalNoteType.Procedure, "procedure-note"),
                     (ClinicalNoteType.Referral, "referral-note"), (ClinicalNoteType.Other, "clinical-note"),
                 })
        {
            var data = AbdmTestData.ClinicalNoteRecord() with { Note = AbdmTestData.ProgressNote() with { Type = type, Title = null } };
            var document = AbdmRecords.ClinicalNote(data);

            document.FileName.ShouldStartWith(file + "-");
            document.Title.ShouldBe(DocumentReferenceMapper.NoteName(type));
            document.Sections.ShouldHaveSingleItem().Title.ShouldBe(DocumentReferenceMapper.NoteName(type), "a note without a title goes by its kind");
        }
    }

    [Fact]
    public void Only_a_signed_note_is_exported_never_a_counselling_note_and_an_addendum_only_with_its_note()
    {
        AbdmRecordLoader.NoteNotExportable(ClinicalNoteType.ProgressNote, isSigned: true, isAddendum: false).ShouldBeNull();
        AbdmRecordLoader.NoteNotExportable(ClinicalNoteType.Referral, isSigned: true, isAddendum: false).ShouldBeNull();
        AbdmRecordLoader.NoteNotExportable(ClinicalNoteType.ProgressNote, isSigned: false, isAddendum: false).ShouldBe("Sign the note before exporting it.");
        AbdmRecordLoader.NoteNotExportable(ClinicalNoteType.ProgressNote, isSigned: true, isAddendum: true).ShouldNotBeNull().ShouldContain("with the note it adds to");
        AbdmRecordLoader.NoteNotExportable(ClinicalNoteType.ProgressNote, isSigned: false, isAddendum: true).ShouldNotBeNull()
            .ShouldContain("with the note it adds to", customMessage: "signing it would not let it go on its own");
        foreach (var signed in new[] { true, false })
        {
            AbdmRecordLoader.NoteNotExportable(ClinicalNoteType.Counselling, signed, isAddendum: false).ShouldNotBeNull().ShouldContain("counselling note");
            AbdmRecordLoader.NoteNotExportable(ClinicalNoteType.Counselling, signed, isAddendum: true).ShouldNotBeNull().ShouldContain("counselling note");
        }
    }

    [Fact]
    public void A_note_is_named_for_the_patient_by_its_kind_and_when_it_was_signed_never_its_title()
    {
        AbdmCareContexts.Describe(AbdmRecordType.ClinicalNote, AbdmTestData.NoteId, NoteSigned, AbdmRecordLoader.NoteLabel(ClinicalNoteType.ProgressNote, NoteSigned))
            .Display.ShouldBe("Clinical note: Progress note, signed at 9:20 am, 25 Sep 2026");
        AbdmCareContexts.Describe(AbdmRecordType.ClinicalNote, AbdmTestData.NoteId, NoteSigned, AbdmRecordLoader.NoteLabel(ClinicalNoteType.Other, NoteSigned))
            .Display.ShouldBe("Clinical note: signed at 9:20 am, 25 Sep 2026");
        AbdmRecordLoader.NoteLabel(ClinicalNoteType.Referral, AddendumSigned).ShouldBe("Referral note, signed at 8:30 am");
    }

    [Fact]
    public void A_note_goes_as_a_health_document_and_only_under_a_consent_for_health_documents()
    {
        var id = AbdmTestData.NoteId;

        (AbdmGatewayMessages.HiType(AbdmRecordType.ClinicalNote), AbdmLinkingService.ReferenceNumber(AbdmRecordType.ClinicalNote, id))
            .ShouldBe(("HealthDocumentRecord", $"clinical-note/{id:D}"));
        AbdmDataFlowService.Covers("HealthDocumentRecord", AbdmRecordType.ClinicalNote).ShouldBeTrue();
        AbdmDataFlowService.Covers("OPConsultation,DischargeSummary", AbdmRecordType.ClinicalNote).ShouldBeFalse();
        AbdmDataFlowService.Covers("HealthDocumentRecord", AbdmRecordType.OpConsultation).ShouldBeFalse();
    }
}
