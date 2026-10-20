using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Integrations.Features.Labels;
using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Flags;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Label printing over the real API (INT-BAR): the wristband carries the allergy mark from the chart whoever prints it,
/// and marks allergies never assessed differently from "no known allergies"; the label endpoints answer printer
/// commands to any staff member, and only staff allowed to set up printers can change a workstation's printers, each
/// change recorded in the audit trail.
/// </summary>
public sealed class LabelPrintingTests(HospitalServer server)
{
    private static readonly LabelPrinterSetup Zebra = new("zpl", 203, 50, 25);

    [Fact]
    public async Task A_wristband_printed_at_the_admission_desk_carries_the_allergy_mark_from_the_chart()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Arun"), MiddleName: null, LastName: "Band" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1980, 3, 14), AgeYears: null, AgeMonths: null, Phone: "9" + Digits(9), AlternatePhone: null,
            Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null,
            EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null,
            Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null,
            PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var ward = (await admin.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).First(w => w.Code == "GWM");
        var bed = (await admin.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patient.Id}&wardId={ward.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant);
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Cellulitis, left leg", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null, NotifyPatient: false));
        var path = $"/api/v1/inpatient/admissions/{admission.Id}/wristband";

        try
        {
            var before = await frontOffice.GetAsync<WristbandDto>(path);
            before.Uhid.ShouldBe(patient.Uhid);
            before.AdmissionNumber.ShouldBe(admission.AdmissionNumber);
            before.BedLabel.ShouldBe(admission.BedLabel);
            before.Allergies.ShouldBe(AllergyRecordingStatus.NotRecorded);

            // Allergies never assessed print an outlined NOT RECORDED mark, never the bare band of "no known allergies".
            var unknown = await PrintAsync(before);
            unknown.ShouldContain("^FDALLERGIES^FS");
            unknown.ShouldContain("^FDNOT RECORDED^FS");
            unknown.ShouldNotContain("^FDALLERGY^FS");

            await nurse.PostAsync<AllergyStatusDto>($"/api/v1/emr/patients/{patient.Id}/allergies/none");
            var none = await frontOffice.GetAsync<WristbandDto>(path);
            none.Allergies.ShouldBe(AllergyRecordingStatus.NoKnownAllergies);
            (await PrintAsync(none)).ShouldNotContain("ALLERG");

            await nurse.PostAsync<AllergyDto>($"/api/v1/emr/patients/{patient.Id}/allergies",
                new RecordAllergyRequest("Penicillin", AllergyType.Drug, "Rash", AllergySeverity.Moderate, null, null));

            // The front office may not read clinical flags, yet the band it prints carries the mark (and only the mark).
            var band = await frontOffice.GetAsync<WristbandDto>(path);
            band.Allergies.ShouldBe(AllergyRecordingStatus.Recorded);
            var commands = await PrintAsync(band);
            commands.ShouldContain("^FDALLERGY^FS");
            commands.ShouldNotContain("NOT RECORDED");
        }
        finally
        {
            await admin.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of wristband printing"));
        }

        using var cancelled = await frontOffice.SendRawAsync(HttpMethod.Get, path);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await cancelled.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("admission.not_in_bed");

        // The band prints as ZPL for the ward's band printer, with the allergy status passed through as the chart gives it.
        async Task<string> PrintAsync(WristbandDto band)
        {
            var commands = await frontOffice.PostAsync<LabelCommands>("/api/v1/integrations/labels/wristbands", new WristbandLabelsRequest(
                new LabelPrinterSetup("zpl", 300, 25, 254),
                [
                    new WristbandLabel(band.Uhid, band.PatientName, $"{band.Age}/{band.Gender.ToString()[0]}", band.AdmissionNumber,
                        band.WardName, band.BedLabel, Enum.Parse<WristbandAllergies>(band.Allergies.ToString())),
                ]));
            commands.Language.ShouldBe("zpl");
            return commands.Content;
        }
    }

    [Fact]
    public async Task Staff_get_printer_commands_for_their_labels()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);

        var commands = await lab.PostAsync<LabelCommands>("/api/v1/integrations/labels/lab-tubes", new LabTubeLabelsRequest(Zebra,
        [
            new LabTubeLabel("LS2600000123", "Kiran Test", "NH00012345", "46Y/M", "LA2600123", true, "EDTA", "HAEM", ["CBC"], DateTimeOffset.UtcNow),
        ]));

        commands.Labels.ShouldBe(1);
        commands.Content.ShouldStartWith("^XA\n");
        commands.Content.ShouldContain("^FD>:LS>52600000123^FS");

        using var unprintable = await lab.SendRawAsync(HttpMethod.Post, "/api/v1/integrations/labels/lab-tubes", new LabTubeLabelsRequest(Zebra,
        [
            new LabTubeLabel("LS2600000123", "रवि शर्मा", "NH00012345", null, null, false, null, null, null, null),
        ]));
        unprintable.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await unprintable.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("labels.text_not_printable");
    }

    [Fact]
    public async Task Only_staff_allowed_to_set_up_printers_change_a_workstations_printers_and_each_change_is_recorded()
    {
        var computer = "OPD-RECEPTION-" + Digits(4);
        var change = new PrinterSetupChange(computer, null,
        [
            new PrinterRoleSetup("labels", "ZDesigner GK420t", "zpl", 203, 50, 25),
            new PrinterRoleSetup("wristbands", "ZDesigner HC100", "zpl", 300, 25, 254, "qr"),
            new PrinterRoleSetup("receipts", "EPSON TM-T82 Receipt", null, null, null, null),
        ]);

        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        foreach (var path in new[] { "/api/v1/integrations/printers/changes/approval", "/api/v1/integrations/printers/changes" })
        {
            using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, path, change);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The approval names who the change is saved as, from the sign-in rather than the page; it records nothing.
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var me = await admin.GetAsync<JsonElement>("/api/v1/auth/me");
        var approval = await admin.PostAsync<PrinterChangeApproval>("/api/v1/integrations/printers/changes/approval", change);
        approval.ChangedBy.ShouldBe(me.GetProperty("displayName").GetString());

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var recorded = db.Set<AuditEvent>().AsNoTracking().Where(e => e.Action == "workstation.printers_changed" && e.EntityId == computer);
        (await recorded.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        // Recorded once HC360 Desktop has saved it, with the wristband's code.
        await admin.PostAsync("/api/v1/integrations/printers/changes", change);

        var entry = (await recorded.ToListAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        entry.UserName.ShouldBe(HospitalServer.Users.HospitalAdmin);
        entry.Summary.ShouldBe(
            $"Changed the printers on {computer}: labels: ZDesigner GK420t; wristbands: ZDesigner HC100 (QR code); receipts: EPSON TM-T82 Receipt");

        using var badCode = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/integrations/printers/changes/approval",
            change with { Roles = [new PrinterRoleSetup("labels", "ZDesigner GK420t", "zpl", 203, 50, 25, "qr")] });
        badCode.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_pharmacy_tag_carries_the_item_code_and_batch_the_counter_scans()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);

        var commands = await pharmacist.PostAsync<LabelCommands>("/api/v1/integrations/labels/pharmacy-tags", new PharmacyTagLabelsRequest(Zebra,
        [
            new PharmacyTagLabel("PCM500#AB1234", "Paracetamol 500 mg tab", "AB1234", new DateOnly(2027, 9, 30), 2.5m, "tab"),
        ], Copies: 2));

        commands.Labels.ShouldBe(1);
        commands.Content.ShouldContain("PCM500#AB1234");
        commands.Content.ShouldContain("MRP Rs 2.50/tab");
        commands.Content.ShouldContain("^PQ2");

        using var unscannable = await pharmacist.SendRawAsync(HttpMethod.Post, "/api/v1/integrations/labels/pharmacy-tags", new PharmacyTagLabelsRequest(Zebra,
        [
            new PharmacyTagLabel("PCM500#AB\\1234", "Paracetamol 500 mg tab", null, null, null),
        ]));
        unscannable.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Only_staff_allowed_to_set_up_printers_change_a_workstations_serial_scanner_and_each_change_is_recorded()
    {
        var computer = "PHARMACY-" + Digits(4);
        var change = new ScannerSetupChange(computer, null, "com3", 19200, 7, "even", "one");

        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        foreach (var path in new[] { "/api/v1/integrations/scanners/changes/approval", "/api/v1/integrations/scanners/changes" })
        {
            using var refused = await pharmacist.SendRawAsync(HttpMethod.Post, path, change);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var me = await admin.GetAsync<JsonElement>("/api/v1/auth/me");
        var approval = await admin.PostAsync<PrinterChangeApproval>("/api/v1/integrations/scanners/changes/approval", change);
        approval.ChangedBy.ShouldBe(me.GetProperty("displayName").GetString());

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var recorded = db.Set<AuditEvent>().AsNoTracking().Where(e => e.Action == "workstation.scanner_changed" && e.EntityId == computer);
        (await recorded.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        await admin.PostAsync("/api/v1/integrations/scanners/changes", change);
        await admin.PostAsync("/api/v1/integrations/scanners/changes", change with { Port = null });

        (await recorded.OrderBy(e => e.OccurredAt).Select(e => e.Summary).ToListAsync(TestContext.Current.CancellationToken)).ShouldBe(
        [
            $"Changed the barcode scanner on {computer}: COM3 at 19200 baud, 7 data bits, even parity, 1 stop bit",
            $"Changed the barcode scanner on {computer}: none (keyboard scanners only)",
        ]);

        foreach (var bad in new[] { change with { Port = "LPT1" }, change with { BaudRate = 9601 }, change with { Parity = "mark" } })
        {
            using var refused = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/integrations/scanners/changes/approval", bad);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
