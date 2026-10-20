# ABDM profiles used by the tests (ndhm.in 6.5.0)

The tests in `Abdm/` (and `AbdmExportTests` in the integration tests) check every ABDM bundle HC360 makes against the
profiles of the **FHIR Implementation Guide for ABDM**, published by the National Resource Center for EHR Standards
(NRCeS, https://nrces.in/).

| | |
|---|---|
| Package | `ndhm.in` version `6.5.0` (canonical `https://nrces.in/ndhm/fhir/r4`, FHIR 4.0.1, IG build date 2025-05-08) |
| Source | npm registry, https://registry.npmjs.org/ndhm.in/-/ndhm.in-6.5.0.tgz (published there 2026-03-05) |
| Integrity | `sha512-Xp4pA47PdjP0obww4iCGeqv/e4dCnvrtWan/609sV0EZ1aZVWCfKB6y2wEBDl4IHbdTouYh9GtnkItcJ15Q2Jg==` (checked against the registry's record) |
| Downloaded | 2026-10-20 |
| Licence | CC0-1.0 (public domain dedication, as declared in the package's `package.json`), so copying the files here is allowed |

The files are copied from the package unchanged; only the ones the tests need are here.

- `package/StructureDefinition-DocumentBundle.json`, `package/StructureDefinition-DischargeSummaryRecord.json` and
  `package/StructureDefinition-PrescriptionRecord.json`: the profiles a discharge summary or prescription bundle must
  meet.
- The other `package/StructureDefinition-*.json` files: every profile those can point to (a Composition section entry
  or reference may target any of them), plus the profiles those in turn point to. Firely's validator compiles each
  profile a reference may target before it validates, so it refuses to run with any of them missing.
- `package/ValueSet-ndhm-identifier-type-code.json` and `package/CodeSystem-ndhm-identifier-type-code.json`: the
  identifier types the Patient, Practitioner and Organization profiles bind (extensible), so that binding is checked
  offline rather than skipped.
- `example/Bundle-DischargeSummary-example-04.json` and `example/Bundle-Prescription-example-06.json`: the guide's own
  discharge summary and prescription examples. They must validate, which shows the validator is set up the way NRCeS
  intended.

The FHIR R4 core definitions come from the `Hl7.Fhir.Specification.Data.R4` NuGet package (`specification.zip`), not
from here. SNOMED CT, LOINC and other external terminologies cannot be checked offline; see `Abdm/AbdmProfileValidator.cs`
for what the validator skips and how the tests make sure nothing else is skipped.

To move to a newer guide, download the new `ndhm.in` package from the npm registry, replace these files with the same
files from it (adding any profile the validator then asks for), update this README and `AbdmProfiles.ImplementationGuideVersion`,
and run the Interop module tests and `AbdmExportTests`.
