using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>Result-sheet layout of ordered tests and the tube status derived from its tests.</summary>
public sealed class LabWorkflowTests
{
    [Fact]
    public void A_single_analyte_test_is_one_result_line()
    {
        var potassium = Analyte("K");

        var slot = LabWorkflow.Expand(potassium, Map(potassium)).ShouldHaveSingleItem();

        slot.Analyte.ShouldBe(potassium);
        slot.GroupName.ShouldBeNull();
        slot.DisplayOrder.ShouldBe(1);
    }

    [Fact]
    public void A_panel_expands_to_its_analytes_in_report_order_with_sub_headings()
    {
        var hb = Analyte("HB");
        var neutrophils = Analyte("NEUT");
        var lymphocytes = Analyte("LYMPH");
        var cbc = Panel("LAB-CBC", (lymphocytes, 3, "DLC"), (hb, 1, null), (neutrophils, 2, "DLC"));

        var slots = LabWorkflow.Expand(cbc, Map(cbc, hb, neutrophils, lymphocytes));

        slots.Select(s => s.Analyte.Code).ShouldBe(["HB", "NEUT", "LYMPH"]);
        slots.Select(s => s.GroupName).ShouldBe([null, "DLC", "DLC"]);
        slots.Select(s => s.DisplayOrder).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void A_nested_panel_is_headed_by_its_name_and_an_analyte_ordered_twice_appears_once()
    {
        var hb = Analyte("HB");
        var platelets = Analyte("PLT");
        var glucose = Analyte("LAB-FBS");
        var cbc = Panel("LAB-CBC", (hb, 1, null), (platelets, 2, null));
        cbc.Name = "Complete blood count";
        var healthCheck = Panel("HC-BASIC", (cbc, 1, null), (glucose, 2, null), (hb, 3, null));

        var slots = LabWorkflow.Expand(healthCheck, Map(healthCheck, cbc, hb, platelets, glucose));

        slots.Select(s => s.Analyte.Code).ShouldBe(["HB", "PLT", "LAB-FBS"]);
        slots[0].GroupName.ShouldBe("Complete blood count");
        slots[1].GroupName.ShouldBe("Complete blood count");
        slots[2].GroupName.ShouldBeNull();
    }

    [Fact]
    public void A_panel_without_components_is_still_resulted_as_one_free_line()
    {
        var incomplete = Panel("LAB-NEW-PANEL");

        LabWorkflow.Expand(incomplete, Map(incomplete)).ShouldHaveSingleItem().Analyte.ShouldBe(incomplete);
    }

    [Fact]
    public void A_panel_that_contains_itself_does_not_loop()
    {
        var loop = Panel("LOOP");
        loop.Components.Add(new LabTestComponent { PanelTestId = loop.Id, TestId = loop.Id, DisplayOrder = 1 });

        LabWorkflow.Expand(loop, Map(loop)).ShouldHaveSingleItem().Analyte.ShouldBe(loop);
    }

    [Fact]
    public void A_tube_is_complete_when_every_test_on_it_is_verified()
    {
        var tube = Sample(SampleStatus.InProcess);

        LabWorkflow.RefreshSample(tube, [Item(LabOrderItemStatus.Verified), Item(LabOrderItemStatus.Released)], [], Now);

        tube.Status.ShouldBe(SampleStatus.Completed);
        tube.CompletedAt.ShouldBe(Now);
    }

    [Fact]
    public void A_reopened_result_brings_a_completed_tube_back_into_processing()
    {
        var tube = Sample(SampleStatus.Completed);

        LabWorkflow.RefreshSample(tube, [Item(LabOrderItemStatus.Verified), Item(LabOrderItemStatus.ResultsEntered)], [Result(LabResultStatus.Entered)], Now);

        tube.Status.ShouldBe(SampleStatus.InProcess);
        tube.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public void A_received_tube_starts_processing_with_its_first_result()
    {
        var waiting = Sample(SampleStatus.Received);
        var started = Sample(SampleStatus.Received);

        LabWorkflow.RefreshSample(waiting, [Item(LabOrderItemStatus.InLab)], [Result(LabResultStatus.Pending)], Now);
        LabWorkflow.RefreshSample(started, [Item(LabOrderItemStatus.InLab)], [Result(LabResultStatus.Pending), Result(LabResultStatus.Entered)], Now);

        waiting.Status.ShouldBe(SampleStatus.Received);
        started.Status.ShouldBe(SampleStatus.InProcess);
    }

    [Theory]
    [InlineData(SampleStatus.Pending)]
    [InlineData(SampleStatus.Collected)]
    [InlineData(SampleStatus.Rejected)]
    [InlineData(SampleStatus.Cancelled)]
    public void Tubes_outside_the_lab_are_left_alone(SampleStatus status)
    {
        var tube = Sample(status);

        LabWorkflow.RefreshSample(tube, [Item(LabOrderItemStatus.Verified)], [Result(LabResultStatus.Verified)], Now);

        tube.Status.ShouldBe(status);
    }

    [Fact]
    public void A_tube_whose_tests_were_all_cancelled_is_not_reported_complete()
    {
        var tube = Sample(SampleStatus.Received);

        LabWorkflow.RefreshSample(tube, [], [], Now);

        tube.Status.ShouldBe(SampleStatus.Received);
    }

    private static LabTest Analyte(string code) => new() { Code = code, Name = code, ResultType = LabResultType.Numeric };

    private static LabTest Panel(string code, params (LabTest Test, int Order, string? Group)[] components)
    {
        var panel = new LabTest { Code = code, Name = code, IsPanel = true };
        foreach (var (test, order, group) in components)
        {
            panel.Components.Add(new LabTestComponent { PanelTestId = panel.Id, TestId = test.Id, DisplayOrder = order, GroupName = group });
        }

        return panel;
    }

    private static Dictionary<Guid, LabTest> Map(params LabTest[] tests) => tests.ToDictionary(t => t.Id);
}
