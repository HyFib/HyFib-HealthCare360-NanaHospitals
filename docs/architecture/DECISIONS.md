# Architecture Decision Log

Each entry records a decision, its context, and the consequences. New decisions are appended; superseded ones are marked, never deleted.

---

## ADR-001 — Client-server on a dedicated WiFi-connected Windows machine
**Status:** Accepted (SRS rev 2.0)
**Context:** Nana Hospitals wants HC360 as a Windows application running on a dedicated machine that connects to the hospital WiFi. Workstations across departments must share one consistent dataset in real time.
**Decision:** A dedicated *Server Node* runs HC360 Server and PostgreSQL as Windows services. Workstations run *HC360 Desktop*, which connects to the Server Node over TLS on the WiFi LAN and finds it through mDNS. An all-in-one install (both on one machine) is supported.
**Consequences:** Operations stay on-premises and keep working through internet outages. The WiFi link becomes a critical dependency, which is mitigated by idempotent retries, auto-reconnect, draft auto-save, link monitoring and a recommended Ethernet fallback.

## ADR-002 — .NET 10 modular monolith for the server
**Status:** Accepted
**Context:** About 40 functional modules must share patient, visit and billing data transactionally, be maintainable by one team, and still be able to scale.
**Decision:** One ASP.NET Core 10 host composed of module projects with enforced layering. It uses Minimal APIs, a single EF Core `HospitalDbContext` with one schema per module, and contracts in Platform for upward calls.
**Consequences:** Transactions are simple and deployment is a single binary. Boundaries are enforced by project references and architecture tests. Modules can be split out later.

## ADR-003 — WebView2 hybrid desktop shell (WPF) instead of pure XAML UI or Electron
**Status:** Accepted
**Context:** The UI must be best-in-class and Windows-native in look, and also reachable from tablets and the patient portal. Hardware access (printers, scanners, biometrics, serial analysers) is required.
**Decision:** A thin .NET 10 WPF shell hosts WebView2, which renders the React + Fluent UI v9 application served by the Server Node. A JSON-RPC bridge exposes allow-listed native capabilities. This is the architecture used by the new Microsoft Teams and Outlook.
**Consequences:** There is one UI code base for desktop, tablets and portal, and server upgrades update every workstation instantly. The Evergreen WebView2 runtime is present on Windows 11. The installer footprint is small compared with Electron, and native device access stays in C#.

## ADR-004 — PostgreSQL 17
**Status:** Accepted
**Context:** A reliable RDBMS is needed with no per-core licence cost on a single Windows machine, and it must scale to millions of records.
**Decision:** PostgreSQL 17 running as a Windows service, using `pg_trgm` for fuzzy search, `xmin` for optimistic concurrency and partitioning for high-volume tables.
**Consequences:** No licence cost, strong JSON and full-text search, and streaming replication for a warm standby. SQL Server would need Standard edition for databases above 10 GB.
**Amended (October 2026):** partitioning was decided but not built: the audit, RPM-reading and message tables are plain tables (SCAL-04, see [TRACEABILITY.md](../requirements/TRACEABILITY.md)). PostgreSQL's declarative partitioning remains the plan when their volume calls for it.
**Amended again (October 2026):** built (SCAL-04, ADR-013): the four tables are declarative range partitions, one per UTC month.

## ADR-005 — Private hospital CA with certificate pinning on the LAN
**Status:** Accepted
**Context:** Traffic crosses shared WiFi. Public certificates need a public DNS name and internet access.
**Decision:** On first start the server creates a private CA and a server certificate (SANs: host, host.local, IPs). The Desktop pins the SHA-256 fingerprint during enrolment. Browsers or tablets install the CA from `/api/v1/system/ca-certificate`. An administrator may supply a corporate or public certificate instead.
**Consequences:** Encryption and server authentication work with no internet dependency. The certificate must be renewed (dashboard warning at 30 days).

## ADR-006 — Access tokens + rotating refresh cookies, server-side permissions
**Status:** Accepted
**Decision:** 10-minute ES256 JWT access tokens are held in memory, and refresh tokens are `HttpOnly; Secure; SameSite=Strict` cookies scoped to `/api/v1/auth`, rotated with reuse detection. Permissions are resolved per request from a cache instead of being embedded in the token.
**Consequences:** Revocation is immediate, XSS cannot read refresh tokens, and CSRF is mitigated by SameSite plus a required custom header on refresh.

## ADR-007 — Idempotency keys for all unsafe requests
**Status:** Accepted
**Context:** On WiFi, a request may succeed on the server while the response is lost, and the client then retries.
**Decision:** Every unsafe request carries `Idempotency-Key`. The server persists a key → response snapshot for 24 h and replays it on retry.
**Consequences:** Retries never duplicate bills, payments, dispenses or orders. It costs one small table and a filter.

## ADR-008 — Transactional outbox for integrations
**Status:** Accepted
**Decision:** Modules write integration messages (SMS, WhatsApp, Tally postings, webhook calls) to `platform.outbox_messages` in the same transaction as the business change. A dispatcher delivers them with exponential backoff and a dead-letter state.
**Consequences:** Messages survive internet outages and are delivered at least once. Providers must handle duplicates, which is done with a message ID passed as the provider reference.

## ADR-009 — No commercial-licence runtime dependencies
**Status:** Accepted
**Context:** MediatR, AutoMapper and MassTransit moved to commercial licences, and QuestPDF needs a paid licence above $1M revenue.
**Decision:** Use plain Minimal API handlers and hand-written mapping. PDFs are rendered from HTML: WebView2 `PrintToPdf` on the Desktop, and headless Microsoft Edge (installed on every Windows machine) on the server.
**Consequences:** No licence exposure for the hospital, at the cost of slightly more explicit code.

## ADR-010 — Hash-chained, append-only audit log
**Status:** Accepted
**Decision:** Audit rows are inserted without locks, and a sealer job computes a SHA-256 chain in sequence order. A database trigger forbids UPDATE (except sealing a null hash) and DELETE.
**Consequences:** It is tamper-evident without serialising business transactions. Verification is available on demand.

## ADR-011 — AI assistant is provider-pluggable, off by default, human-in-the-loop
**Status:** Accepted
**Decision:** Speech-to-text providers are Windows offline speech (Desktop) or a configured cloud/on-prem service. Text structuring and intent parsing go through an `IAiTextService` whose default provider is Anthropic Claude. Prompts strip direct identifiers. Output is always a draft that the clinician must accept, and every call is audited.
**Consequences:** Hospitals can choose on-prem or cloud AI according to their data-processing agreements. Nothing clinical is committed without human review.

## ADR-012 — Documents patients receive are made on the server
**Status:** Accepted (implements the server half of ADR-009)
**Context:** Lab reports must reach patients as PDFs with QR verification, on the portal, by WhatsApp and by e-mail
(LIS-07); the portal offers prescriptions, reports, discharge summaries and bills as PDF downloads (PP-02); MIS reports
export to PDF. Until now every document was laid out in the browser, so the server had nothing to send, and the portal
kept its own copies of the templates.
**Decision:** Documents that leave the hospital (lab, radiology and cardiology reports, the health check-up report,
prescriptions, discharge summaries, A4 bills, MIS reports) are laid out once, on the server, as HTML built through an
encoding builder (`Platform/Documents`). The staff app prints that HTML; the server turns the same HTML into a PDF with
headless Microsoft Edge, driven over a private pipe (`Integrations/Pdf`). Page size, margins and the running footer
(who and what the page belongs to, page X of Y) are CSS in the document, so the printed copy and the PDF match. Slips,
labels, 80 mm receipts and internal lists stay in the browser.
**Consequences:** One template per document, and the copy a patient receives is the one the hospital printed. Each
module owns its document templates next to its data. The Server Node needs Edge (present on Windows 10/11 and Server
2022); without it PDFs are unavailable and printing still works. A PDF costs about a third of a second once the browser
is running, and the browser takes about 250 MB of memory while it runs; it closes when idle.

## ADR-013 — Several API nodes share state through PostgreSQL and a file share
**Status:** Accepted (SRS SCAL-01 to SCAL-04)
**Context:** A larger hospital runs several API nodes behind a reverse proxy, with one node running the jobs. Any node may
answer any request, so nothing that decides access may live in one node's memory, and the keys that wrap identifiers
and sign tokens must be the same on every node. Redis is optional (it carries real-time updates only), and the audit
trail, RPM readings and message logs grow by millions of rows a year.
**Decision:** Shared state stays in the PostgreSQL every node already uses: sessions, challenges and break-the-glass
grants are rows; caches kept for speed are expired on every node through `LISTEN/NOTIFY`, and bypassed while a node
cannot hear them; one-time tokens and attempt counters use a small store with expiry and atomic increment
(`IClusterStore`, `platform.cluster_entries`). The keys and documents live in a shared folder, and the Data Protection
ring that wraps the keys there is protected with a certificate every node holds, never in the database. Redis is used
only as the SignalR backplane, and its absence degrades real-time updates without failing a request. The busiest
tables are partitioned by month in the database itself (declarative range partitions), with primary keys that include
the month and no foreign keys into them.
**Consequences:** No new infrastructure is needed to be correct with several nodes; a security check never fails open
because Redis is away. Each cluster-store call is one statement on the database, so it suits sign-in-rate traffic, not
every request. A copy of the database alone opens no encrypted value. The shared folder and the cluster key
certificate become things to back up and protect. Partitioned tables cannot be the target of a foreign key, and the
audit chain position is kept unique by its trigger and the sealer's lock rather than by an index.
