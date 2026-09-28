using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class MedicalRecordsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 26);

    private static MedicalRecord InpatientChart(bool mlc = false) => new() { PatientId = Guid.CreateVersion7(), AdmissionId = Guid.CreateVersion7(), IsMlc = mlc };

    [Fact]
    public void Deficiencies_block_completion_until_resolved()
    {
        var chart = InpatientChart();
        var deficiency = chart.AddDeficiency(DeficiencyType.MissingDischargeSummary, "Discharge summary is missing.", null, null, "discharge-summary", null,
            systemDetected: true, Now).Value;

        chart.Status.ShouldBe(MedicalRecordStatus.Deficient);
        chart.Complete(null, Now, Today).Error!.Code.ShouldBe("chart.deficient");

        chart.ResolveDeficiency(deficiency.Id, null, "Signed", Now).IsSuccess.ShouldBeTrue();
        chart.Status.ShouldBe(MedicalRecordStatus.Incomplete);
        chart.ResolveDeficiency(deficiency.Id, null, null, Now).Error!.Code.ShouldBe("deficiency.resolved");

        chart.Complete(null, Now, Today).IsSuccess.ShouldBeTrue();
        chart.Status.ShouldBe(MedicalRecordStatus.Complete);
        chart.RetainUntil.ShouldBe(Today.AddYears(5));
    }

    [Theory]
    [InlineData(true, false, 5)]
    [InlineData(false, false, 3)]
    public void Retention_follows_the_record_type(bool inpatient, bool mlc, int years) =>
        MedicalRecord.RetentionUntil(inpatient, mlc, Today).ShouldBe(Today.AddYears(years));

    [Fact]
    public void Medico_legal_records_are_kept_permanently() => MedicalRecord.RetentionUntil(true, true, Today).ShouldBeNull();

    [Fact]
    public void Only_complete_charts_are_archived_and_archives_are_frozen()
    {
        var chart = InpatientChart();
        chart.Archive("Rack 4, shelf B", Now).Error!.Code.ShouldBe("chart.not_complete");

        chart.Complete(null, Now, Today);
        chart.Archive("Rack 4, shelf B", Now).IsSuccess.ShouldBeTrue();

        chart.Status.ShouldBe(MedicalRecordStatus.Archived);
        chart.ArchiveLocation.ShouldBe("Rack 4, shelf B");
        chart.AddDeficiency(DeficiencyType.Other, "Late note", null, null, null, null, false, Now).Error!.Code.ShouldBe("chart.archived");
    }

    [Fact]
    public void Open_deficiency_lookup_matches_type_and_reference()
    {
        var chart = InpatientChart();
        chart.AddDeficiency(DeficiencyType.UnsignedNote, "Note not signed", null, null, "note:1", null, true, Now);

        chart.HasOpenDeficiency(DeficiencyType.UnsignedNote, "note:1").ShouldBeTrue();
        chart.HasOpenDeficiency(DeficiencyType.UnsignedNote, "note:2").ShouldBeFalse();
    }

    [Fact]
    public void Record_request_is_issued_returned_and_overdue_when_late()
    {
        var request = new RecordRequest { PatientId = Guid.CreateVersion7(), RequestedByName = "Dr. Mehta", RequestedAt = Now };

        request.Return(null, Now).Error!.Code.ShouldBe("record_request.not_issued");
        request.Issue(Today.AddDays(3), null, Now).IsSuccess.ShouldBeTrue();
        request.EffectiveStatus(Today).ShouldBe(RecordRequestStatus.Issued);
        request.EffectiveStatus(Today.AddDays(4)).ShouldBe(RecordRequestStatus.Overdue);
        request.Cancel("No longer needed").Error!.Code.ShouldBe("record_request.not_pending");

        request.Return(null, Now).IsSuccess.ShouldBeTrue();
        request.EffectiveStatus(Today.AddDays(10)).ShouldBe(RecordRequestStatus.Returned);
    }

    [Fact]
    public void Pending_record_request_can_be_cancelled()
    {
        var request = new RecordRequest { PatientId = Guid.CreateVersion7(), RequestedByName = "Insurance desk", RequestedAt = Now };

        request.Cancel("Duplicate request").IsSuccess.ShouldBeTrue();
        request.Status.ShouldBe(RecordRequestStatus.Cancelled);
        request.Issue(null, null, Now).Error!.Code.ShouldBe("record_request.not_pending");
    }

    [Fact]
    public void Coding_needs_exactly_one_primary_diagnosis()
    {
        var coding = new EncounterCoding { PatientId = Guid.CreateVersion7(), AdmissionId = Guid.CreateVersion7() };

        coding.Code([new(CodingEntryKind.Diagnosis, "I10", "Essential (primary) hypertension", false)], null, Now).Error!.Fields!.Keys.ShouldContain("entries");
        coding.Code(
        [
            new(CodingEntryKind.Diagnosis, "I21.4", "Acute subendocardial myocardial infarction", true),
            new(CodingEntryKind.Diagnosis, "E11.9", "Non-insulin-dependent diabetes mellitus without complications", true),
        ], null, Now).IsFailure.ShouldBeTrue();
        coding.Code(
        [
            new(CodingEntryKind.Diagnosis, "I21.4", "Acute subendocardial myocardial infarction", true),
            new(CodingEntryKind.Procedure, "027034Z", "PTCA with drug-eluting stent", true),
        ], null, Now).IsFailure.ShouldBeTrue();
        coding.Code(
        [
            new(CodingEntryKind.Diagnosis, "I21.4", "Acute subendocardial myocardial infarction", true),
            new(CodingEntryKind.Diagnosis, "i21.4", "Duplicate", false),
        ], null, Now).IsFailure.ShouldBeTrue();
        coding.Status.ShouldBe(CodingStatus.Pending);
    }

    [Fact]
    public void Coding_orders_the_primary_first_and_is_verified_by_another_coder()
    {
        var coder = Guid.CreateVersion7();
        var verifier = Guid.CreateVersion7();
        var coding = new EncounterCoding { PatientId = Guid.CreateVersion7(), AdmissionId = Guid.CreateVersion7() };
        coding.Verify(verifier, Now).Error!.Code.ShouldBe("coding.not_coded");

        coding.Code(
        [
            new(CodingEntryKind.Procedure, "027034Z", "PTCA with drug-eluting stent", false),
            new(CodingEntryKind.Diagnosis, "e11.9", "Non-insulin-dependent diabetes mellitus without complications", false),
            new(CodingEntryKind.Diagnosis, "I21.4", "Acute subendocardial myocardial infarction", true),
        ], coder, Now).IsSuccess.ShouldBeTrue();

        coding.Status.ShouldBe(CodingStatus.Coded);
        coding.Entries.Select(e => e.Code).ShouldBe(["I21.4", "E11.9", "027034Z"]);
        coding.Verify(coder, Now).Error!.Code.ShouldBe("coding.self_verification");
        coding.Verify(verifier, Now).IsSuccess.ShouldBeTrue();
        coding.Status.ShouldBe(CodingStatus.Verified);

        coding.Code([new(CodingEntryKind.Diagnosis, "I21.4", "Acute subendocardial myocardial infarction", true)], coder, Now);
        coding.Status.ShouldBe(CodingStatus.Coded);
        coding.VerifiedByUserId.ShouldBeNull();
    }
}
