using FellowOakDicom;
using FellowOakDicom.Network;
using HyFib.HealthCare360.Integrations.Pacs.Worklist;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs.Worklist;

/// <summary>Reading a worklist query and writing the answers in DICOM, as modalities expect them.</summary>
public sealed class WorklistDicomTests
{
    private static readonly WorklistPresentation Latin1 = new(WorklistConfig.Latin1, "CT", null);

    /// <summary>The identifier fo-dicom's own worklist query sends (as most consoles do).</summary>
    private static DicomDataset Query(string? patientName = null, string? modality = "CT", string? station = null) =>
        DicomCFindRequest.CreateWorklistQuery(patientName: patientName, modality: modality, stationAE: station).Dataset;

    [Fact]
    public void Reads_the_keys_of_a_worklist_query()
    {
        var identifier = Query(patientName: "SHARMA*", station: "CT1");
        identifier.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0]
            .AddOrUpdate(DicomTag.ScheduledProcedureStepStartDate, "20261002-20261003");

        var keys = WorklistDicom.Keys(identifier);

        keys.PatientName.ShouldBe("SHARMA*");
        keys.Modality.ShouldBe("CT");
        keys.StationAeTitle.ShouldBe("CT1");
        keys.Dates.ShouldBe(new DicomRange("20261002", "20261003"));
        keys.PatientId.ShouldBeNull();
    }

    [Fact]
    public void Without_an_identifier_everything_matches() => WorklistDicom.Keys(null).ShouldBe(WorklistKeys.Any);

    [Fact]
    public void Answers_the_attributes_a_modality_needs()
    {
        var answer = WorklistDicom.Response(WorklistMatchingTests.Item(), Query(), Latin1);

        answer.GetString(DicomTag.SpecificCharacterSet).ShouldBe("ISO_IR 100");
        answer.GetString(DicomTag.PatientName).ShouldBe("SHARMA^RAVI^KUMAR");
        answer.GetString(DicomTag.PatientID).ShouldBe("NH26000001");
        answer.GetString(DicomTag.PatientBirthDate).ShouldBe("19800101");
        answer.GetString(DicomTag.PatientSex).ShouldBe("M");
        answer.GetString(DicomTag.AccessionNumber).ShouldBe("NHRD260000042");
        answer.GetString(DicomTag.StudyInstanceUID).ShouldBe("2.25.123456789");
        answer.GetString(DicomTag.RequestedProcedureID).ShouldBe("NHRD260000042");
        answer.GetString(DicomTag.RequestedProcedureDescription).ShouldBe("CT Head plain");
        answer.GetString(DicomTag.ReferringPhysicianName).ShouldBe("Dr Rao");

        var step = answer.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items.ShouldHaveSingleItem();
        step.GetString(DicomTag.Modality).ShouldBe("CT");
        step.GetString(DicomTag.ScheduledProcedureStepStartDate).ShouldBe("20261002");
        step.GetString(DicomTag.ScheduledProcedureStepStartTime).ShouldBe("103000");
        step.GetString(DicomTag.ScheduledProcedureStepID).ShouldBe("NHRD260000042");
        step.GetString(DicomTag.ScheduledProcedureStepDescription).ShouldBe("CT Head plain");
    }

    [Fact]
    public void Answers_only_what_was_asked_with_unknown_attributes_empty()
    {
        var identifier = new DicomDataset
        {
            { DicomTag.PatientName, string.Empty },
            { DicomTag.PatientWeight, string.Empty },
            new DicomSequence(DicomTag.ScheduledProcedureStepSequence, new DicomDataset { { DicomTag.Modality, "CT" } }),
        };

        var answer = WorklistDicom.Response(WorklistMatchingTests.Item(), identifier, Latin1);

        answer.Select(item => item.Tag).ShouldBe([DicomTag.SpecificCharacterSet, DicomTag.PatientName, DicomTag.PatientWeight, DicomTag.ScheduledProcedureStepSequence],
            ignoreOrder: true);
        answer.GetValueCount(DicomTag.PatientWeight).ShouldBe(0);
        answer.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0].Select(item => item.Tag).ShouldBe([DicomTag.Modality]);
    }

    [Fact]
    public void An_empty_step_item_asks_for_the_whole_step()
    {
        var identifier = new DicomDataset { new DicomSequence(DicomTag.ScheduledProcedureStepSequence, new DicomDataset()) };

        var step = WorklistDicom.Response(WorklistMatchingTests.Item(), identifier, Latin1).GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];

        step.Contains(DicomTag.ScheduledProcedureStepStatus).ShouldBeTrue();
        step.GetString(DicomTag.ScheduledProcedureStepLocation).ShouldBe("CT-1");
    }

    [Fact]
    public void An_accession_number_longer_than_dicom_allows_is_sent_empty_never_cut_short()
    {
        var item = WorklistMatchingTests.Item(accession: "ABCDEFRD260000042");
        var identifier = Query();
        identifier.AddOrUpdate(DicomTag.FillerOrderNumberImagingServiceRequest, string.Empty);

        var answer = WorklistDicom.Response(item, identifier, Latin1);

        answer.GetValueCount(DicomTag.AccessionNumber).ShouldBe(0);
        answer.GetString(DicomTag.FillerOrderNumberImagingServiceRequest).ShouldBe("ABCDEFRD260000042");
        answer.GetString(DicomTag.RequestedProcedureID).ShouldBe("HC0000000000000A");
    }

    [Fact]
    public void A_station_is_sent_as_the_one_asked_for_when_none_is_assigned()
    {
        var answer = WorklistDicom.Response(WorklistMatchingTests.Item(station: null), Query(station: "CT1"), Latin1 with { StationAeTitle = "CT1" });

        answer.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0].GetString(DicomTag.ScheduledStationAETitle).ShouldBe("CT1");
    }

    [Fact]
    public void Plain_ascii_answers_carry_no_character_set()
    {
        var answer = WorklistDicom.Response(WorklistMatchingTests.Item(), Query(), Latin1 with { CharacterSet = string.Empty });

        answer.Contains(DicomTag.SpecificCharacterSet).ShouldBeFalse();
    }

    [Theory]
    [InlineData("SHARMA^RAVI", "SHARMA^RAVI")]
    [InlineData("SHARMA^RAVI^^", "SHARMA^RAVI")]
    [InlineData("A\\B", "A B")]
    public void Person_names_are_dicom_names(string value, string expected) => WorklistDicom.PersonName(value).ShouldBe(expected);

    [Fact]
    public void A_long_name_keeps_the_family_name_whole()
    {
        var family = new string('F', 40);
        var name = WorklistDicom.PersonName($"{family}^{new string('G', 20)}^{new string('M', 20)}")!;

        name.ShouldBe($"{family}^{new string('G', 20)}");
        WorklistDicom.PersonName(new string('F', 80))!.Length.ShouldBe(64);
    }
}
