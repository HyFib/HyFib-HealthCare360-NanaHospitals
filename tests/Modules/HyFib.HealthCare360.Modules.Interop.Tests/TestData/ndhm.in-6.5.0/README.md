# ABDM profiles used by the tests (ndhm.in 6.5.0)

The tests (`AbdmDocumentTests`, `Abdm/ValidatorSetupTests`, `Nhcx/NhcxClaimBundleTests`, and `AbdmExportTests` in the
integration tests) check every ABDM document and NHCX claim HC360 makes against the profiles of the **FHIR Implementation Guide for ABDM**, published by the National Resource Center for EHR Standards
(NRCeS, https://nrces.in/).

| | |
|---|---|
| Package | `ndhm.in` version `6.5.0` (canonical `https://nrces.in/ndhm/fhir/r4`, FHIR 4.0.1, IG build date 2025-05-08) |
| Source | npm registry, https://registry.npmjs.org/ndhm.in/-/ndhm.in-6.5.0.tgz (published there 2026-03-05) |
| Integrity | `sha512-Xp4pA47PdjP0obww4iCGeqv/e4dCnvrtWan/609sV0EZ1aZVWCfKB6y2wEBDl4IHbdTouYh9GtnkItcJ15Q2Jg==` (checked against the registry's record) |
| Downloaded | 2026-10-20 |
| Licence | CC0-1.0 (public domain dedication, as declared in the package's `package.json`), so copying the files here is allowed |

The files are copied from the package unchanged; only the ones the tests need are here.

- `package/StructureDefinition-DocumentBundle.json`, `package/StructureDefinition-DischargeSummaryRecord.json`,
  `package/StructureDefinition-PrescriptionRecord.json`, `package/StructureDefinition-OPConsultRecord.json` and
  `package/StructureDefinition-DiagnosticReportRecord.json`: the profiles HC360's discharge summaries, prescriptions,
  consultation notes and lab reports must meet.
- The other `package/StructureDefinition-*.json` files: every profile those can point to (a Composition section entry
  or reference may target any of them), plus the profiles those in turn point to. Firely's validator compiles each
  profile a reference may target before it validates, so it refuses to run with any of them missing.
- `package/StructureDefinition-ObservationVitalSigns.json` and `package/StructureDefinition-ObservationBodyMeasurement.json`:
  the profiles of a consultation's vital signs and body measurements (added 2026-10-21 from the same package, checked
  byte for byte against the files already here).
- `package/ValueSet-ndhm-identifier-type-code.json` and `package/CodeSystem-ndhm-identifier-type-code.json`: the
  identifier types the Patient, Practitioner and Organization profiles bind (extensible), so that binding is checked
  offline rather than skipped.
- `example/Bundle-DischargeSummary-example-04.json` and `example/Bundle-Prescription-example-06.json`: the guide's own
  discharge summary and prescription examples. They must validate, which shows the validator is set up the way NRCeS
  intended.
- `package/StructureDefinition-ClaimBundle.json`, `package/ValueSet-ndhm-claim-type.json` and
  `package/ValueSet-ndhm-diagnostic-type.json`: the NHCX claim bundle profile (the Claim, Coverage, Patient, Organization
  and Practitioner profiles it uses were already here), and the claim types and diagnosis types the claims are checked
  against (`Nhcx/NhcxClaimBundleTests`). Added 2026-10-26 from the same package; the files already here were checked
  byte for byte against it.
- `example/Bundle-ClaimBundle-preauthorization-example-01.json` and `example/Bundle-ClaimBundle-settlement-example-01.json`:
  the guide's own pre-authorisation and claim examples, which must validate too.

The FHIR R4 core definitions come from the `Hl7.Fhir.Specification.Data.R4` NuGet package (`specification.zip`), not
from here. SNOMED CT, LOINC and other external terminologies cannot be checked offline; see `Abdm/AbdmProfileValidator.cs`
for what the validator skips and how the tests make sure nothing else is skipped.

To move to a newer guide, download the new `ndhm.in` package from the npm registry, replace these files with the same
files from it (adding any profile the validator then asks for), update this README and `AbdmProfiles` (`src/server/Modules/HyFib.HealthCare360.Modules.Interop/Abdm/AbdmCodes.cs`),
and run the Interop module tests and `AbdmExportTests`.
- `package/StructureDefinition-CoverageEligibilityRequest.json`, `…CoverageEligibilityRequestBundle.json`,
  `…CoverageEligibilityResponse.json`, `…CoverageEligibilityResponseBundle.json`, `…ClaimResponse.json` and
  `…ClaimResponseBundle.json`: the NHCX eligibility check and the insurers' answers (`Nhcx/NhcxResponseTests`). Added
  2026-10-27 from the same package; the files already here were checked byte for byte against it.
- `example/Bundle-CoverageEligibilityRequestBundle-validation-example-01.json` and `…-benefits-example-01.json`, the four
  `example/Bundle-CoverageEligibilityResponseBundle-*-example-01.json` and the four
  `example/Bundle-ClaimResponseBundle-*-example-01.json`: the guide's own checks and answers, which must validate and
  which the response reader is checked against figure by figure.
