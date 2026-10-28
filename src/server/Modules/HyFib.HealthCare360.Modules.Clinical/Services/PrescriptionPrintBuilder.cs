using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// What a printed prescription shows (ERX-04): letterhead, prescriber registration, patient, the encounter's diagnoses,
/// known allergies, medicines with English directions and the same in Hindi and Tamil, advice, follow-up, the QR verification link and the
/// doctor's DSC signature while it checks out. The
/// print model, the printed and downloaded document, the portal's PDF and the copy sent with the "prescription shared"
/// message are all made from it. It applies no access rules: callers check the permission or portal scope, the
/// restricted-record guard, that the prescription is signed and that the DSC rules let it be issued
/// (<see cref="PrescriptionFinalCopies"/>), and audit the access.
/// </summary>
internal sealed class PrescriptionPrintBuilder(HospitalDbContext db, ISettingsService settings, TimeProvider time)
{
    /// <param name="p">The prescription with its items.</param>
    /// <param name="signature">
    /// Its DSC signature as re-checked for this copy (<see cref="PrescriptionFinalCopies.CheckAsync"/>). A valid one is
    /// printed with the prescriber's block as the doctor signed it (name, qualification, registration); any other prints
    /// as "Signed electronically", with the block as the practitioner master has it.
    /// </param>
    public async Task<PrescriptionPrintModel> BuildAsync(Prescription p, PrescriptionDigitalSignatureDto signature, CancellationToken ct)
    {
        var today = time.Today();
        var patient = await db.Set<Patient>().AsNoTracking().Where(x => x.Id == p.PatientId)
            .Select(x => new { x.Id, x.Uhid, x.FullName, x.DateOfBirth, x.Gender, x.PreferredLanguage }).FirstAsync(ct);
        var diagnoses = p.EncounterId is { } encounterId
            ? await db.Set<Diagnosis>().AsNoTracking().Where(d => d.EncounterId == encounterId && d.Type != DiagnosisType.Differential)
                .OrderByDescending(d => d.IsPrimary).Select(d => d.IcdCode == null ? d.Title : d.Title + " (" + d.IcdCode + ")").ToListAsync(ct)
            : [];
        var allergies = await db.Set<Allergy>().AsNoTracking().Where(a => a.PatientId == p.PatientId && a.Status == AllergyStatus.Active)
            .Select(a => a.Allergen).ToListAsync(ct);
        var encounter = p.EncounterId is { } eid
            ? await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(e => e.Id == eid)
                .Select(e => new { e.FollowUpDate, e.FollowUpAdvice }).FirstOrDefaultAsync(ct)
            : null;

        var lines = p.Items.OrderBy(i => i.Sequence).Select(i =>
        {
            var (english, hindi) = FrequencyLabels.For(i.Frequency, i.CustomFrequency);
            return new PrescriptionPrintLine(i.Sequence, i.DrugName, i.GenericName, i.Strength, i.DosageForm, i.Route, i.Dose, i.FrequencyText, english, hindi,
                FrequencyLabels.Tamil(i.Frequency, i.CustomFrequency), i.DurationDays, i.Quantity, i.Instructions, i.IsControlledDrug);
        }).ToList();
        var publicAddress = p.VerificationToken is null ? null : await PortalAddress.BaseUrlAsync(settings, p.FacilityId, ct);
        var prescriber = await PrintQueries.PrescriberAsync(db, p, ct);
        var digitalSignature = PrescriptionFinalCopies.PrintSignature(p, signature);
        if (digitalSignature is not null && prescriber is not null && PrescriptionCanonicalForm.TryReadParties(p.SignedContent!) is { } signed)
        {
            prescriber = prescriber with
            {
                DisplayName = signed.PrescriberName,
                Qualification = signed.PrescriberQualification,
                RegistrationCouncil = signed.RegistrationCouncil,
                RegistrationNumber = signed.RegistrationNumber,
            };
        }

        return new PrescriptionPrintModel(
            p.Id, p.PrescriptionNumber, p.Status, p.SignedAt,
            await PrintQueries.LetterheadAsync(db, p.FacilityId, ct),
            prescriber,
            new PrescriptionPrintPatient(patient.Id, patient.Uhid, patient.FullName, patient.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
                patient.Gender, patient.PreferredLanguage),
            diagnoses, allergies, lines, p.Advice, encounter?.FollowUpDate, encounter?.FollowUpAdvice, p.RefillsAllowed, p.VerificationToken,
            PrescriptionVerificationLinks.Path(p.VerificationToken),
            PrescriptionVerificationLinks.Url(publicAddress, p.VerificationToken),
            digitalSignature);
    }
}
