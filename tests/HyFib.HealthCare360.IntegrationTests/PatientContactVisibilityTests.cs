using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.FollowUps;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.Scheduling.Features.Availability;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS PRV-03, minimum necessary: a patient's contact details (mobile and other phone numbers, email, street address, the
/// emergency contact's phone) go in full only to staff whose role holds <c>patients.contact.read</c>, those who contact or
/// bill patients; everyone else gets them masked. Proved across the whole API rather than screen by screen: every GET
/// endpoint is called with one patient's ids (record, UHID, appointment, visit, follow-up call, bill) by a role holding
/// every other permission, and no answer may carry the details. The same calls by a role that also holds the permission do
/// find them, so the calls reach the patient's data.
/// </summary>
public sealed class PatientContactVisibilityTests(HospitalServer server)
{
    private const string ContactRead = "patients.contact.read";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_answer_gives_a_role_without_the_permission_a_patients_contact_details()
    {
        var patient = await RegisterAsync();
        var paths = Paths(patient);
        paths.Count.ShouldBeGreaterThan(500, "every GET the API maps, the patient's ids filled in");

        var blind = await StaffAsync(withContacts: false);
        var leaks = await CrawlAsync(blind, paths, patient);
        leaks.ShouldBeEmpty($"{paths.Count} GETs by a role holding every permission but {ContactRead}");

        var sighted = await StaffAsync(withContacts: true);
        var shown = await CrawlAsync(sighted, paths, patient);
        TestContext.Current.SendDiagnosticMessage($"{paths.Count} GETs; with the permission, the details came from: {string.Join("; ", shown)}");
        shown.ShouldContain(found => found.StartsWith($"/api/v1/patients/{patient.Id}?", StringComparison.Ordinal),
            "the same GETs by a role that also holds the permission should find the details on the patient's record");
    }

    [Fact]
    public async Task Editing_a_patient_needs_the_permission_and_the_record_says_its_contacts_are_masked()
    {
        var patient = await RegisterAsync();
        var blind = await StaffAsync(withContacts: false);
        var sighted = await StaffAsync(withContacts: true);

        var masked = await blind.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}");
        var full = await sighted.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}");

        masked.ContactsMasked.ShouldBeTrue();
        full.ContactsMasked.ShouldBeFalse();
        masked.Phone.ShouldBe(Phone.Mask(full.Phone));
        masked.Phone.ShouldEndWith(full.Phone![^4..], Case.Sensitive, "the last four digits confirm a number with the patient");
        masked.Email.ShouldBe("p***@example.org");
        masked.Address.ShouldBe(full.Address with { Line1 = null, Line2 = null, PinCode = null });
        masked.Address.City.ShouldBe("Pune");
        masked.EmergencyContactName.ShouldBe("Ramesh");
        masked.EmergencyContactPhone.ShouldBe(Phone.Mask(full.EmergencyContactPhone));

        // Saving the masked details back would replace the real ones: without the permission, editing is refused.
        var edit = new UpdatePatientRequest(
            full.Title, full.FirstName, full.MiddleName, full.LastName, full.Gender, full.DateOfBirth, null, null, masked.Phone, masked.AlternatePhone,
            masked.Email, masked.Address, full.BloodGroup, full.MaritalStatus, full.GuardianName, full.GuardianRelation, full.EmergencyContactName,
            masked.EmergencyContactPhone, full.EmergencyContactRelation, full.Occupation, full.Religion, full.Nationality, full.PreferredLanguage,
            full.Category, null, full.AbhaAddress, null, full.PhotoFileId, full.IsVip, full.IsDeceased, full.DeceasedAt, "Called about the bill",
            full.IsActive, full.Version);
        using (var refused = await blind.SendRawAsync(HttpMethod.Put, $"/api/v1/patients/{patient.Id}", edit))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await sighted.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).ShouldBe(full);
        await sighted.PutAsync($"/api/v1/patients/{patient.Id}", edit with
        {
            Phone = full.Phone, AlternatePhone = full.AlternatePhone, Email = full.Email, Address = full.Address,
            EmergencyContactPhone = full.EmergencyContactPhone,
        });
        (await sighted.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).Notes.ShouldBe("Called about the bill");
    }

    /// <summary>
    /// A patient of this test alone, with every contact detail, an appointment, a visit, a CRM follow-up call, and a bill drafted
    /// from the visit if it has charges.
    /// </summary>
    private async Task<Seeded> RegisterAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var token = Letters(10);
        var phone = "9" + Digits(9);
        var alternate = "8" + Digits(9);
        var emergency = "7" + Digits(9);
        var email = $"prv03.{token.ToLowerInvariant()}@example.org";
        var street = $"Plot {token} Lane";
        var pin = "4" + Digits(5);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: TestPatients.FirstName("Lata"), MiddleName: null, LastName: "Contact" + token, Gender: Gender.Female,
            DateOfBirth: new DateOnly(1985, 3, 14), AgeYears: null, AgeMonths: null, Phone: phone, AlternatePhone: alternate, Email: email,
            Address: new Address { Line1 = street, Line2 = "Near the water tank", City = "Pune", State = "Maharashtra", PinCode = pin },
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: "Ramesh",
            EmergencyContactPhone: emergency, EmergencyContactRelation: "Brother", Occupation: null, Religion: null, Nationality: null,
            PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null,
            IsVip: false, Notes: null, ConfirmedNotDuplicate: true));

        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            created.Id, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Cough for a week",
            false, null, null, TokenPriority.Normal, IssueToken: true, NotifyPatient: false));

        var today = server.Services.GetRequiredService<TimeProvider>().Today();
        var availability = await frontOffice.GetAsync<List<PractitionerAvailability>>(
            $"/api/v1/scheduling/slots?practitionerId={practitionerId}&from={today:yyyy-MM-dd}&to={today.AddDays(3):yyyy-MM-dd}&mode=InPerson&includeFull=false");
        var slot = availability.SelectMany(a => a.Slots).Where(s => s.Available > 0 && s.StartsAt > DateTimeOffset.UtcNow.AddMinutes(2)).MinBy(s => s.StartsAt)
                   ?? throw new InvalidOperationException("The doctor has no free slot in the next four days.");
        var appointment = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            created.Id, practitionerId, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.FollowUp, IsTelehealth: false, "Review of the cough",
            Notes: null, NotifyPatient: false));

        var followUpId = await (await server.SignInAsync(HospitalServer.Users.Admin)).PostAsync<Guid>("/api/v1/crm/follow-ups",
            new CreateFollowUpRequest(created.Id, visit.Id, FollowUpType.PostConsultation, today.AddDays(3), null, FollowUpPriority.Normal, "Ask about the cough"));

        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        using var drafted = await billing.SendRawAsync(HttpMethod.Post, "/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
        Guid? billId = drafted.IsSuccessStatusCode
            ? (await drafted.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("id").GetGuid()
            : null;

        return new Seeded(created.Id, created.Uhid, visit.Id, appointment.Id, followUpId, billId,
        [
            ("mobile", phone),
            ("other phone", alternate),
            ("emergency contact's phone", emergency),
            ("email", email),
            ("street", street),
        ]);
    }

    /// <summary>A member of staff whose role holds every permission but the administrative ones, and the contacts' only if asked.</summary>
    private async Task<ApiClient> StaffAsync(bool withContacts)
    {
        var permissions = server.Services.GetRequiredService<PermissionRegistry>().All.Select(p => p.Name)
            .Where(p => !PlatformPermissions.IsAdministrative(p) && p != PatientsPermissions.Portal && (withContacts || p != ContactRead))
            .ToList();
        var suffix = Letters(8).ToLowerInvariant();
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var role = await admin.PostAsync<RoleDetail>("/api/v1/admin/roles", new SaveRoleRequest(
            $"IT {(withContacts ? "with" : "without")} contacts {suffix}", "Every permission but administration", permissions, Version: null));
        var userName = "it-prv03-" + suffix;
        await server.CreateStaffAccountAsync(userName, role.Name);
        return await server.SignInAsync(userName);
    }

    /// <summary>
    /// Every GET the API maps (the patient portal's, which serve the patients themselves, aside), its route values filled
    /// with the patient's ids, and the list filters set to the patient.
    /// </summary>
    private List<string> Paths(Seeded patient)
    {
        var query = $"?search={patient.Uhid}&patientId={patient.Id}&visitId={patient.VisitId}&page=1&pageSize=100";
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in server.Endpoints)
        {
            var raw = endpoint.RoutePattern.RawText ?? string.Empty;
            if (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") != true
                || !(raw.StartsWith("/api/v1/", StringComparison.Ordinal) || raw.StartsWith("/fhir", StringComparison.Ordinal))
                || raw.Split('/').Contains("portal", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var path in Fill(endpoint.RoutePattern, patient))
            {
                paths.Add(path + query);
            }
        }

        return [.. paths];
    }

    /// <summary>The paths a route pattern takes with the patient's values; none for a pattern with a catch-all.</summary>
    private static IEnumerable<string> Fill(RoutePattern pattern, Seeded patient)
    {
        IEnumerable<string> paths = [string.Empty];
        string? previous = null;
        foreach (var segment in pattern.PathSegments)
        {
            IEnumerable<string> texts = [string.Empty];
            foreach (var part in segment.Parts)
            {
                IEnumerable<string> values = part switch
                {
                    RoutePatternLiteralPart literal => [literal.Content],
                    RoutePatternSeparatorPart separator => [separator.Content],
                    RoutePatternParameterPart { IsCatchAll: true } => [],
                    RoutePatternParameterPart parameter => Values(parameter, previous, patient),
                    _ => [],
                };
                texts = [.. texts.SelectMany(text => values.Select(value => text + value))];
            }

            paths = [.. paths.SelectMany(path => texts.Select(text => path + "/" + text))];
            previous = segment.Parts is [RoutePatternLiteralPart only] ? only.Content : previous;
        }

        return paths;
    }

    private static IEnumerable<string> Values(RoutePatternParameterPart parameter, string? previous, Seeded patient)
    {
        var name = parameter.Name.ToLowerInvariant();
        List<string> ids =
        [
            patient.Id.ToString(), patient.VisitId.ToString(), patient.AppointmentId.ToString(), patient.FollowUpId.ToString(),
            .. patient.BillId is { } bill ? [bill.ToString()] : Array.Empty<string>(),
        ];
        if (name.Contains("uhid", StringComparison.Ordinal))
        {
            return [patient.Uhid];
        }

        if (name.Contains("patient", StringComparison.Ordinal) || (name == "id" && previous == "patients"))
        {
            return [patient.Id.ToString()];
        }

        if (name.Contains("visit", StringComparison.Ordinal) || (name == "id" && previous == "visits"))
        {
            return [patient.VisitId.ToString()];
        }

        if (name == "id" && previous == "follow-ups")
        {
            return [patient.FollowUpId.ToString()];
        }

        if (name.Contains("appointment", StringComparison.Ordinal) || (name == "id" && previous == "appointments"))
        {
            return [patient.AppointmentId.ToString()];
        }

        if ((name.Contains("bill", StringComparison.Ordinal) || (name == "id" && previous == "bills")) && patient.BillId is { } billId)
        {
            return [billId.ToString()];
        }

        var policies = parameter.ParameterPolicies.Select(p => p.Content ?? string.Empty).ToList();
        return policies.Contains("int") || policies.Contains("long") ? ["1"]
            : policies.Contains("guid") || name.EndsWith("id", StringComparison.Ordinal) ? ids
            : [patient.Uhid];
    }

    /// <summary>Each path and contact detail an answer carried in full ("path detail"), whatever its status.</summary>
    private static async Task<List<string>> CrawlAsync(ApiClient client, List<string> paths, Seeded patient)
    {
        var found = new List<string>();
        foreach (var path in paths)
        {
            var body = await GetAsync(client, path);
            var squeezed = Squeeze(body);
            found.AddRange(patient.Details
                .Where(detail => squeezed.Contains(Squeeze(detail.Value), StringComparison.OrdinalIgnoreCase))
                .Select(detail => $"{path} {detail.Name}"));
        }

        return found;
    }

    /// <summary>The answer's body; a GET that does not finish in 15 seconds counts as answering nothing.</summary>
    private static async Task<string> GetAsync(ApiClient client, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var response = await client.Http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 10)
                {
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5), Ct);
                    continue;
                }

                return await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>The text without spaces and the marks numbers are written with, so "+91 98765 43210" is found as well.</summary>
    private static string Squeeze(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && c is not '-' and not '(' and not ')' and not '+')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string Letters(int count) => new([.. Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int count) => new([.. Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10)))]);

    private sealed record Seeded(
        Guid Id, string Uhid, Guid VisitId, Guid AppointmentId, Guid FollowUpId, Guid? BillId, IReadOnlyList<(string Name, string Value)> Details);
}
