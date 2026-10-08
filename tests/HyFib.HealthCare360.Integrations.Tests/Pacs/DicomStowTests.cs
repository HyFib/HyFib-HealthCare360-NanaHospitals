using System.Net;
using FellowOakDicom;
using HyFib.HealthCare360.Integrations.Pacs;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs;

/// <summary>How a machine's DICOM file is filed under HC360's patient and study, and how the PACS's answer is read (STOW-RS).</summary>
public sealed class DicomStowTests
{
    internal const string StudyUid = "2.25.123456789012345678901234567890";
    internal const string SopInstanceUid = "1.2.826.0.1.3680043.10.1360.9.1";

    internal static readonly PacsStudyIdentity Study = new("NH00012345", "SHARMA^RAVI", "19800215", "M", StudyUid, "NHCD26000042", "2D Echo");

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 4, 30, 0, TimeSpan.Zero);

    /// <summary>An ultrasound loop as an echo machine exports it: its own patient ID, name and study, in Latin-1.</summary>
    internal static byte[] EchoFile(
        string? characterSet = "ISO_IR 100", string patientName = "MÜLLER^ANNA", string sopInstanceUid = SopInstanceUid, string institution = "Clínica Norte")
    {
        var dataset = new DicomDataset();
        if (characterSet is not null)
        {
            dataset.Add(DicomTag.SpecificCharacterSet, characterSet);
        }

        dataset.Add(DicomTag.SOPClassUID, DicomUID.UltrasoundMultiFrameImageStorage);
        dataset.Add(DicomTag.SOPInstanceUID, sopInstanceUid);
        dataset.Add(DicomTag.PatientID, "ECHO-17");
        dataset.Add(DicomTag.PatientName, patientName);
        dataset.Add(DicomTag.PatientSex, "F");
        dataset.Add(DicomTag.StudyInstanceUID, "1.2.840.113619.2.1.7");
        dataset.Add(DicomTag.SeriesInstanceUID, "1.2.840.113619.2.1.7.1");
        dataset.Add(DicomTag.StudyDescription, "Écho cardiaque");
        dataset.Add(DicomTag.InstitutionName, institution);
        return Save(new DicomFile(dataset));
    }

    [Fact]
    public void A_file_is_filed_under_the_patient_and_the_study_and_keeps_its_object_uids()
    {
        var prepared = DicomStow.Prepare(new PacsStoreFile("file-1", EchoFile()), Study, Now, out var problem).ShouldNotBeNull();

        problem.ShouldBeNull();
        prepared.Reference.ShouldBe("file-1");
        prepared.SopInstanceUid.ShouldBe(SopInstanceUid);
        prepared.SopClassUid.ShouldBe(DicomUID.UltrasoundMultiFrameImageStorage.UID);
        var sent = Open(prepared.Content);
        sent.GetString(DicomTag.PatientID).ShouldBe("NH00012345");
        sent.GetString(DicomTag.PatientName).ShouldBe("SHARMA^RAVI");
        sent.GetString(DicomTag.PatientBirthDate).ShouldBe("19800215");
        sent.GetString(DicomTag.PatientSex).ShouldBe("M");
        sent.GetString(DicomTag.StudyInstanceUID).ShouldBe(StudyUid);
        sent.GetString(DicomTag.AccessionNumber).ShouldBe("NHCD26000042");
        sent.GetString(DicomTag.StudyDescription).ShouldBe("2D Echo");
        sent.GetString(DicomTag.SOPInstanceUID).ShouldBe(SopInstanceUid, "sending the file again replaces the same object");
        sent.GetString(DicomTag.SeriesInstanceUID).ShouldBe("1.2.840.113619.2.1.7.1");
        sent.GetString(DicomTag.InstitutionName).ShouldBe("Clínica Norte", "the machine's other values are kept, in its character set");
        sent.GetString(DicomTag.SpecificCharacterSet).ShouldBe("ISO_IR 100");
    }

    [Fact]
    public void The_values_the_machine_recorded_are_kept_in_the_original_attributes_sequence()
    {
        var sent = Open(DicomStow.Prepare(new PacsStoreFile("file-1", EchoFile()), Study, Now, out _)!.Content);

        var modification = sent.GetSequence(DicomTag.OriginalAttributesSequence).Items.ShouldHaveSingleItem();
        modification.GetString(DicomTag.ModifyingSystem).ShouldBe("HC360");
        modification.GetString(DicomTag.ReasonForTheAttributeModification).ShouldBe("COERCE");
        modification.GetSingleValue<DateTime>(DicomTag.AttributeModificationDateTime).ShouldBe(Now.UtcDateTime);
        var original = modification.GetSequence(DicomTag.ModifiedAttributesSequence).Items.ShouldHaveSingleItem();
        original.GetString(DicomTag.PatientID).ShouldBe("ECHO-17");
        original.GetString(DicomTag.PatientName).ShouldBe("MÜLLER^ANNA");
        original.GetString(DicomTag.PatientSex).ShouldBe("F");
        original.GetString(DicomTag.StudyInstanceUID).ShouldBe("1.2.840.113619.2.1.7");
        original.GetString(DicomTag.StudyDescription).ShouldBe("Écho cardiaque");
        original.Contains(DicomTag.AccessionNumber).ShouldBeFalse("the machine recorded none");
    }

    [Fact]
    public void A_file_already_filed_under_the_study_gets_no_modification_entry()
    {
        var once = DicomStow.Prepare(new PacsStoreFile("file-1", EchoFile()), Study, Now, out _)!.Content;

        var again = Open(DicomStow.Prepare(new PacsStoreFile("file-1", once), Study, Now.AddHours(1), out _)!.Content);

        again.GetSequence(DicomTag.OriginalAttributesSequence).Items.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("ISO_IR 100", "Clínica Norte")]
    [InlineData(null, "City Heart Clinic")]
    public void A_name_the_files_character_set_cannot_hold_changes_the_file_to_utf8(string? characterSet, string institution)
    {
        var study = Study with { PatientName = "शर्मा^रवि" };

        var sent = Open(DicomStow.Prepare(new PacsStoreFile("file-1", EchoFile(characterSet, "PATEL^ASHA", institution: institution)), study, Now, out _)!.Content);

        sent.GetString(DicomTag.SpecificCharacterSet).ShouldBe(DicomStow.Utf8);
        sent.GetString(DicomTag.PatientName).ShouldBe("शर्मा^रवि");
        sent.GetString(DicomTag.InstitutionName).ShouldBe(institution, "the other text is written again in UTF-8");
    }

    [Fact]
    public void A_latin1_file_holds_a_latin1_name_without_a_change_of_character_set()
    {
        var sent = Open(DicomStow.Prepare(new PacsStoreFile("file-1", EchoFile()), Study with { PatientName = "D'SOUZA^JOSÉ" }, Now, out _)!.Content);

        sent.GetString(DicomTag.SpecificCharacterSet).ShouldBe("ISO_IR 100");
        sent.GetString(DicomTag.PatientName).ShouldBe("D'SOUZA^JOSÉ");
    }

    [Fact]
    public void An_accession_number_too_long_for_dicom_is_left_empty_and_long_values_are_cut_to_fit()
    {
        var study = Study with { AccessionNumber = "NHMAINCD2600000042", StudyDescription = new string('E', 80), PatientName = new string('A', 70) + "^RAVI" };

        var sent = Open(DicomStow.Prepare(new PacsStoreFile("file-1", EchoFile()), study, Now, out _)!.Content);

        sent.GetString(DicomTag.AccessionNumber).ShouldBeEmpty();
        sent.GetString(DicomTag.StudyDescription).Length.ShouldBe(64);
        sent.GetString(DicomTag.PatientName).Length.ShouldBe(64);
    }

    [Fact]
    public void A_study_without_a_description_keeps_the_machines()
    {
        var sent = Open(DicomStow.Prepare(new PacsStoreFile("file-1", EchoFile()), Study with { StudyDescription = null }, Now, out _)!.Content);

        sent.GetString(DicomTag.StudyDescription).ShouldBe("Écho cardiaque");
    }

    [Fact]
    public void Files_that_are_not_dicom_objects_a_pacs_can_store_are_not_sent()
    {
        DicomStow.Prepare(new PacsStoreFile("pdf", "%PDF-1.7 report"u8.ToArray()), Study, Now, out var notDicom).ShouldBeNull();
        notDicom.ShouldBe("HC360 could not read this file as DICOM.");

        var directory = new DicomDataset { { DicomTag.SOPClassUID, DicomUID.MediaStorageDirectoryStorage }, { DicomTag.SOPInstanceUID, "1.2.3.4" } };
        DicomStow.Prepare(new PacsStoreFile("dicomdir", Save(new DicomFile(directory))), Study, Now, out var dicomDir).ShouldBeNull();
        dicomDir.ShouldNotBeNull().ShouldContain("DICOMDIR");

        var file = DicomFile.Open(new MemoryStream(EchoFile()), FileReadOption.ReadAll);
        file.Dataset.Remove(DicomTag.SOPInstanceUID);
        DicomStow.Prepare(new PacsStoreFile("no-uid", Save(file)), Study, Now, out var withoutUids).ShouldBeNull();
        withoutUids.ShouldBe("The file has no SOP class or instance UID, so a PACS cannot store it.");
    }

    [Fact]
    public void The_body_is_one_dicom_part_of_a_multipart_related_request()
    {
        var item = DicomStow.Prepare(new PacsStoreFile("file-1", EchoFile()), Study, Now, out _)!;

        using var body = DicomStow.Body(item);

        var header = body.Headers.ContentType!.ToString();
        header.ShouldStartWith("multipart/related");
        header.ShouldContain("type=\"application/dicom\"");
        var part = body.ShouldHaveSingleItem();
        part.Headers.ContentType!.MediaType.ShouldBe("application/dicom");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, PacsStoreStatus.Stored)]
    [InlineData(HttpStatusCode.Accepted, PacsStoreStatus.Stored)]
    [InlineData(HttpStatusCode.Conflict, PacsStoreStatus.Refused)]
    [InlineData(HttpStatusCode.BadRequest, PacsStoreStatus.Refused)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, PacsStoreStatus.Refused)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, PacsStoreStatus.Refused)]
    [InlineData(HttpStatusCode.Unauthorized, PacsStoreStatus.Failed)]
    [InlineData(HttpStatusCode.NotFound, PacsStoreStatus.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PacsStoreStatus.Failed)]
    public void The_http_status_says_what_became_of_the_object(HttpStatusCode status, PacsStoreStatus expected)
    {
        var outcome = DicomStow.Outcome(Item, status, string.Empty);

        outcome.Status.ShouldBe(expected);
        outcome.Reference.ShouldBe("file-1");
        outcome.SopInstanceUid.ShouldBe(SopInstanceUid);
        if (expected != PacsStoreStatus.Stored)
        {
            outcome.Message.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void A_failed_sop_sequence_gives_the_pacss_reason()
    {
        var body = $$$"""{"00081198":{"vr":"SQ","Value":[{"00081155":{"vr":"UI","Value":["{{{SopInstanceUid}}}"]},"00081197":{"vr":"US","Value":[43264]}}]}}""";

        var outcome = DicomStow.Outcome(Item, HttpStatusCode.Conflict, body);

        outcome.Status.ShouldBe(PacsStoreStatus.Refused);
        outcome.Message.ShouldBe("The PACS refused the object: the object does not match its SOP class.");
    }

    [Fact]
    public void A_failure_in_a_200_answer_still_refuses_the_object()
    {
        // Some PACS answer 200 with the failure in the body.
        var body = $$$"""[{"00081198":{"vr":"SQ","Value":[{"00081155":{"vr":"UI","Value":["{{{SopInstanceUid}}}"]},"00081197":{"vr":"US","Value":[290]}}]}}]""";

        var outcome = DicomStow.Outcome(Item, HttpStatusCode.OK, body);

        outcome.Status.ShouldBe(PacsStoreStatus.Refused);
        outcome.Message.ShouldBe("The PACS refused the object: it does not support this kind of object (SOP class).");
    }

    [Fact]
    public void An_object_the_pacs_already_holds_counts_as_stored()
    {
        var body = $$$"""{"00081198":{"vr":"SQ","Value":[{"00081155":{"vr":"UI","Value":["{{{SopInstanceUid}}}"]},"00081197":{"vr":"US","Value":[273]}}]}}""";

        var outcome = DicomStow.Outcome(Item, HttpStatusCode.Conflict, body);

        outcome.Status.ShouldBe(PacsStoreStatus.Stored);
        outcome.Message.ShouldBe("The PACS already held this object.");
    }

    [Fact]
    public void A_warning_on_a_stored_object_is_passed_on()
    {
        var body = $$$"""{"00081199":{"vr":"SQ","Value":[{"00081155":{"vr":"UI","Value":["{{{SopInstanceUid}}}"]},"00081196":{"vr":"US","Value":[45056]}}]}}""";

        var outcome = DicomStow.Outcome(Item, HttpStatusCode.Accepted, body);

        outcome.Status.ShouldBe(PacsStoreStatus.Stored);
        outcome.Message.ShouldBe("The PACS stored the object, and changed some of its values.");
    }

    [Fact]
    public void Another_objects_failure_does_not_refuse_this_one()
    {
        var body = """{"00081198":{"vr":"SQ","Value":[{"00081155":{"vr":"UI","Value":["1.2.3.999"]},"00081197":{"vr":"US","Value":[272]}}]}}""";

        DicomStow.Outcome(Item, HttpStatusCode.OK, body).Status.ShouldBe(PacsStoreStatus.Stored);
    }

    private static DicomObjectToStore Item => new("file-1", DicomUID.UltrasoundMultiFrameImageStorage.UID, SopInstanceUid, [1, 2, 3]);

    private static byte[] Save(DicomFile file)
    {
        using var stream = new MemoryStream();
        file.Save(stream);
        return stream.ToArray();
    }

    private static DicomDataset Open(byte[] content) => DicomFile.Open(new MemoryStream(content), FileReadOption.ReadAll).Dataset;
}
