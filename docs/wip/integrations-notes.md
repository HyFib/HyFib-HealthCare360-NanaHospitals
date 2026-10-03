# Integrations item: survey notes (2026-10-02)

Audit: no PACS image retrieval or DICOM worklist; no serial-port analysers or sending orders to them; no fingerprint
sign-in or thermal label printing; ICD-10 catalogue incomplete; Tally and the payment gateway not reconciled.

## Lab analysers (Integrations/Laboratory, Diagnostics)
- TCP only: LabGatewayHostedService (one TcpListener per instrument, jobs nodes), LabInstrumentListener (IP allow-list,
  connection cap) -> MllpSession (HL7 ORU/OUL results only; QBP/ORM not implemented) or AstmSession (full E1381 link
  layer, ACK after store, host query Q -> H/P/O/L answered after EOT, contention + 6 retries).
- Sessions run over a plain Stream; tested with DuplexPipe (Integrations.Tests/TestSupport). Reusable over any byte stream.
- Settings `integrations.lab` + `integrations.lab.instruments` (JSON array {code, protocol Hl7v2|Astm, port, allowedIps,
  encoding, sampleIdFields, hostQuery}); edited as raw JSON in admin settingsForms.ts:1450-1508 (checkInstruments :585).
- Sink: Platform/Contracts/InstrumentContracts.cs IInstrumentResultSink {ReceiveAsync, GetOrderForSampleAsync}; impl
  Diagnostics/Services/InstrumentResultSink.cs (instrument registered+active, same facility as tube, exact barcode,
  auto-receive Collected tube, LabTest.InstrumentChannelCodes `CH` or `INSTR:CH`). LabInstrument.IsBidirectional never read.
- lab.sample series `{SEQ:9}` global locked. AutoValidationPolicy. Analysers tab lab/setup.tsx:283-365.
- Desktop: WPF + WebView2 net10.0-windows, per-user, runs only while signed in. Bridge (BridgeHost.cs:117-168): device.info,
  network.status, printer.list/printHtml/printRaw, shell.openExternal, notifications.show, speech.dictate. Device
  enrolment one-time code -> deviceId+secret (DPAPI CurrentUser). No installer project (deploy/windows/Install-HC360Desktop.ps1).
- System.IO.Ports pinned in Directory.Packages.props:56, unused. No agent/service on client PCs.
- SRS INT-LAB (SRS.md:326, Must): "TCP/IP and serial (via Desktop) interfaces for HL7 v2.x and ASTM E1381/E1394 analysers,
  uni-/bi-directional (host query)". A-06 (:412) serial via a workstation's Desktop bridge. DED-01 (:129) services run
  without interactive logon. ARCHITECTURE.md:221 lists serial.*, scanner.data, biometric.capture bridge methods (absent);
  :241 ILabInstrumentGateway (absent). integrations.md:275-310 accurate; :425 "HL7 host query not implemented".
- Demo XN550 seeded HL7 + bidirectional (can't work). Server installer firewall opens only 7360, 5353.
- Suggested: headless lab agent (Windows service) relaying COM bytes to the server over pinned TLS (WebSocket), new
  DeviceKind; server transport feeding AstmSession/MllpSession; serial settings (transport, deviceId, COM, baud, parity,
  handshake). Orders: HL7 QBP^Q11->RSP^K11; order download via outbox on sample receipt; AstmSession _outgoing queue needs
  idle bidding; one switch LabInstrument.IsBidirectional.

## Fingerprint sign-in and labels
- Auth: AuthEndpoints.cs:16-93; AuthService login 120-210 (lockout, enrolled-device check 179-183, TOTP 192-207, PIN unlock
  548-611); refresh rotation; PlatformOptions MFA required for SuperAdmin/HospitalAdmin; EnrolledDeviceRequiredRoles empty.
  IdentityEntities: no credential table; AuthChallengeKind {Mfa, Otp}. Web login.tsx:134-178 (sends device id/secret from
  bridge), LockScreen.tsx (PIN/password), security.tsx (MFA, PIN). Desktop overrides cert errors for private CA (AlwaysAllow).
- No biometrics except HR ZKTeco ADMS attendance. SRS :80 "fingerprint scanners (UIDAI-certified RD service)", INT-BIO :323
  (Must) "fingerprint login/verification at workstations through the RD service, and patient identity verification
  (optional)", UX-11 :348 quick unlock (PIN/biometric), A-07 :413 RD-service readers at desks.
- WebAuthn caveats: Windows Hello can't tell staff apart on shared Windows logons; Chromium refuses WebAuthn with cert
  errors (private CA override); no WebAuthn on IP addresses. RD service output is encrypted for UIDAI (no local matching).
  CSP: connect-src 'self', Permissions-Policy usb=() serial=() (SecurityHeaders.cs).
- Labels: bwip-js SVG (packages/ui print/barcode.ts); core print.ts printHtml (bridge on desktop, iframe in browser);
  page sizes A4, A5, Receipt80, Label50x25; desktop PrintService silent to default printer; RawPrinter.cs RAW spooler;
  server POST /api/v1/integrations/labels -> ZPL/TSPL (ZplLabelBuilder, escaping), generic layout, 203 dpi; only used by
  the admin integration test. Flows (all HTML 50x25, no printer name): lab tubes (modules/lab/print.ts, labelHtml.ts),
  pharmacy, wristband (inpatient/print.ts on 50x25), CSSD, blood units, asset tags, UHID card (A5 dialog), token slip.
- SRS INT-BAR :321 (Must) "ZPL/TSPL raw printing ... through Desktop"; :80 ZPL/TSPL label printers.

## ICD-10 (Clinical)
- IcdCode -> clinical.icd_codes (unique Code+Release, GIN trigram on SearchText); releases "ICD-10 2019", "ICD-11 MMS",
  "ICD-10-PCS"; Source Seed/Online/Manual. Seed C# arrays IcdCodeCatalogue(.Part2).cs: 594 codes / 475 categories (~5%).
  SeedIcdCodesAsync inserts missing only. IcdClassification.cs hard-coded chapters/blocks. 96 PCS codes.
- GET /api/v1/mrd/icd-codes (Take 300 unordered then rank in memory, IcdSearchRanker); /online (ICD-11 default; WHO
  ICD-API can't text-search ICD-10); POST/PUT single code (mrd.coding). No import. Web never sends release.
- Usage: encounter diagnoses must exist in catalogue (any release); discharge codes must exist; MRD coding strict
  (billable); claims free text; AI dictation drops codes not in catalogue; FHIR always icd-10 system URI.
- Licensing: WHO ICD-10 copyright; repo MIT; ADR-009 (DECISIONS.md:53) rules out deps needing commercial licence.
  Recommendation: WHO ClaML importer with the hospital's licensed file (+ CMS PCS public domain), CodeSource.Import,
  deactivate withdrawn, POST /api/v1/mrd/icd-codes/import + ServerCli; fix SQL ordering before Take(300); WHO attribution.
- SRS: :58 standards; INT-ICD :322 "A local ICD-10 catalogue (seeded) plus online lookup"; MRD-01..06 :295.

## Tally and gateway (Billing, Integrations)
- Tally: TallyService builds a facility's IST day book (Domain/Tally.cs builder); HTTP POST to TallyPrime :9000 in batches
  of 50 (TallyPrimeExporter), response parsed (CREATED/ALTERED/ERRORS); file download; TallyExport row per facility+date
  (status Queued/Posted/NotConfigured/Failed, counts); nightly queue 00:30 IST (TallyJobs); outbox billing.tally.post;
  REMOTEID "HC360-<reference>" ACTION=Create. Ledger map setting billing.tally.ledgers (no form) + rename map
  integrations.tally.ledgerMap. UI routes/_app/billing/tally.tsx. Permission billing.tally.export.
- Tally gaps: no figures compared (counts only, no amounts/hash/read-back); BUG cancellation Credit Note uses bill number
  as reference -> same REMOTEID as the sale (Tally.cs:168) so Tally alters the sale; doctor-fee voucher numbers positional
  (Tally.cs:230); NotConfigured never happens (exporter always registered) -> nightly Failed; pharmacy/canteen sales absent.
- Gateway: Razorpay + simulator (RazorpayGateway orders, links, status, refunds); secrets integrations.payment.secret
  (encrypted); GatewayOrder per order; PaymentGatewayService (links, portal checkout, SyncAsync, webhook HMAC);
  ReceiptService 157-210 (mode PaymentGateway, ExternalId payment id, idempotent); appointment prepayments on Appointment
  (AppointmentPaymentService) -> advance via outbox; refunds RefundService.
- Gateway gaps: no settlement API/entities; clearing ledger never cleared to bank/fees/GST; orphans (appointment webhooks
  ignored, failed receipt leaves order Created with LastError not shown; nothing re-polls; Expired/Cancelled never set);
  refunds marked Paid on refund id; receipt times not capture times; webhook events not stored.
- SRS INT-TALLY :320 (Must) "ledger mapping, retry, reconciliation and an export-file fallback"; INT-PG :328 (Must)
  "refunds and reconciliation"; INT-POS :327 "poll status and reconcile"; :402 Tally remains the book of accounts.
- Suggested: per-voucher export lines (number, REMOTEID, type, amount, hash, status) + day-book vs day-end/bill/GST totals
  + mark imported manually + REMOTEID fix (BillNumber + "/C"); IPaymentGateway settlements (Razorpay settlement recon
  API), GatewaySettlement(+lines), nightly job matching ReceiptPayment.ExternalId / Refund.GatewayRefundId, re-poll stuck
  orders, flag payments without receipts, exceptions screen, settlement journal to Tally.

## PACS (pending survey)
- ImagingStudy (Diagnostics/Domain/Imaging/ImagingStudy.cs): AccessionNumber, StationAeTitle (free text), StudyInstanceUid
  assigned at order (DicomConventions.NewUid 2.25), PacsLinkedAt/SeriesCount/InstanceCount, LinkPacs. radiology.accession
  `{FAC}RD{YY}{SEQ:7}` locked, issued in ImagingService.CreateFromClinicalOrderAsync (ServiceOrderPlaced handler).
- RadiologyEndpoints.cs:23-62 (radiology.worklist perm): reconcile, viewer (:162 PatientAccessGuard + audit), worklist/mwl
  (:49, JSON only, unused, untested), pacs/reconcile. Web modules/radiology (viewer.ts new tab / desktop openExternal),
  routes/_app/radiology/index.tsx, studies/$studyId.tsx:196 "Images in PACS"; EMR ResultReviewDialog.tsx:169.
- IPacsClient (Platform/Integrations/IntegrationAbstractions.cs:154-167: Ping, FindStudiesAsync, BuildViewerUrl);
  Integrations/Pacs: DicomWebPacsProvider (QIDO studies only), QidoQuery, DicomJson, Simulator, PacsClientRouter.
  Settings integrations.pacs (enabled, provider, qidoUrl, authType, userName, viewerUrlTemplate, limit, timeZone) +
  integrations.pacs.secret (password, bearerToken); admin form settingsForms.ts:1297.
- WorklistAsync (ImagingService.cs:251-293) -> ModalityWorklistItem (RadiologyContracts.cs:98). Reconciliation manual only
  (ImagingService.cs:152-222; UHID guard :471). Viewer refuses unlinked studies (pacs.not_linked :237) though template
  supports {accession}. fo-dicom 5.2.6 pinned (Directory.Packages.props:48), unreferenced. Dev Orthanc in compose.dev.yml
  DICOMweb only. Cardiology DICOM stored as encrypted uploads. No FHIR ImagingStudy.
- SRS IMG-01 :202, IMG-02 :203 "DICOMweb (QIDO-RS/WADO-RS) and DICOM MWL, and open the viewer for a study from the
  patient record", INT-PACS :324 "DICOMweb QIDO/WADO/STOW, modality worklist, viewer launch URL templates, and
  study-to-order reconciliation by accession number"; :330 per-facility settings, encrypted creds, simulator, PHI-masked
  logs, health on dashboard. ARCHITECTURE.md:16 claims fo-dicom.
- Conventions: modules must not reference Integrations (LayeringTests:69) -> Platform contract (like IInstrumentResultSink,
  resolved per message in a new scope). Hosted listeners: copy LabGatewayHostedService (RunsJobs, re-read settings each
  minute, bind address, allow-lists, dashboard status via IntegrationStatusService). Background work has no facility filter.
- Build: MWL SCP hosted service (fo-dicom DicomServer, C-ECHO + C-FIND) with settings integrations.mwl (port, bind, AE,
  allowed calling AEs/IPs -> facility), contract IModalityWorklistSource (move ModalityWorklistItem to Platform);
  IPacsClient series QIDO + WADO-RS rendered/thumbnails (wadoUrl), proxied audited endpoints; viewer by accession before
  link; scheduled reconcile job.

## Build agents (launched 2026-10-02, worktree isolation; resume with SendMessage)
- a68e624d46949ba11 PACS (MWL SCP, WADO images, scheduled reconcile)
- a969b10b35a623906 Lab (serial lab agent service + WebSocket relay, order download, HL7 QBP/OML, IsBidirectional)
- ac378777bafbffadc Labels (printer roles, typed ZPL/TSPL templates, printLabel)
- a76a1ff8be0d17708 ICD-10 (ClaML importer, PCS, SQL-ordered search, import endpoint/CLI/UI)
- a8ebd6599543265f5 Tally (REMOTEID fix, per-voucher tracking, internal + read-back reconciliation, pharmacy revenue)
- ae998172a0308e54e Gateway (settlements, matching, exceptions, re-poll, refunds, webhook log, settlement journal)
- Fingerprint sign-in: the user chose to leave it for now (not covered in this item).
- Expected migrations: ICD-10, Tally, Gateway (regenerate when combining). PR #20 (review fixes for #19) open, auto-merge on.

## Agent results
- Gateway (ae998172a0308e54e) DONE, branch worktree-agent-ae998172a0308e54e (base f470440): ea913ab reconcile settlements,
  eedbf32 screen, 16b773d docs, a3519d9 receipt series gap fix. Migration 20261002211345_GatewayReconciliation
  (gateway_settlements/_lines, gateway_discrepancies, gateway_webhook_events; refunds.gateway_status/reference/settled_at/
  error + unique gateway_refund_id; simulator_transactions.settlement_id/settled_at; index appointments.gateway_order_id).
  Permission billing.gateway.reconcile. Endpoints /api/v1/billing/gateway. GatewayReconciliationJob. Settlement journal
  voucher in Tally day book (TallyDayBook optional param, Build, LoadDayBookAsync) -> merge with Tally agent. Receipts dated
  at capture; re-queues posted Tally day. Tests: Billing 277, Scheduling 123, Privacy 73, Architecture 82, Integrations
  504/507 (3 EdgePdfRenderer failures from Edge contention), staff 1302. IntegrationTests GatewayReconciliationTests (CI).

## State on 2026-10-03, when the work moved to a cloud session

The six parts were built by agents in local worktrees. All their work, finished or not, is pushed to these branches
(each based on main at c80d44e, after #20). WIP commits hold work the agents had not committed when they stopped:

| Branch | State | What is left |
|---|---|---|
| `wip/integrations-gateway` | Done (4 commits, all gates run) | Review |
| `wip/integrations-tally` | Server done; web screen WIP | Run the new web suites twice more, docs, final gates |
| `wip/integrations-pacs` | Server done; web WIP | Web gates (format, typecheck, lint, staff tests), docs check |
| `wip/integrations-lab` | Server + lab agent done; web WIP | Tests for the connection rules, web gates, docs |
| `wip/integrations-labels` | Server + desktop done; web WIP | Update three test files to the printLabel pattern, web gates |
| `wip/integrations-icd10` | Server done; web WIP | Check old "ICD-10 browser" references, typecheck, lint, tests, docs |

Fingerprint sign-in: the user chose to leave it for now (not covered).

## Plan

1. Finish each part on its branch (gates: `dotnet build` of what changed, `dotnet test --project <dir> -c Release
   --no-build`, `dotnet format whitespace --verify-no-changes`, LF endings; in src/web `pnpm install`, `pnpm
   format:check`, `pnpm typecheck`, `pnpm lint`, staff tests). Windows-only parts (the WPF desktop shell, the lab agent
   service, Edge PDF tests) can't build or run on Linux: CI runs .NET on windows-latest.
2. Review each part (money, idempotency, concurrency, security, PHI, migrations) and fix what is found.
3. Combine onto `claude/integrations` (from main). Conflicts are expected in IntegrationAbstractions.cs,
   IntegrationsServiceCollectionExtensions.cs, settingsForms.ts(+test), integrationsApi.ts, admin/integrations/index.tsx,
   docs/modules/integrations.md, Directory.Packages.props, the slnx, routeTree.gen.ts (regenerate with
   `node scripts/generate-routes.mjs` in src/web/apps/staff) and, between Tally and Gateway, Billing's Domain/Tally.cs and
   TallyService.LoadDayBookAsync. Migrations from icd10, tally and gateway each change the model snapshot: drop them and
   generate fresh ones in order after combining.
4. Suggested PRs (the user asks with "create a PR, do auto CI fix, and merge when green"; merge commits only, never
   squash): lab analysers + labels, PACS, ICD-10, Tally + gateway. Delete docs/wip before opening any PR.
5. Codex reviews often arrive after auto-merge: fix each late finding in a small follow-up PR, reply on its thread
   (ending "_🤖 Addressed by [Claude Code](https://claude.com/claude-code)_") and resolve it.

## Cloud session, 2026-10-03

- Toolchain here: .NET SDK 10.0.1xx from Ubuntu (global.json lowered locally with skip-worktree, never committed),
  PostgreSQL 16 for IntegrationTests (`HC360_TEST_DATABASE=Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres`).
- Each part was finished (web gates, staff tests), reviewed and fixed on a local branch `part/<name>` (worktrees under
  /home/user/wt). Review fixes: labels (wristband allergy states, raw print to installed printers only, setup
  validation, page fallback), lab (LabAgent credential refused for sign-in, E1381 contention, facility scoping, size
  limit, order claims, pinning, relay close; a 500 on every connection save), PACS (viewer and linking fail closed on
  patient mismatch, worklist filtering in SQL, peer re-check, throttled refusals, image proxy limits), ICD-10
  (switched-off codes, withdrawn codes on existing records, hand edits, Down, test isolation), Tally (legacy
  references for days posted before tracking, timeouts within the outbox's 30 s, xmin, key length, doctor-fee
  suffix, honest counter lines), gateway (double refund on retry, failed-refund exception cleared, lost exceptions,
  starved polling, facility guard, webhook in flight, late receipts, shared accounts, refund follow-up window).
- This branch is built as a stack of merges in PR order: lab, labels, then PACS, ICD-10, Tally, gateway. Each part's
  commits stay reachable from here.
- Known Linux-only failure, not ours: Platform.Tests StoredFileNameTests (Windows path), fine on windows-latest.

### Combined (all six parts), 2026-10-03
- Merge order on this branch: lab, labels, PACS, ICD-10, Tally, gateway. Migrations in order: LabAnalyserOrders
  (20261002215306), IcdCatalogueImport (20261003034119), TallyReconciliation (20261003035955), GatewayReconciliation
  (20261003040327); the later three regenerated with dotnet-ef after each merge, hand-written SQL kept.
  `has-pending-model-changes` is clean.
- After the gateway merge: TallyCorrections queues a one-day range (Tally changed QueueAsync); the Tally ledger form
  now carries the three settlement-journal ledgers (it saves the whole map).
- Checks on the combined branch: whole solution builds (EnableWindowsTargeting); every Linux-runnable .NET suite passes
  (IntegrationTests 101 on PostgreSQL, Integrations 877, Billing 327, …) except Platform StoredFileNameTests (Windows
  path, Linux only); web format, typecheck, lint (0 errors), staff 1417, portal 226, core 44, ui 16.
- For CI on Windows: Desktop.Tests, LabAgent.Tests (DPAPI), Edge PDF tests, WebView2 PemEncodedIssuerCertificateChain.
- Open: Razorpay `X-Refund-Idempotency` header to confirm in their docs (the `receipt` field carries the key too);
  printer setup trusts the page beyond the server approval (policy lock is the protection; documented); wristband
  reprint prompt when the first allergy is recorded; ARCHITECTURE.md bridge list names app.info, biometric.capture and
  scanner.data that the shell lacks; GatewayExceptions.test.tsx once timed out under heavy local load (passes alone).
- PRs, when asked: the stack can be cut at the merge commits (lab + labels, PACS, ICD-10, Tally + gateway) or opened
  as one PR from this branch. Delete docs/wip first.
