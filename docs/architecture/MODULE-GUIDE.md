# Module development guide (server)

How to build a bounded-context module for HC360 so it looks and behaves like the rest of the system.
The **reference implementation is the Patients module** (`src/server/Modules/HyFib.HealthCare360.Modules.Patients`) —
copy its patterns. Read [ARCHITECTURE.md](ARCHITECTURE.md) first.

## 1. What you own
| Path | Owner |
|---|---|
| `src/server/Modules/HyFib.HealthCare360.Modules.<Name>/**` | your module (all server code) |
| `tests/Modules/HyFib.HealthCare360.Modules.<Name>.Tests/**` | your module's tests |
| `docs/modules/<name>.md` | your module's API reference (endpoints, DTOs, permissions, events) |

Do **not** edit Platform, SharedKernel, other modules, the Server project, `Directory.*.props`, the solution or
EF migrations. If you need something there, stop and report it (the maintainers add it).

## 2. Layering
A module may reference only **lower** modules (see ARCHITECTURE.md §4). Everything upward goes through Platform:
* **Contracts** (`HyFib.HealthCare360.Platform.Contracts`): `IChargePoster` (post billable charges; idempotent on
  `SourceModule`+`SourceReference`), `IBillingStatusProvider`, `IPayerDirectory` (Billing's `PayerDirectory`: a payer's
  name and the patient's in-date policy with it, for the patient banner, each part only for staff who may read payers
  or policies; `NullPayerDirectory` without Billing), `IInstrumentResultSink`, `IPortalAccountResolver`,
  `IVerifiedCodingSource` (Clinical's verified coding of an episode, for Billing's claims).
* **Integration events** (`Platform/Contracts/IntegrationEvents.cs`): `ServiceOrderPlaced`, `PrescriptionSigned`,
  `PatientAdmitted`, `PatientTransferred`, `PatientDischarged`, `AdmissionCancelled`, `DiagnosticReportReleased`, `AppointmentBooked`,
  `MedicationDispensed`, `PaymentReceived`, `BillFinalized`, … Raise them from your aggregate
  (`Raise(new PatientAdmitted(...))`); handle others' events with `IDomainEventHandler<TEvent>` (auto-registered,
  runs inside the same transaction before commit).
* Refer to entities of other modules **by id** (`Guid PatientId`, `Guid AdmissionId`). Foreign keys are allowed only to
  lower modules you reference (e.g. `b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId)`).

## 3. Anatomy
```
<Name>Module.cs          IModule (Name = schema key), <Name>Permissions, NumberSeries, AddServices, MapEndpoints
<Name>NumberSeries.cs    the document numbers the module issues (see §8)
Domain/                  entities, enums, domain rules (methods returning Result), events raised
Persistence/             IEntityTypeConfiguration<T> classes — auto-discovered
Features/<Area>/         <Area>Contracts.cs (records + FluentValidation validators) and <Area>Endpoints.cs
Services/                application services (transactions, numbering, charges, notifications)
Seed/                    ISeeder (Order ≥ 100) — idempotent reference data; demo data only if SeedDemoData
```
Global usings (SharedKernel, Platform.*, EF Core, ASP.NET Core, FluentValidation) are already provided by
`src/server/Modules/Directory.Build.props`.

## 4. Entities
* Derive from `AuditableEntity` (GUID v7 `Id`, audit columns, soft delete, `Version` = PostgreSQL `xmin`).
  High-volume append-only rows may derive from `Entity` and carry `[NotAudited]`.
* Facility-owned rows implement `IFacilityScoped` (queries are filtered to the user's facility automatically;
  `FacilityId` is filled on insert from the signed-in user).
* Types: `decimal` money (18,2), `DateTimeOffset` for instants (UTC), `DateOnly`/`TimeOnly` for calendar values,
  enums (stored as strings — add new values at the end), `List<string>`/`List<Guid>` map to PostgreSQL arrays.
* Mark secrets/identifiers with `[SensitiveData]` (masked in audit diffs; `byte[]` values are always recorded by length
  and SHA-256); encrypt national identifiers with
  `IFieldProtector` (see `PatientRegistrationService`).
* Put state transitions in entity methods returning `Result` (see `Visit.Cancel`, `QueueToken.Call`).

## 5. Persistence
```csharp
internal sealed class LabOrderConfiguration : IEntityTypeConfiguration<LabOrder>
{
    public void Configure(EntityTypeBuilder<LabOrder> b)
    {
        b.ToTable("lab_orders", "diagnostics");          // schema = module key; columns become snake_case
        b.Property(x => x.OrderNumber).HasMaxLength(24);   // always bound strings
        b.HasIndex(x => x.OrderNumber).IsUnique();
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.LabOrderId).OnDelete(DeleteBehavior.Cascade);
    }
}
```
Use `db.Set<T>()` from `HospitalDbContext` (no DbSet properties). Do **not** add migrations; the maintainers
generate them after all modules compile.

## 6. Endpoints
* One static class per area: `group = app.MapGroup("/api/v1/<area>").WithTags("<Area>")`.
* **Every** endpoint: `.RequirePermission(<Name>Permissions.X)` (or `RequireAnyPermission`). Anonymous endpoints
  (public booking, webhooks) must be explicit (`.AllowAnonymous()` + `.RequireRateLimiting(RateLimitPolicies.Public)`).
  Machines that present their own credential (RPM gateways, biometric terminals) use `RateLimitPolicies.Device`
  instead, which is counted per credential rather than per client address.
* `EndpointAuthorizationTests` (ArchitectureTests) fails the build for an endpoint that states nothing. An anonymous
  endpoint, or one open to any signed-in user through a bare `RequireAuthorization()` (patients on the portal
  included), must also be added to that test's reviewed lists, with the reason it is safe. At run time a request whose
  endpoint states nothing still requires sign-in (the fallback policy).
* Bodies: `.WithValidation<TRequest>()` + a public `AbstractValidator<TRequest>` (use `CommonRules`: `PhoneNumber()`,
  `IndianMobile()`, `PinCode()`, `Gstin()`, `SafeText(max)`).
* Lists: `[AsParameters] PageQuery query` → `query.ToRequest()` → `.ToPagedResultAsync(page, ct)`; sort only via
  `OrderByKey(page, allowList, default)`.
* Results: return `IResult`; map `Result`/`Error` with `.ToOk()`, `.ToNoContent()`, `.ToCreated(...)`, `error.ToProblem()`.
  Error types: `Error.NotFound`, `Error.Field` (400), `Error.Conflict` (409), `Error.Rule` (422), `Error.Forbidden` (403).
* Updates carry `uint Version` and call `db.SetExpectedVersion(entity, request.Version)` (409 on conflict).
* Idempotency (`Idempotency-Key`) is handled by middleware — nothing to do.
* PHI: when returning clinical/patient data call `await audit.PatientRecordAccessedAsync(patientId, "what", ct)` once
  per request, and guard restricted records with `PatientAccessGuard.EnsureCanOpenAsync(patientId, ct)`.
* Patient-portal endpoints live under `/api/v1/portal/<area>`, require `PatientsPermissions.Portal` and must scope
  data with `PortalPatientScope` (`EnsureAccessAsync` / `AccessiblePatientIdsAsync`). The scope covers the patients on
  the account's own number and the family members its family links give it (PP-01). A step that relies on the
  patient having signed in with their own number, such as taking the sign-in as proof of identity, checks
  `OwnNumberPatientIdsAsync` instead.
* Dates arriving as query parameters are local (IST) calendar dates; convert with the helpers in
  `PatientQueries` (`time.Today()`, `IndiaTime`).

## 7. Permissions
Names are `<area>.<resource>.<action>` (e.g. `lab.results.verify`). Declare them in `<Name>Permissions.All`
with a group label, a plain-language description and default roles from `SystemRoles`
(grants are applied once at first start).

## 8. Services you can inject
| Need | Use |
|---|---|
| Document numbers (bill, accession, PO…) | Declare a `NumberSeriesDefinition` in `<Name>NumberSeries` and list it in `IModule.NumberSeries`, then `INumberSeries.NextAsync(<Name>NumberSeries.LabAccession, facilityId, ct)` inside `db.Database.BeginTransactionAsync`. Administrators set the formats before the first number (Hospital setup → Number formats); see [numbering.md](../modules/numbering.md) |
| Charge a patient | `IChargePoster.PostAsync(new ChargeRequest { ..., SourceModule = "diagnostics.lab", SourceReference = item.Id.ToString() }, ct)` |
| SMS / WhatsApp / e-mail | `INotificationService.Queue(templateKey, recipient, values, [NotificationChannel.Sms, ...])` (+ seed templates). A number that replied STOP on WhatsApp gets no WhatsApp message except the essential ones in `WhatsAppOptOutPolicy.EssentialTemplates` (sign-in codes); a new template that must reach the person for security has to be added there |
| In-app alerts to staff | `INotificationService.NotifyPermissionHoldersAsync(permission, facilityId, title, body, link, category, severity, ct)` |
| Real-time UI refresh | `IRealtimePublisher.PublishAsync(Topics.Lab(facilityId), "lab.sample.received", new { id }, ct)` — ids only |
| Files (PDF/images) | `IFileStore.SaveAsync/ReadAsync` (encrypted at rest) |
| A document patients receive (report, prescription, bill…) | Build a `PrintableDocument` (`Platform/Documents`) whose body comes only from `Html.Of($"…")`, which encodes every value; letterhead, logo, signatures and QR codes from `DocumentAssets`; `Footer` = patient · UHID · document number. Answer `GET …/document` with `document.ToDto()` (the staff app prints it) and `GET …/pdf` with `await document.ToPdfAsync(IPdfRenderer, ct)`, after the same permission, guard and audit as the record's own endpoint. Reference: `Diagnostics/Documents/LabReportDocument.cs` |
| Send a document with a message | Register an `INotificationDocumentSource` for its kind (it makes the PDF when the message is sent) and pass `new NotificationDocument(kind, id)` to `INotificationService.Queue`: e-mail attaches it, WhatsApp sends it in a template's document header |
| Settings | `ISettingsService.GetAsync<T>("module.setting", facilityId, ct)` |
| Feature switches (a part of HC360 an administrator can turn off, such as video consultations) | `.RequireFeature(FeatureKeys.Telehealth)` on its endpoints and hubs (503 `feature.disabled` while off); `IFeatureFlags.IsEnabledAsync(FeatureKeys.X, facilityId, ct)` and `FeatureProblems.DisabledError(...)` for an option inside a request that otherwise works. New switches go in `FeatureCatalog`; see [features.md](../modules/features.md) |
| Current user / facility | `ICurrentUser` (`UserId`, `FacilityId`, `PractitionerId`, `IsPatient`, `PatientId`) |
| Audit | `IAuditLogger.Add(...)` (same transaction) / `WriteAsync(...)` (immediate) |
| Time | `TimeProvider` (never `DateTime.Now`) |

## 9. Seeding
`internal sealed class XSeeder(...) : ISeeder { public int Order => 1xx; ... }` — must be idempotent (check by code
before insert). Service catalogue entries your module bills for go into `org.service_items` (`ServiceItem`) with
stable codes (`LAB-CBC`, `RAD-XR-CHEST`, …). Demo/sample transactional data only when `PlatformOptions.SeedDemoData`.
Such data reaches only a database made for training or testing: start-up stops before the seeders on a database that
holds patients and is not one (`TrainingDatabase`, Platform).

## 10. Build & test (you share the machine with other agents)
```bash
dotnet build src/server/Modules/HyFib.HealthCare360.Modules.<Name> --no-dependencies -v q
dotnet build tests/Modules/HyFib.HealthCare360.Modules.<Name>.Tests --no-dependencies -v q
dotnet test --project tests/Modules/HyFib.HealthCare360.Modules.<Name>.Tests --no-build
```
Warnings are errors. Unit-test domain rules (pure C#, no database). Do not run the server or touch Docker.

## 11. Definition of done
* Module compiles with zero warnings; tests pass.
* Every SRS requirement ID assigned to you is covered by entities + endpoints (+ seed data where useful).
* `docs/modules/<name>.md` lists endpoints (method, path, permission, request/response record names and key
  fields), events raised/handled, notification templates, number series and seeded service codes.
