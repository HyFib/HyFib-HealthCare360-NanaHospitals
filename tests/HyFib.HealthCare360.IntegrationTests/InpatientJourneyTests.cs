using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Features.Rounds;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using CodingEntryKind = HyFib.HealthCare360.Modules.Clinical.Domain.CodingEntryKind;
using CodingStatus = HyFib.HealthCare360.Modules.Clinical.Domain.CodingStatus;
using DischargeSummaryStatus = HyFib.HealthCare360.Modules.Clinical.Domain.DischargeSummaryStatus;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The in-patient journey that SRS §9 names as a release condition: admit → bed → charges → discharge bill → insurance
/// claim. An insured patient with cellulitis of the leg stays two nights. Every step goes through the real HTTP API and is
/// done by the member of staff who does it in the hospital: the front office, the billing counter, the insurance (TPA)
/// desk, the doctor, the ward nurse and medical records.
/// </summary>
public sealed class InpatientJourneyTests(HospitalServer server)
{
    private const decimal CoPayPercent = 10m;
    private const decimal PreAuthRequested = 25_000m;
    private const decimal PreAuthApproved = 20_000m;
    private const string Diagnosis = "L03.1 Cellulitis of other parts of limb (left leg)";

    /// <summary>The diagnosis as medical records codes it, with the title of the ICD-10 catalogue.</summary>
    private const string CodedDiagnosis = "L03.1 Cellulitis of other parts of limb";

    [Fact]
    public async Task An_insured_patient_is_admitted_charged_for_the_stay_discharged_and_the_insurer_settles_the_claim()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var coder = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var secondCoder = await server.SignInAsync(HospitalServer.Users.MrdVerifier);

        // The demonstration seed has no insurance-desk account. The hospital administrator holds the TPA desk's
        // permissions (pre-authorisations and claims) and plays that part.
        var tpaDesk = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        // The stay began at 10:00 (IST) the day before yesterday and ends today. The admission is recorded as a late entry,
        // which the API allows up to three days back, so the stay really crosses two midnights.
        var today = await HospitalDayAsync();
        var admittedAt = IndiaClock.AtLocal(today.AddDays(-2), new TimeOnly(10, 0));
        DateOnly[] nights = [today.AddDays(-2), today.AddDays(-1)];

        // ------------------------------------------------------------------ admission

        // Front office: registers the patient.
        var patient = await RegisterPatientAsync(frontOffice);

        // Billing counter: records his Star Health policy (Medi Assist TPA, 10 % co-pay) and verifies it with the TPA. The
        // counter holds the insurance desk's policy permission; the front office does not.
        var payers = await billing.GetAsync<List<PayerListItem>>("/api/v1/insurance/payers");
        var insurer = payers.Single(p => p.Code == "STAR");
        var tpa = payers.Single(p => p.Code == "MEDIASSIST");
        var policyNumber = "SH/" + UniqueDigits(10);
        var policy = await RecordPolicyAsync(billing, patient, insurer, tpa, policyNumber, today);

        // Front office: admits him as a cashless patient under the doctor, in a free bed of the male general ward.
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var wards = await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards");
        var generalWard = wards.Single(w => w.Code == "GWM");
        var firstBedId = await FreeBedAsync(frontOffice, patient.Id, generalWard.Id);
        var admission = await AdmitAsync(frontOffice, patient.Id, firstBedId, consultantId, insurer.Id, policyNumber, admittedAt, today);

        // Bed: the bed board shows the bed occupied by him.
        var firstBed = await BedOnBoardAsync(frontOffice, generalWard.Id, firstBedId);
        firstBed.Status.ShouldBe(BedStatus.Occupied, "the admission occupies the bed");
        firstBed.Occupant.ShouldNotBeNull().AdmissionId.ShouldBe(admission.Id);
        firstBed.Occupant.Uhid.ShouldBe(patient.Uhid);
        firstBed.Occupant.PayerType.ShouldBe(PayerType.Insurance);

        // Billing: the admission opened his in-patient account with the insurer, his policy and the general-ward bed class.
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        account.Status.ShouldBe(IpAccountStatus.Open);
        account.PayerId.ShouldBe(insurer.Id);
        account.PolicyId.ShouldBe(policy.Id, "the account picks up the policy valid on the admission date");
        account.BedServiceCode.ShouldBe("BED-GEN");

        // Patient banner (UX-04): the admission's visit is open, so its insurer is the payer. The billing counter, which
        // reads payers and policies, sees the insurer's name and the in-date policy; the doctor sees the payer type only.
        var bannerPayer = (await billing.GetAsync<PatientPayerResponse>($"/api/v1/patients/{patient.Id}/payer")).Current.ShouldNotBeNull();
        bannerPayer.IsCurrentVisit.ShouldBeTrue();
        bannerPayer.PayerType.ShouldBe(PayerType.Insurance);
        bannerPayer.PayerId.ShouldBe(insurer.Id);
        bannerPayer.PayerName.ShouldBe(insurer.Name);
        bannerPayer.PolicyNumber.ShouldBe(policy.PolicyNumberMasked);
        bannerPayer.ValidUntil.ShouldBe(policy.ValidTo);
        var doctorsBanner = (await doctor.GetAsync<PatientPayerResponse>($"/api/v1/patients/{patient.Id}/payer")).Current.ShouldNotBeNull();
        (doctorsBanner.PayerType, doctorsBanner.PayerName, doctorsBanner.PolicyNumber, doctorsBanner.ValidUntil).ShouldBe((PayerType.Insurance, null, null, null));

        // TPA desk: requests cashless pre-authorisation; the insurer approves part of the estimate.
        var preAuth = await ObtainPreAuthorisationAsync(tpaDesk, policy.Id, admission.Id);
        preAuth.Status.ShouldBe(PreAuthStatus.PartiallyApproved);
        preAuth.EffectiveApprovedAmount.ShouldBe(PreAuthApproved);
        preAuth.TpaId.ShouldBe(tpa.Id);

        // ------------------------------------------------------------------ the stay (today, day 3)

        // Nurse: at the family's request, moves him to a semi-private bed.
        var semiPrivateWard = wards.Single(w => w.Code == "SPW");
        var secondBedId = await FreeBedAsync(nurse, patient.Id, semiPrivateWard.Id);
        await nurse.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/transfer",
            new TransferPatientRequest(secondBedId, "Moved to a semi-private room at the family's request", null, null, null));

        var secondBed = await BedOnBoardAsync(nurse, semiPrivateWard.Id, secondBedId);
        secondBed.Status.ShouldBe(BedStatus.Occupied);
        secondBed.Occupant.ShouldNotBeNull().AdmissionId.ShouldBe(admission.Id);
        var vacated = await BedOnBoardAsync(nurse, generalWard.Id, firstBedId);
        vacated.Status.ShouldBe(BedStatus.Cleaning, "a bed left by a transfer goes for cleaning before the next patient");
        vacated.Occupant.ShouldBeNull();

        var bedHistory = await nurse.GetAsync<List<BedAssignmentDto>>($"/api/v1/inpatient/admissions/{admission.Id}/bed-history");
        bedHistory.Select(s => (s.BedId, s.Reason)).ToArray()
            .ShouldBe([(firstBedId, BedAssignmentReason.Admission), (secondBedId, BedAssignmentReason.Transfer)]);
        bedHistory[0].FromAt.ShouldBe(admittedAt);
        bedHistory[0].ToAt.ShouldNotBeNull();
        bedHistory[1].ToAt.ShouldBeNull();
        (await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/{account.Id}")).BedServiceCode
            .ShouldBe("BED-SEMI", "billing follows the transfer to price later services by the new room category");

        // Doctor: his ward round lists him in the new bed, on day 3 of the stay, not yet seen today.
        var onRound = (await doctor.GetAsync<WardRound>("/api/v1/inpatient/rounds")).Patients.Single(p => p.AdmissionId == admission.Id);
        onRound.WardName.ShouldBe(semiPrivateWard.Name);
        onRound.IsPrimaryConsultant.ShouldBeTrue();
        onRound.DayOfStay.ShouldBe(3);
        onRound.ProvisionalDiagnosis.ShouldBe("Cellulitis of the left leg with fever");
        onRound.SeenToday.ShouldBeFalse();
        onRound.LastVisitAt.ShouldBeNull();

        // Doctor: sees him on the ward round (a consultant-visit charge) and orders a dressing of the leg (a procedure,
        // which Billing prices when it is ordered).
        var visit = await doctor.PostAsync<ConsultantVisitDto>($"/api/v1/inpatient/admissions/{admission.Id}/consultant-visits",
            new RecordConsultantVisitRequest(null, null, "Swelling and redness settling, afebrile for 24 hours. Dressing today; fit for discharge."));
        visit.PractitionerId.ShouldBe(consultantId);
        visit.ServiceCode.ShouldBe("VISIT-DR");
        visit.IsCharged.ShouldBeTrue(visit.ChargeError);
        (await doctor.GetAsync<WardRound>("/api/v1/inpatient/rounds")).Patients.Single(p => p.AdmissionId == admission.Id)
            .SeenToday.ShouldBeTrue("the recorded visit shows on the round");

        // The billing counter can see admissions, but not the round's clinical picture.
        using (var refused = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/inpatient/rounds"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var dressing = await ServiceAsync(doctor, "PROC-DRESS");
        var order = await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patient.Id, null, admission.VisitId, admission.Id, OrderPriority.Routine, "Dressing of the left leg before discharge",
            [new OrderLineRequest(dressing.Id, null)], null, false));
        order.AdmissionId.ShouldBe(admission.Id);
        order.Items.ShouldHaveSingleItem().ServiceCode.ShouldBe("PROC-DRESS");

        // Billing: both services are on the running bill, unbilled, with the insurer's cover estimated.
        var running = await billing.GetAsync<RunningBill>($"/api/v1/billing/ip-accounts/{account.Id}/running-bill");
        running.Lines.Where(l => l.ServiceCode is "VISIT-DR" or "PROC-DRESS").Select(l => (l.ServiceCode, l.Status)).Order().ToArray()
            .ShouldBe([("PROC-DRESS", ChargeStatus.Unbilled), ("VISIT-DR", ChargeStatus.Unbilled)]);
        running.Insurance.ShouldNotBeNull().PayerId.ShouldBe(insurer.Id);
        running.Insurance.CoPayPercent.ShouldBe(CoPayPercent);
        running.Insurance.CoverageLimit.ShouldBe(PreAuthApproved, "an insurer covers at most what it approved");

        // ------------------------------------------------------------------ discharge

        // Doctor: writes the discharge summary from the admission's record and signs it.
        var summary = await WriteDischargeSummaryAsync(doctor, patient.Id, admission.Id, admittedAt);
        summary.Status.ShouldBe(DischargeSummaryStatus.Signed);
        summary.SignedByPractitionerId.ShouldBe(consultantId);

        // Nurse: initiates the discharge. Every bed day due so far is charged first.
        var initiated = await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/initiate",
            new InitiateDischargeRequest(today, "Discharge advised on the ward round"));
        initiated.Status.ShouldBe(AdmissionStatus.DischargeInitiated);

        // Charges: midnight census (the hospital's default bed-day rule) charges a bed day and a nursing day for every
        // midnight spent in a bed: the day before yesterday and yesterday. Both midnights were spent in the general ward,
        // so both days are charged at its class although he leaves from a semi-private bed.
        var dayCharges = await nurse.GetAsync<List<DailyChargeDto>>($"/api/v1/inpatient/admissions/{admission.Id}/charges");
        dayCharges.Where(c => c.Kind == DailyChargeKind.Bed).Select(c => (c.ChargeDate, c.ServiceCode)).ToArray()
            .ShouldBe([(nights[0], "BED-GEN"), (nights[1], "BED-GEN")]);
        dayCharges.Where(c => c.Kind == DailyChargeKind.Nursing).Select(c => (c.ChargeDate, c.ServiceCode)).ToArray()
            .ShouldBe([(nights[0], "NURS-DAY"), (nights[1], "NURS-DAY")]);

        // The stay at the admission's tariff (the payer's tariff plan, else the catalogue price): bed days × bed tariff
        // plus nursing days, the doctor's visit and the dressing.
        var tariff = await TariffAsync(billing, admission.Id);
        var total = nights.Length * (tariff.BedDay + tariff.NursingDay) + tariff.DoctorVisit + tariff.Dressing;

        // The insurer pays what its rules allow: everything payable less the policy's co-pay, up to the approved amount.
        // The patient pays the rest, rounded to the rupee.
        var coPay = Math.Round(total * CoPayPercent / 100m, 2, MidpointRounding.AwayFromZero);
        var insurerShare = Math.Min(total - coPay, PreAuthApproved);
        var patientShare = Math.Round(total - insurerShare, 0, MidpointRounding.AwayFromZero);

        // Billing: discharge initiation drafted the final bill (IPB-07), and it already carries every charge of the stay,
        // the bed and nursing days that the initiation charged included.
        var drafts = await billing.GetAsync<PagedResult<BillListItem>>($"/api/v1/billing/bills?admissionId={admission.Id}&status=Draft");
        var draft = drafts.Items.ShouldHaveSingleItem();
        draft.IsFinalBill.ShouldBeTrue();
        draft.NetAmount.ShouldBe(insurerShare + patientShare, "the final bill drafted at discharge initiation must include the bed days it charged");

        // Billing: prepares the final bill (the refresh the IP desk runs before finalizing) and checks it line by line...
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/final-bill");
        bill.Id.ShouldBe(draft.Id, "the final bill is the draft prepared at discharge initiation");
        bill.Type.ShouldBe(BillType.InPatient);
        bill.IsFinalBill.ShouldBeTrue();
        bill.Lines.Count.ShouldBe(2 * nights.Length + 2, "bed and nursing days, the doctor's visit and the dressing");
        AssertDayLines(bill, "BED-GEN", nights, tariff.BedDay);
        AssertDayLines(bill, "NURS-DAY", nights, tariff.NursingDay);
        Lines(bill, "VISIT-DR").ShouldHaveSingleItem().NetAmount.ShouldBe(tariff.DoctorVisit);
        Lines(bill, "PROC-DRESS").ShouldHaveSingleItem().NetAmount.ShouldBe(tariff.Dressing);
        bill.Lines.ShouldAllBe(l => l.Quantity == 1 && l.IsPayerPayable && !l.IsReturn);
        bill.GrossAmount.ShouldBe(total);
        bill.DiscountAmount.ShouldBe(0m);
        bill.TaxAmount.ShouldBe(0m, "in-patient healthcare services are exempt from GST");

        // ...and the payer split: the insurer's share under the approved pre-authorisation, and the patient's co-pay.
        bill.PayerId.ShouldBe(insurer.Id);
        bill.PolicyId.ShouldBe(policy.Id);
        bill.PreAuthorizationId.ShouldBe(preAuth.Id);
        bill.PayerCoverageLimit.ShouldBe(PreAuthApproved);
        bill.CoPayPercent.ShouldBe(CoPayPercent);
        bill.CoPayAmount.ShouldBe(coPay);
        bill.NonPayableAmount.ShouldBe(0m, "nothing on this bill is an insurer non-payable");
        bill.PayerAmount.ShouldBe(insurerShare);
        bill.PatientAmount.ShouldBe(patientShare);
        bill.NetAmount.ShouldBe(insurerShare + patientShare);

        // Billing: finalizes the bill; only the patient's share is due at the counter.
        bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}/finalize", new BillVersionRequest(bill.Version));
        bill.Status.ShouldBe(BillStatus.Finalized);
        bill.BillNumber.ShouldNotBeNullOrWhiteSpace();
        bill.DueAmount.ShouldBe(patientShare);

        // Nurse: the ward cannot discharge him until the co-pay is paid.
        var pending = await nurse.GetAsync<DischargeClearanceDto>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/clearance");
        pending.Cleared.ShouldBeFalse();
        pending.Outstanding.ShouldBe(patientShare);

        // Billing counter: collects the co-pay in cash.
        var receipt = await CollectAtCounterAsync(billing, bill.Id, patientShare);
        receipt.Purpose.ShouldBe(ReceiptPurpose.BillPayment);
        receipt.Amount.ShouldBe(patientShare);

        // Nurse: billing has cleared him; she completes the discharge and the bed is released for cleaning.
        (await nurse.GetAsync<DischargeClearanceDto>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/clearance")).Cleared.ShouldBeTrue();
        var discharged = await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete",
            new CompleteDischargeRequest(DischargeType.Normal, null, "Discharged home in a stable condition"));
        discharged.Status.ShouldBe(AdmissionStatus.Discharged);

        var released = await BedOnBoardAsync(nurse, semiPrivateWard.Id, secondBedId);
        released.Status.ShouldBe(BedStatus.Cleaning, "the discharge releases the bed to housekeeping");
        released.Occupant.ShouldBeNull();

        var stay = await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admission.Id}");
        stay.Status.ShouldBe(AdmissionStatus.Discharged);
        stay.DischargeType.ShouldBe(DischargeType.Normal);
        stay.LengthOfStayDays.ShouldBe(nights.Length);
        (await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/{account.Id}")).Status
            .ShouldBe(IpAccountStatus.Closed, "the account closes once the patient has paid his share of the final bill");

        // Nurse (housekeeping): cleans the bed, which is free again.
        await CleanBedAsync(nurse, semiPrivateWard.Id, secondBedId, admission.Id);
        (await BedOnBoardAsync(nurse, semiPrivateWard.Id, secondBedId)).Status.ShouldBe(BedStatus.Vacant);

        // ------------------------------------------------------------------ insurance claim

        // TPA desk: claims the insurer's share of the final bill. Medical records has not coded the stay yet, so the coding
        // comes from the pre-authorisation.
        var claimRequest = new CreateClaimRequest(bill.Id, null, null, null, null, null);
        var claimId = (await tpaDesk.PostAsync<JsonElement>("/api/v1/insurance/claims", claimRequest)).GetProperty("id").GetGuid();
        var claim = await tpaDesk.GetAsync<ClaimDetail>($"/api/v1/insurance/claims/{claimId}");
        claim.BillId.ShouldBe(bill.Id);
        claim.BillNumber.ShouldBe(bill.BillNumber);
        claim.PatientId.ShouldBe(patient.Id);
        claim.PayerId.ShouldBe(insurer.Id);
        claim.TpaId.ShouldBe(tpa.Id);
        claim.PolicyId.ShouldBe(policy.Id);
        claim.PreAuthorizationId.ShouldBe(preAuth.Id);
        claim.DiagnosisCodes.ShouldBe(Diagnosis);
        claim.VerifiedCoding.ShouldBeNull("medical records has not coded the stay");
        claim.ClaimedAmount.ShouldBe(insurerShare, "the claim is for the insurer's share, never the patient's co-pay");
        claim.Status.ShouldBe(ClaimStatus.Submitted);

        // Medical records: a coder codes the stay from the discharge summary. The claim offers the coding only once a
        // second coder has verified it.
        var coding = await CodeStayAsync(coder, admission.Id);
        (await tpaDesk.GetAsync<ClaimDetail>($"/api/v1/insurance/claims/{claimId}")).VerifiedCoding
            .ShouldBeNull("a second coder has not verified the coding yet");
        coding = await secondCoder.PostAsync<CodingDto>(
            $"/api/v1/mrd/codings/{coding.Id}/verify", new VerifyCodingRequest(coding.Version));
        coding.Status.ShouldBe(CodingStatus.Verified);

        // TPA desk: replaces the pre-authorisation's provisional codes on the claim with the verified coding.
        claim = await tpaDesk.GetAsync<ClaimDetail>($"/api/v1/insurance/claims/{claimId}");
        var verified = claim.VerifiedCoding.ShouldNotBeNull();
        verified.DiagnosisCodes.ShouldBe(CodedDiagnosis);
        verified.ProcedureCodes.ShouldBeNull("the coder coded no procedure");
        await tpaDesk.PutAsync($"/api/v1/insurance/claims/{claimId}/coding",
            new UpdateClaimCodingRequest(verified.DiagnosisCodes, verified.ProcedureCodes, null, null, claim.Version));
        (await tpaDesk.GetAsync<ClaimDetail>($"/api/v1/insurance/claims/{claimId}")).DiagnosisCodes.ShouldBe(CodedDiagnosis);

        // TPA desk: records the insurer's approval, then its settlement by bank transfer.
        await tpaDesk.PostAsync($"/api/v1/insurance/claims/{claimId}/approve", new ApproveClaimRequest(insurerShare));
        (await tpaDesk.GetAsync<ClaimDetail>($"/api/v1/insurance/claims/{claimId}")).Status.ShouldBe(ClaimStatus.Approved);

        var utr = "UTR" + UniqueDigits(12);
        await tpaDesk.PostAsync($"/api/v1/insurance/claims/{claimId}/settle",
            new SettleClaimRequest(insurerShare, null, utr, "Medi Assist settlement", today));
        claim = await tpaDesk.GetAsync<ClaimDetail>($"/api/v1/insurance/claims/{claimId}");
        claim.Status.ShouldBe(ClaimStatus.Settled);
        claim.SettledAmount.ShouldBe(insurerShare);
        claim.DeductionAmount.ShouldBe(0m);
        claim.Outstanding.ShouldBe(0m);
        claim.UtrNumber.ShouldBe(utr);
        claim.Settlements.ShouldHaveSingleItem().ReceiptId.ShouldNotBeNull();

        // Billing: the bill is settled on both sides: the patient paid the co-pay and the insurer its share.
        bill = await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");
        bill.Status.ShouldBe(BillStatus.Paid);
        bill.PaidAmount.ShouldBe(patientShare);
        bill.DueAmount.ShouldBe(0m, "the patient owes nothing more");
        bill.PayerSettledAmount.ShouldBe(insurerShare);
        bill.PayerDisallowedAmount.ShouldBe(0m);
        (bill.PayerAmount - bill.PayerSettledAmount - bill.PayerDisallowedAmount).ShouldBe(0m, "the insurer owes nothing more");
        bill.Receipts.Select(r => (r.Purpose, r.Amount)).Order().ToArray()
            .ShouldBe([(ReceiptPurpose.BillPayment, patientShare), (ReceiptPurpose.PayerSettlement, insurerShare)]);
    }

    // ---------------------------------------------------------------------- steps

    /// <summary>Front office: registers a new patient with a name and phone number no other test uses.</summary>
    private static async Task<PatientCreated> RegisterPatientAsync(ApiClient frontOffice)
    {
        var surname = UniqueLetters(8);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Ravi", MiddleName: null, LastName: char.ToUpperInvariant(surname[0]) + surname[1..], Gender: Gender.Male,
            DateOfBirth: null, AgeYears: 52, AgeMonths: null, Phone: "9" + UniqueDigits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        patient.Uhid.ShouldNotBeNullOrWhiteSpace();
        return patient;
    }

    /// <summary>Records the patient's policy with its member id and co-pay, and marks it verified with the TPA.</summary>
    private static async Task<PolicyDto> RecordPolicyAsync(
        ApiClient desk, PatientCreated patient, PayerListItem insurer, PayerListItem tpa, string policyNumber, DateOnly today)
    {
        var created = await desk.PostAsync<PolicyDto>("/api/v1/insurance/policies", new SavePolicyRequest(
            PatientId: patient.Id, PayerId: insurer.Id, TpaId: tpa.Id, PolicyNumber: policyNumber, MemberId: "MA" + UniqueDigits(8),
            PolicyHolderName: null, RelationToHolder: "Self", ValidFrom: today.AddMonths(-6), ValidTo: today.AddMonths(6), SumInsured: 500_000m,
            CoPayPercent: CoPayPercent, IsActive: true, CardFileId: null, Notes: null, Version: null));
        await desk.PostAsync($"/api/v1/insurance/policies/{created.Id}/verify", new VerifyPolicyRequest("Eligibility confirmed on the TPA portal"));

        var policy = await desk.GetAsync<PolicyDto>($"/api/v1/insurance/policies/{created.Id}");
        policy.PayerId.ShouldBe(insurer.Id);
        policy.TpaId.ShouldBe(tpa.Id);
        policy.MemberId.ShouldNotBeNullOrWhiteSpace();
        policy.PolicyNumberMasked.ShouldNotBe(policyNumber, "policy numbers are shown masked");
        policy.IsValidToday.ShouldBeTrue();
        policy.VerifiedAt.ShouldNotBeNull();
        return policy;
    }

    /// <summary>Front office: a cashless admission (insurer and policy number) recorded as a late entry.</summary>
    private static async Task<AdmissionCreated> AdmitAsync(
        ApiClient frontOffice, Guid patientId, Guid bedId, Guid consultantId, Guid insurerId, string policyNumber, DateTimeOffset admittedAt, DateOnly today)
    {
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null, DepartmentId: null,
            AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId, ProvisionalDiagnosis: "Cellulitis of the left leg with fever",
            ReferredBy: null, PayerType: PayerType.Insurance, PayerId: insurerId, PolicyNumber: policyNumber, IsMlc: false, MlcNumber: null,
            ExpectedDischargeDate: today, AttendantName: "Lakshmi", AttendantRelation: "Wife", AttendantPhone: null, AdmittedAt: admittedAt,
            Notes: "Late entry of the admission"));
        admission.BedId.ShouldBe(bedId);
        return admission;
    }

    /// <summary>
    /// Medical records: finds the coding record the discharge opened on the coding worklist, and codes the diagnosis the
    /// discharge summary suggests as the primary diagnosis.
    /// </summary>
    private static async Task<CodingDto> CodeStayAsync(ApiClient coder, Guid admissionId)
    {
        var worklist = await coder.GetAsync<PagedResult<CodingDto>>("/api/v1/mrd/codings?status=Pending&pageSize=200");
        var coding = worklist.Items.Single(c => c.AdmissionId == admissionId);
        var suggestions = await coder.GetAsync<List<CodingSuggestion>>($"/api/v1/mrd/codings/{coding.Id}/suggestions");
        suggestions.ShouldContain(s => s.Code == "L03.1" && s.Source == "Discharge summary");

        coding = await coder.PutAsync<CodingDto>($"/api/v1/mrd/codings/{coding.Id}",
            new SaveCodingRequest([new CodingLineRequest(CodingEntryKind.Diagnosis, "L03.1", true)], null, coding.Version));
        coding.Status.ShouldBe(CodingStatus.Coded);
        return coding;
    }

    /// <summary>TPA desk: raises the cashless request, submits it, and records the insurer's approval.</summary>
    private static async Task<PreAuthDetail> ObtainPreAuthorisationAsync(ApiClient tpaDesk, Guid policyId, Guid admissionId)
    {
        var request = new SavePreAuthRequest(
            policyId, admissionId, null, PreAuthRequested, Diagnosis, null,
            "Cellulitis of the left leg with fever; IV antibiotics, limb elevation and daily dressings.", 3, null, null);
        var id = (await tpaDesk.PostAsync<JsonElement>("/api/v1/insurance/preauths", request)).GetProperty("id").GetGuid();
        await tpaDesk.PostAsync($"/api/v1/insurance/preauths/{id}/submit");
        await tpaDesk.PostAsync($"/api/v1/insurance/preauths/{id}/approve", new ApprovePreAuthRequest(PreAuthApproved, "MA-PA-" + UniqueDigits(6)));
        return await tpaDesk.GetAsync<PreAuthDetail>($"/api/v1/insurance/preauths/{id}");
    }

    /// <summary>Doctor: drafts the discharge summary from the prefill (the dressing ordered is already listed) and signs it.</summary>
    private static async Task<DischargeSummaryDto> WriteDischargeSummaryAsync(ApiClient doctor, Guid patientId, Guid admissionId, DateTimeOffset admittedAt)
    {
        var path = $"/api/v1/clinical/discharge-summaries/by-admission/{admissionId}";
        var prefill = await doctor.GetAsync<DischargeSummaryPrefill>($"{path}/prefill?patientId={patientId}");
        prefill.Procedures.ShouldNotBeNull().ShouldContain("Dressing");

        var draft = await doctor.PutAsync<DischargeSummaryDto>(path, new SaveDischargeSummaryRequest(
            PatientId: patientId, EncounterId: null, PractitionerId: null, AdmittedAt: admittedAt, DischargedAt: null,
            Diagnoses: "Cellulitis of the left leg", DiagnosisCodes: ["L03.1"],
            PresentingComplaints: "Painful swelling and redness of the left leg with fever for 3 days",
            CourseInHospital: "Treated with IV antibiotics and limb elevation. The fever settled on day 2 and the swelling reduced; dressing done before discharge.",
            Procedures: prefill.Procedures, InvestigationsSummary: null, ConditionAtDischarge: "Stable, afebrile, walking without help",
            Medications: "Oral antibiotics for 5 days", Advice: "Keep the leg elevated; daily dressing at the nearest clinic",
            FollowUpDate: null, FollowUpInstructions: "Review in the OPD after a week", Version: null));
        draft.Status.ShouldBe(DischargeSummaryStatus.Draft);

        return await doctor.PostAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/{draft.Id}/sign", new SignDischargeSummaryRequest(draft.Version));
    }

    /// <summary>
    /// Billing counter: collects a payment in cash. Counter money is collected in the cashier's open shift, so a shift is
    /// opened for the collection when none is open (and closed again, so no other test finds it open).
    /// </summary>
    private static async Task<ReceiptDto> CollectAtCounterAsync(ApiClient billing, Guid billId, decimal amount)
    {
        ShiftDto? opened = null;
        using (var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current"))
        {
            current.EnsureSuccessStatusCode();
            if (current.StatusCode == HttpStatusCode.NoContent)
            {
                opened = await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("IP billing", 0m));
            }
        }

        var receipt = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            billId, [new PaymentLineInput(PaymentMode.Cash, amount, null, null, null, null, null, null)], "Co-pay at discharge"));

        if (opened is not null)
        {
            var shift = await billing.GetAsync<ShiftDto>($"/api/v1/billing/shifts/{opened.Id}");
            shift.ExpectedCash.ShouldBe(amount, "the co-pay is the only cash in this shift");
            var closed = await billing.PostAsync<ShiftDto>($"/api/v1/billing/shifts/{opened.Id}/close", new CloseShiftRequest(shift.ExpectedCash, null, shift.Version));
            closed.Status.ShouldBe(ShiftStatus.Closed);
            closed.Variance.ShouldBe(0m);
        }

        return receipt;
    }

    /// <summary>Nurse (housekeeping): cleans the bed the discharge released; it becomes vacant.</summary>
    private static async Task CleanBedAsync(ApiClient nurse, Guid wardId, Guid bedId, Guid admissionId)
    {
        var queue = await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={wardId}");
        var task = queue.Single(t => t.BedId == bedId && t.AdmissionId == admissionId && t.Trigger == HousekeepingTrigger.Discharge);
        task.Status.ShouldBe(HousekeepingStatus.Pending);

        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
        var done = await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Terminal cleaning done"));
        done.Status.ShouldBe(HousekeepingStatus.Completed);
    }

    // ---------------------------------------------------------------------- look-ups and checks

    /// <summary>
    /// Today in the hospital's calendar (IST). The bed-day rule counts midnights, so the journey must not straddle one:
    /// started in the last two minutes of a day, it waits for the next.
    /// </summary>
    private async Task<DateOnly> HospitalDayAsync()
    {
        var clock = server.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var untilMidnight = IndiaClock.StartOfDayUtc(IndiaClock.LocalDate(now).AddDays(1)) - now;
        if (untilMidnight < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(untilMidnight + TimeSpan.FromSeconds(1), clock, TestContext.Current.CancellationToken);
        }

        return IndiaClock.LocalDate(clock.GetUtcNow());
    }

    /// <summary>A bed of the ward that the patient may be put in (free, and his sex and age fit the ward).</summary>
    private static async Task<Guid> FreeBedAsync(ApiClient api, Guid patientId, Guid wardId)
    {
        var beds = await api.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patientId}&wardId={wardId}");
        return beds.First(b => b.IsEligible && b.Status == BedStatus.Vacant).BedId;
    }

    private static async Task<BedBoardItem> BedOnBoardAsync(ApiClient api, Guid wardId, Guid bedId)
    {
        var board = await api.GetAsync<List<BedBoardWard>>($"/api/v1/beds/board?wardId={wardId}");
        return board.ShouldHaveSingleItem().Beds.Single(b => b.BedId == bedId);
    }

    private static async Task<ServiceItemDto> ServiceAsync(ApiClient api, string code) =>
        (await api.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?search={code}")).Single(s => s.Code == code);

    /// <summary>What the admission is charged for each of its services, from Billing's quote (its tariff plan and bed class).</summary>
    private static async Task<Tariff> TariffAsync(ApiClient billing, Guid admissionId) => new(
        await PriceAsync(billing, admissionId, "BED-GEN"),
        await PriceAsync(billing, admissionId, "NURS-DAY"),
        await PriceAsync(billing, admissionId, "VISIT-DR"),
        await PriceAsync(billing, admissionId, "PROC-DRESS"));

    private static async Task<decimal> PriceAsync(ApiClient billing, Guid admissionId, string code)
    {
        var service = await ServiceAsync(billing, code);
        var quote = await billing.GetAsync<PriceQuoteDto>($"/api/v1/billing/tariffs/quote?serviceItemId={service.Id}&admissionId={admissionId}");
        quote.TaxRate.ShouldBe(0m, $"{code} is a GST-exempt healthcare service");
        quote.UnitPrice.ShouldBeGreaterThan(0m);
        return quote.UnitPrice;
    }

    private static List<BillLineDto> Lines(BillDetail bill, string code) => bill.Lines.Where(l => l.ServiceCode == code).ToList();

    /// <summary>One line per night, dated that day, at the day's tariff.</summary>
    private static void AssertDayLines(BillDetail bill, string code, DateOnly[] nights, decimal tariff)
    {
        var lines = Lines(bill, code);
        lines.Select(l => IndiaClock.LocalDate(l.ServiceDate)).Order().ToArray().ShouldBe(nights, $"one {code} line for each night");
        lines.ShouldAllBe(l => l.UnitPrice == tariff && l.NetAmount == tariff);
    }

    private static string UniqueLetters(int length) =>
        new(Guid.NewGuid().ToString("N")[..length].Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());

    private static string UniqueDigits(int length) =>
        string.Concat(Guid.NewGuid().ToByteArray().Take(length).Select(b => (char)('0' + (b % 10))));

    /// <summary>Unit prices of the stay's services on the admission's tariff.</summary>
    private sealed record Tariff(decimal BedDay, decimal NursingDay, decimal DoctorVisit, decimal Dressing);
}
