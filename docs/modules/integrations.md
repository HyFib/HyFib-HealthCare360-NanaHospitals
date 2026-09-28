# Integrations

Adapters between HC360 and external systems: SMS, WhatsApp, e-mail, payment gateway, card/UPI POS, Tally Prime,
PACS (DICOMweb), laboratory analysers (HL7 v2 / ASTM), barcode label printers and the WHO ICD-API.

| | |
|---|---|
| Project | `src/server/HyFib.HealthCare360.Integrations` (module key `integrations`, registered by `IntegrationsModule`) |
| Schema | `integrations`: `simulator_messages`, `simulator_transactions`, `whatsapp_inbound_messages` (the integration log itself is the platform's `integration_logs`) |
| Requirements | SRS §5: INT-SMS, INT-WA, INT-PG, INT-POS, INT-TALLY, INT-PACS, INT-LAB, INT-BAR, INT-ICD; SEC-10 (PHI in logs) |
| Tests | `tests/HyFib.HealthCare360.Integrations.Tests`: 452 tests covering signatures, templates, every provider against a fake HTTP handler, the routers other modules resolve (payment, POS, PACS, ICD), webhook parsers, Tally XML, DICOM JSON, the HL7/MLLP and ASTM E1381/E1394 sessions over an in-memory socket, PHI masking, labels, validators and DI wiring |

## How it fits together

* **Platform contracts, one router each.** Other modules depend only on the contracts in
  `Platform/Integrations/IntegrationAbstractions.cs` (`ISmsSender`, `IWhatsAppSender`, `IEmailSender`,
  `IPaymentGateway`, `IPosTerminal`, `IAccountingExporter`, `IPacsClient`, `IIcdLookup`, `ILabelPrinterService`) and
  on `IInstrumentResultSink` for analyser results. Integrations registers a **router** for each contract. On every call
  the router reads the facility's settings and delegates to the configured provider. Switching provider, or turning
  an integration off, takes effect within a minute (the settings cache) and needs no restart.
* **Registration order.** The routers are registered with plain `Add*`. The platform adds its "not configured"
  fallbacks later with `TryAdd*`, so when this module is loaded the routers win. Without it, the fallbacks apply.
* **Simulator for every channel.** Setting `"provider": "simulator"` makes the integration work end to end with no
  credentials. SMS, WhatsApp, e-mail and Tally write to `integrations.simulator_messages`. Payment orders and links
  are captured immediately and POS sales are approved after a few seconds, all stored in
  `integrations.simulator_transactions`. Administrators browse these under
  `/api/v1/admin/integrations/simulator/*`. Payment links point to the reserved host `pay.simulator.invalid`.
* **Integration log.** Every external call, inbound webhook and analyser message writes one row through the platform
  `IIntegrationLogger`, via the internal `IntegrationTrace` helper. Rows record integration, provider, operation,
  direction, success, duration, reference and summaries. Adapters build their own summaries: masked recipient,
  template name, counts and status codes. They never log message bodies, patient names or credentials. The platform
  logger then masks phone numbers, e-mails and secrets as a second line of defence. Raw analyser messages are logged
  only after `LabPhiMasking` has removed names, dates of birth, addresses and phones, and has cut identifiers to their
  last four characters.
* **Transport.** Each HTTP provider is a typed `HttpClient` behind the standard resilience handler. Cloud APIs use a
  15 s attempt timeout and a 20 s total; Tally uses 60 s. Unsafe methods (POST) are **never retried** at the HTTP
  layer, because a lost response may mean the SMS was already sent or the order already created. The outbox retries
  whole messages instead. Responses are capped at 4 MB. Endpoints must be absolute http(s) URLs without embedded
  credentials, and https is required for every internet provider.

## What other modules resolve

Each abstraction below is **always registered** by `IntegrationsModule` and stays **inactive until an administrator
configures it**. "Not configured" behaves like the platform's own fallback: a failed result, a decline or an empty
answer, never a crash. Consumers that check `GetService<…>() is null` to detect "not configured" never take that
branch while this module is loaded. They get the behaviour in the last column instead.

| Abstraction (Platform) | Consumers | Implementation | Until configured / on failure |
|---|---|---|---|
| `IPaymentGateway` | Billing payment links and portal bill payment, Scheduling portal prepayment, Billing webhook | `PaymentGatewayRouter` → Razorpay / simulator | `CreateOrderAsync` and `CreatePaymentLinkAsync` throw `IntegrationException` (an `InvalidOperationException`) with the reason. Billing turns it into `billing.gateway_unavailable`; Scheduling switches to pay-at-hospital. `GetStatusAsync` returns `Pending` with the reason. `VerifySignature` returns false. The signature convention is HMAC-SHA256 over `"{orderId}\|{paymentId}"` with the key secret; see below. |
| `IPosTerminal` | Billing card/UPI sales | `PosTerminalRouter` → Pine Labs / simulator | A **declined** result with the reason ("Card/UPI POS is not configured…"). Each declined attempt gets a unique `declined-…` transaction id, and `Provider` reports the provider that handled the call (`none`, `pinelabs` or `simulator`), because Billing stores sales under a unique (provider, transaction id) index. |
| `IAccountingExporter` | Billing Tally day book (outbox) and day-book file download | `AccountingExporterRouter` → Tally Prime / simulator | `PushVouchersAsync` returns a failed result ("Tally is not configured…"). Billing records it and the outbox retries per its policy; Billing's `NotConfigured` status is not reached. `PingAsync` returns false. `BuildImportFile` always works (Tally XML) and throws only for a voucher Tally would reject. |
| `IPacsClient` | Diagnostics radiology: PACS reconciliation and viewer links | `PacsClientRouter` → DICOMweb / simulator | `FindStudiesAsync` throws **`HttpRequestException`** with the reason (not configured, disabled, unreachable or HTTP error). Diagnostics reports it as `pacs.unreachable`. `BuildViewerUrl` returns null without a viewer template (`pacs.viewer_not_configured`). The DICOM modality worklist (`/api/v1/radiology/worklist/mwl`) belongs to Diagnostics. |
| `IInstrumentResultSink` (implemented by Diagnostics, scoped) | Consumed by the lab analyser gateway here | `LabMessageProcessor` resolves it in a new scope per message: `ReceiveAsync` for results, `GetOrderForSampleAsync` for ASTM host queries | The gateway is off until `integrations.lab.enabled` is set and instruments are listed. Without a registered sink, results are refused (HL7 `AE`, ASTM NAK) and logged. |
| `IIcdLookup` | Clinical online ICD search (`/api/v1/mrd/icd-codes/online`) | `IcdLookupRouter` → WHO ICD-API | Returns an empty list until configured. ICD-API failures throw `HttpRequestException`, which Clinical reports as `icd.online_failed`. |
| `ISmsSender`, `IWhatsAppSender`, `IEmailSender` | Platform `notify.sms`, `notify.whatsapp` and `notify.email` outbox handlers | `SmsRouter`, `WhatsAppRouter`, `EmailRouter` | `SendResult.Fail("… is not configured. Set it up in Administration → Integrations.")`, the same as the platform's `UnconfiguredChannelSender`. |
| Biometric attendance | HR (`/iclock/*` ZKTeco/eSSL ADMS push endpoints) | None, by design | HR implements the ADMS protocol itself (`AdmsEndpoints`, `BiometricIngestionService`), so there is no Platform abstraction and nothing for Integrations to register. |

## Configuration

Configuration uses the generic settings API (`/api/v1/admin/settings/...`), facility first and then global:

* `integrations.<name>` holds the non-secret JSON.
* `integrations.<name>.secret` holds the credentials, stored encrypted by the settings service.

Secrets are read only from the `.secret` keys. They are never returned by any Integrations endpoint and never logged.
Settings that are malformed or cannot be decrypted produce a staff-facing problem message ("… settings are not valid",
"… credentials could not be read"). The failure affects only that call, never the host.

Background work (the notification outbox) has no signed-in facility, so it uses the **global** rows. Anonymous
webhooks accept `?facility=<id>` to select whose credentials verify them.

| Key | Non-secret settings (defaults) | Secret settings |
|---|---|---|
| `integrations.sms` | `enabled`, `provider` (`msg91` \| `twilio` \| `generic` \| `simulator`), `senderId`, `dltEntityId`, `msg91 { baseUrl, templateIds{dlt→msg91}, textVariable, shortUrl }`, `twilio { baseUrl, accountSid, from, messagingServiceSid, statusCallbackUrl }`, `generic { url, method, contentType, bodyTemplate, headers, apiKeyHeader, apiKeyPrefix, messageIdPath, successContains }`, `webhookAllowedIps[]` | `msg91AuthKey`, `twilioAuthToken`, `genericApiKey`, `webhookToken` |
| `integrations.whatsapp` | `enabled`, `provider` (`meta` \| `simulator`), `graphBaseUrl`, `apiVersion` (`v21.0`), `phoneNumberId`, `businessAccountId`, `languageCodes { "en": "en_US" }`, `maxDocumentMegabytes` (20), `testTemplateName` (`hello_world`), `testTemplateLanguage` | `accessToken`, `appSecret`, `verifyToken` |
| `integrations.email` | `enabled`, `provider` (`smtp` \| `simulator`), `host`, `port` (587), `useStartTls` (true), `fromAddress`, `fromName`, `userName`, `timeoutSeconds` (20), `maxAttachmentMegabytes` (15) | `userName`, `password` |
| `integrations.payment` | `enabled`, `provider` (`razorpay` \| `simulator`), `baseUrl`, `keyId`, `paymentLinkCallbackUrl`, `notifyByRazorpay` (false), `paymentLinkExpiryHours` (72) | `keySecret`, `webhookSecret`, `simulatorSecret` |
| `integrations.pos` | `enabled`, `provider` (`pinelabs` \| `simulator`), `baseUrl`, `uploadPath`, `statusPath`, `cancelPath`, `merchantId`, `userId`, `autoCancelMinutes` (5), `terminals[] { id, name, imei, storePosCode }`, `paymentModes { all, card, upi }`, `pendingResponseCodes` ([1001]), `simulatorApproveAfterSeconds` (8) | `securityToken` |
| `integrations.tally` | `enabled`, `provider` (`tally` \| `simulator`), `url` (`http://localhost:9000`), `companyName`, `ledgerMap { hc360 → tally }`, `costCategory`, `batchSize` (50) | none (Tally's XML port has no authentication) |
| `integrations.pacs` | `enabled`, `provider` (`dicomweb` \| `simulator`), `qidoUrl`, `authType` (`none` \| `basic` \| `bearer`), `userName`, `viewerUrlTemplate` (`{studyUid}`, `{accession}`), `limit` (50), `timeZone` | `password`, `bearerToken` |
| `integrations.icd` | `enabled`, `provider` (`who` \| `disabled`), `tokenUrl`, `apiBaseUrl`, `icd11Release` (`2025-01`), `icd10Release` (`2019`), `language` (`en`), `cacheHours` (24) | `clientId`, `clientSecret` |
| `integrations.lab` (global) | `enabled`, `bindAddress` (`0.0.0.0`), `maxMessageKilobytes` (1024), `idleTimeoutSeconds` (300), `maxConnectionsPerInstrument` (4), `timeZone` | none |
| `integrations.lab.instruments` (global) | array of `{ code, name, protocol (Hl7v2 \| Astm), port, enabled, allowedIps[], encoding (latin1 \| utf-8), sampleIdFields[], hostQuery }` | none |

## Channels

### SMS (INT-SMS)

* **MSG91 (Flow API)**: `POST {baseUrl}/api/v5/flow` with the `authkey` header. The body is `template_id`, `short_url`
  and `recipients[{ mobiles, … variables }]`. DLT requires the approved template, so every message must carry its
  DLT template id. HC360 sends text it has already rendered, so the provider recovers the Flow variables by matching
  that text against the `NotificationTemplate` bodies registered under the same DLT id (`{{name}}` becomes MSG91
  `##name##`). If no template matches, `msg91.textVariable` can carry the whole text as one variable. When that is
  empty too, the send fails with a clear message instead of reaching the wrong template.
* **Twilio**: `POST /2010-04-01/Accounts/{sid}/Messages.json` with basic auth. `statusCallbackUrl` is attached when
  configured.
* **Generic HTTP**: any gateway, configured with `url` and `bodyTemplate` placeholders (`{{to}}`, `{{toDigits}}`,
  `{{to10}}`, `{{text}}`, `{{dltTemplateId}}`, `{{dltEntityId}}`, `{{senderId}}`, `{{reference}}`, `{{apiKey}}`).
  Values are URL- or JSON-encoded to match the target. Only the URL *template* is logged.
* **Delivery receipts** update the platform notification log by `ProviderMessageId` (indexed). Delivered sets
  `Delivered` and `DeliveredAt` and clears the error. Failed sets `Failed` with the provider's reason.
  * MSG91 and generic callbacks must present `?token=<webhookToken>`, compared in constant time, and pass the optional
    IP allow-list.
  * Twilio callbacks must carry a valid `X-Twilio-Signature` (HMAC-SHA1 over the called URL plus the sorted form
    fields).
  * Anything unverified gets 401 and changes nothing.

### WhatsApp (INT-WA)

* Meta Cloud API template messages go to `POST /{version}/{phoneNumberId}/messages`. Parameters are sanitised
  (newlines, tabs and long runs of spaces are rejected by Meta) and the language is mapped through `languageCodes`.
* Documents (reports, bills) are uploaded to `/media` first, then sent by media id. Only PDF, JPEG, PNG, text,
  DOCX and XLSX are accepted, within `maxDocumentMegabytes`, and file names are sanitised.
* Webhook `GET` answers `hub.challenge` only when `hub.verify_token` matches (constant time) and the challenge is a
  plain token.
* Webhook `POST` requires `X-Hub-Signature-256`, an HMAC-SHA256 of the **raw** body with the app secret. Statuses
  update the notification log. Patient messages are stored idempotently in `whatsapp_inbound_messages`, and exact
  `STOP` or `START` style keywords are classified as opt-out or opt-in. Bodies are capped at 256 KB.

### E-mail

SMTP over STARTTLS, normally on port 587. Implicit TLS on 465 is rejected with an explanation, because
`System.Net.Mail` cannot do it. Credentials come only from the secret settings. The asynchronous send is bounded by
`timeoutSeconds`. Recipients are logged masked (`r***@example.com`).

### Payment gateway (INT-PG) and the signature convention

* **Razorpay**: orders (`POST v1/orders`), payment links (`POST v1/payment_links`, not sent by Razorpay unless
  `notifyByRazorpay`), status (`GET v1/orders/{id}/payments` or `v1/payment_links/{id}`) and refunds
  (`POST v1/payments/{id}/refund`). Amounts travel in paise and must be between ₹1 and ₹1,00,00,000. Ids are
  validated (`order_…`, `plink_…`, `pay_…`) before they are put in a URL. When one order has several attempts, a
  captured attempt wins over earlier failures.
* **Every `IPaymentGateway` call names a facility.** A facility may have its own merchant account, and webhooks,
  patient checkouts and background re-checks have no signed-in facility, so callers pass the order's facility. Null
  means the signed-in facility, then the all-facility settings.
* **Channel messages carry the facility too.** `SmsMessage`, `WhatsAppTemplateMessage`, `WhatsAppDocumentMessage` and
  `EmailMessage` have a `FacilityId`, set by the platform's outbox handlers, so queued notifications use that
  facility's provider settings.
* **`IPaymentGateway.VerifySignature(payload, signature, facilityId)`** is a hex HMAC-SHA256, compared in constant time:
  * `"{orderId}|{paymentId}"` (Checkout) and `"{plinkId}|{referenceId}|{status}|{paymentId}"` (payment-link callback)
    are verified with the **key secret**. This is exactly the convention Scheduling's portal prepayment and Billing
    use.
  * A **JSON body** (starts with `{` or `[`) is verified only with the **webhook secret**. Billing's webhook passes
    the raw body with `X-Razorpay-Signature`. A signature a browser legitimately received at checkout therefore can
    never be replayed as a webhook, and the reverse also holds.
  * Verification reads the settings even while the gateway is disabled, so late callbacks still verify.
* `CreateOrderAsync` and `CreatePaymentLinkAsync` throw `IntegrationException`, an `InvalidOperationException` with
  a staff-safe message, when the gateway is off or failing. Scheduling and Billing catch it and fall back to pay at
  the hospital. `GetStatusAsync` and `RefundAsync` report problems in their results instead.
* **Simulator**: orders and links are created already **captured** (`pay_sim…` payment id, `CheckoutKey`
  `"simulator"`). There is no browser checkout, so a simulator order is confirmed through `GetStatusAsync`, which
  reports `Captured`. Simulator signatures use the same payloads, signed with `simulatorSecret`. Without that secret,
  a random per-process key is used, so nobody outside the server can forge a "paid" simulator payment. A portal
  flow that insists on a checkout signature cannot complete in simulator mode unless test tooling signs with a
  configured `simulatorSecret`.

### Card/UPI POS (INT-POS)

Pine Labs Plutus Cloud-style push-to-terminal:

* **Upload**: the billed transaction goes to the terminal. The body carries the security token and is never logged.
* **Poll**: status codes are mapped, and `pendingResponseCodes` means the customer is still paying.
* **Cancel**: cancels the transaction with its original amount.

Transaction ids encode what status and cancel need (`OPD-1:987654:125075`, meaning terminal, provider reference and
paise), so no extra table is required. A sale that never reached a terminal gets a unique `declined-…` id. A lost
status or cancel response reports "still waiting" rather than a decline. Only the last four card digits are kept. The admin "test" checks configuration only, because pushing a sale
would prompt a customer at a counter.

### Tally Prime (INT-TALLY)

* Vouchers are sent as `ENVELOPE / HEADER / TALLYREQUEST = Import Data` with `SVCURRENTCOMPANY`, one
  `TALLYMESSAGE / VOUCHER` per voucher (`REMOTEID="HC360-<reference>"`, `VCHTYPE`, `ACTION="Create"`,
  `OBJVIEW="Accounting Voucher View"`), and `ALLLEDGERENTRIES.LIST` lines, built with LINQ to XML so every value is
  escaped.
* Sign convention: a debit is `ISDEEMEDPOSITIVE=Yes` with a negative `AMOUNT`, a credit is `No` with a positive one.
* Cost centres go under `CATEGORYALLOCATIONS.LIST`. Ledger names are mapped with `ledgerMap`.
* Every voucher is validated first: balanced, positive amounts, ledgers present, at least two lines. One bad voucher
  is reported by number instead of Tally's generic totals error.
* The import goes in batches of `batchSize`. `REMOTEID` makes a retried batch alter rather than duplicate.
* The response counters `CREATED`, `ALTERED` and `ERRORS`, plus `LINEERROR` texts, are parsed from both the classic
  and the Prime layout. Tally's illegal XML character references are tolerated and DTDs are prohibited.
* `PingAsync` asks for the list of open companies and checks the configured one.
* `BuildImportFile` produces the same envelope for manual import (Tally → Import → Vouchers) and works even while the
  integration is disabled.

### PACS (INT-PACS)

* QIDO-RS `GET {qidoUrl}/studies?AccessionNumber&PatientID&StudyDate=from-to&ModalitiesInStudy&includefield…&limit`.
  DICOM wildcards `*` and `?` are stripped, so one lookup can never scan the whole archive.
* DICOM JSON is parsed by tag: PN `SHARMA^RAVI` becomes "SHARMA RAVI", IS values may arrive as numbers or strings,
  and date and time are read with offset `00080201` or the PACS zone.
* Authentication is none, basic or bearer.
* `FindStudiesAsync` raises every failure as `HttpRequestException` with a staff-facing message, which Diagnostics
  handles as "PACS could not be reached". That covers not configured, disabled, unreachable and HTTP errors.
* `BuildViewerUrl` fills `viewerUrlTemplate` with URL-encoded values and returns only http(s) URLs. It works even
  while the integration is disabled.
* Only the query shape, the masked patient id and counts are logged.

### Laboratory analysers (INT-LAB)

`LabGatewayHostedService` runs only where `PlatformOptions.RunsJobs` is true. Every minute it reconciles one TCP
listener per enabled instrument with the settings: it starts new listeners, restarts changed ones, stops removed
ones and retries ports that could not be bound.

* **Connection limits**:
  * Connections from outside `allowedIps` are closed at once. Use the analyser's fixed IP.
  * At most `maxConnectionsPerInstrument` connections are served at a time.
  * Idle connections close after `idleTimeoutSeconds`.
  * A message larger than `maxMessageKilobytes` is refused: NAK for ASTM, connection closed for MLLP.
* **HL7 v2 over MLLP** (`0x0B … 0x1C 0x0D`): ORU^R01/R30/R32 and OUL^R21–R24 are accepted.
  * Samples are identified by `sampleIdFields`, default SPM-2, SAC-3, OBR-3, OBR-2.
  * OBX handling:
    * NM, ST and TX values are read as text.
    * CE, CWE and CNE use the coded value's text.
    * SN is joined, for example `<5` or `1:160`.
    * ED, RP and AD values, and D, W and X statuses, are skipped.
  * Results are timed from OBX-14, falling back to OBR-7 and then MSH-7, interpreted in the lab's zone.
  * Acknowledgements:
    * `MSA|AA` once the LIS has stored the results.
    * `MSA|AE` when it could not, so the analyser keeps and resends them.
    * `MSA|AR` for other message types or text that cannot be read.
  * The ACK swaps sender and receiver and echoes the version.
* **ASTM E1381/E1394**:
  * Link layer: ENQ/ACK handshake, frame-number and checksum checks (NAK on error), duplicate frames re-ACKed
    without being applied twice, ETB continuation, and E1381's 240-character frames with frame numbers modulo 8.
  * The frame completing a message (the one with the L record) is ACKed **only after** the LIS stored the results.
    If the LIS is unavailable or rejects a sample, that frame is NAKed and the analyser retransmits it, so a result
    is never acknowledged and then lost.
  * Records H/P/O/R/C/L use delimiters from the header. The sample comes from O.3, falling back to O.4. Results with
    status X or I are skipped.
  * **Host query**: Q records ask the LIS through `GetOrderForSampleAsync`. HC360 answers after the analyser's EOT
    with ENQ, then H/P/O records (O.5 `^^^code` repeats, O.26 `Q`) and `L|1|F`, or `L|1|I` when no orders exist.
    The instrument has priority on line contention. NAKed frames are retransmitted up to six times.
* **LIS bridge**: every message gets its own DI scope and resolves `IInstrumentResultSink`, which the Diagnostics
  module implements. If no sink is registered, results are refused (AE or NAK) and logged, never dropped silently.
* Live listener status (state, connections, accepted and refused counts, last error) appears on the integration
  dashboard.

### Barcode labels (INT-BAR)

`POST /api/v1/integrations/labels` returns raw **ZPL** (Zebra) or **TSPL** (TSC) text. The Desktop bridge
(`printer.printRaw`) prints it; the server never prints.

* Geometry is for 203 dpi (8 dots/mm): 50×25 mm is `^PW400 ^LL200`.
* **Code 128**: printable ASCII, at most 48 characters, and the bar width is chosen to fit the label.
* **QR**: model 2, level Q, at most 400 bytes, with the smallest version that fits.
* Up to 6 text lines of 80 characters. Control characters are removed.
* In ZPL, all field data goes through `^FH`, and `^`, `~`, `_` and non-ASCII characters are hex-escaped, so label
  text can never inject printer commands. TSPL quotes are escaped as `\["]`.

### ICD lookup (INT-ICD) and the Clinical module

* WHO ICD-API v2 with OAuth2 client credentials (`scope=icdapi_access`). Tokens are cached until one minute before
  they expire, and a semaphore prevents a stampede of token requests.
* Every call sends the `API-Version: v2` and `Accept-Language` headers.
* **ICD-11 MMS search**: `icd/release/11/{release}/mms/search?q=…&flatResults=true`. Highlight markup is removed and
  entities without a code are skipped.
* **ICD-10**: looks up codes and their children by code (`icd/release/10/{release}/{code}`). Child links are reduced
  to safe paths on the configured host. The ICD-API has **no ICD-10 free-text search**; that stays with Clinical's
  local catalogue.
* **Release labels** accepted by `IIcdLookup.SearchAsync`:
  * `ICD-11 MMS`, `11` or empty searches ICD-11.
  * `ICD-10 2019`, `ICD-10`, `icd10` or `10` looks up ICD-10 by code.
  * `ICD-10-PCS` returns nothing.
* Answers are cached for `cacheHours`. Failures are not cached.
* **Registration.** `IIcdLookup` is **always registered**, so Clinical's 422 `icd.online_unavailable` path, which is
  taken only when no implementation exists, is not reached. How the lookup behaves:
  * It stays **inactive until configured**: disabled, not configured or `provider: "disabled"` returns an empty list.
  * When the ICD-API fails (network, timeout, 4xx/5xx, rejected credentials), it throws `HttpRequestException` with a
    staff-facing message. Clinical already maps that to 422 `icd.online_failed`, "could not be reached".
  * Distinguishing "not configured" from "no matches" would need a Platform contract addition such as
    `IIcdLookup.IsAvailableAsync`.

## Endpoints

| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/api/v1/admin/integrations` | `admin.integrations.manage` | → `IntegrationStatusDto[]`: provider, enabled/configured, health (`healthy`, `degraded`, `failing`, `idle`, `disabled` or `not_configured`), last success/failure, failures in 24 h, lab listener status |
| POST | `/api/v1/admin/integrations/{name}/test` | `admin.integrations.manage` | `TestIntegrationRequest { to, message, dltTemplateId, templateName, languageCode }` → `IntegrationTestResult`. Sends a real test SMS, WhatsApp or e-mail, creates a ₹1 order, pings Tally, PACS or ICD, and checks lab and label configuration. Audited as `integration.tested`. |
| GET | `/api/v1/admin/integrations/logs` | `admin.integrations.manage` | paging + `integration, success, from, to, search` → `PagedResult<IntegrationLogDto>` |
| GET | `/api/v1/admin/integrations/simulator/messages` | `admin.integrations.manage` | paging + `channel` → simulated SMS, WhatsApp, e-mail and Tally deliveries |
| GET | `/api/v1/admin/integrations/simulator/transactions` | `admin.integrations.manage` | paging + `kind` → simulated payment orders and links, and POS sales |
| GET | `/api/v1/admin/integrations/whatsapp/inbound` | `admin.integrations.manage` | paging + `keyword` → patient messages (sender masked) |
| POST | `/api/v1/integrations/labels` | `integrations.labels.print` | `PrintLabelRequest { value, symbology, textLines, widthMm, heightMm, copies, language }` → `PrintLabelResponse { language, content, copies }` |
| POST | `/api/v1/integrations/webhooks/sms/msg91` | anonymous, token | delivery reports (JSON or form `data`) |
| POST | `/api/v1/integrations/webhooks/sms/twilio` | anonymous, `X-Twilio-Signature` | status callbacks (form) |
| GET, POST | `/api/v1/integrations/webhooks/sms/generic` | anonymous, token | `{ id\|messageId\|msgid…, status }` |
| GET | `/api/v1/integrations/webhooks/whatsapp` | anonymous, verify token | Meta subscription check |
| POST | `/api/v1/integrations/webhooks/whatsapp` | anonymous, `X-Hub-Signature-256` | statuses and inbound messages |

Webhooks use the platform's `public` rate-limit policy, read at most 256 KB and accept `?facility=<id>`. Integration
settings themselves are edited through the platform settings API with `admin.integrations.manage`.

`IntegrationsModule` declares `integrations.labels.print` (all staff roles). Administration uses the platform's
`admin.integrations.manage`.

## DI registrations (`IntegrationsServiceCollectionExtensions.AddIntegrations`)

* **Scoped helpers**: `IntegrationSettingsReader`, `IntegrationTrace`, `SimulatorInbox`, `NotificationLogUpdater`,
  `WhatsAppInboundStore`, `IntegrationStatusService`, `IntegrationTestService`.
* **Typed HTTP clients** with the standard resilience handler: `Msg91SmsProvider`, `TwilioSmsProvider`,
  `GenericHttpSmsProvider`, `MetaWhatsAppProvider`, `RazorpayGateway`, `PineLabsPosTerminal`, `DicomWebPacsProvider`
  and `WhoIcdApiClient` use a 15 s attempt timeout and 20 s total. `TallyPrimeExporter` uses 60 s.
* **Providers** (scoped, keyed by `Key`):
  * `ISmsProvider`: msg91, twilio, generic, simulator.
  * `IWhatsAppProvider`: meta, simulator.
  * `IEmailProvider`: smtp, simulator.
  * `IPaymentProvider`: razorpay, simulator.
  * `IPosProvider`: pinelabs, simulator.
  * `IAccountingProvider`: tally, simulator.
  * `IPacsProvider`: dicomweb, simulator.
* **Platform contracts** (scoped routers):
  * `ISmsSender` → `SmsRouter` (with `ISmsTemplateCatalog` → `SmsTemplateCatalog`)
  * `IWhatsAppSender` → `WhatsAppRouter`
  * `IEmailSender` → `EmailRouter`
  * `IPaymentGateway` → `PaymentGatewayRouter`
  * `IPosTerminal` → `PosTerminalRouter`
  * `IAccountingExporter` → `AccountingExporterRouter`
  * `IPacsClient` → `PacsClientRouter`
  * `IIcdLookup` → `IcdLookupRouter`
  * `ILabelPrinterService` → `LabelPrinterService`, a singleton
* **Singletons**: `SimulatorSigningKey`, `IcdTokenCache`, `LabGatewayState`, and `ILabMessageProcessor` →
  `LabMessageProcessor`.
* **Hosted service**: `LabGatewayHostedService`.

## Security summary

* Every inbound webhook is verified with a token, signature or verify token, all compared in constant time
  (`CryptographicOperations.FixedTimeEquals`; tokens are compared by SHA-256 so their length does not leak). Optional
  IP allow-lists apply. Bodies are capped, and the anonymous rate limit applies.
* Secrets are read only from `*.secret` settings and never logged or returned. Log summaries are built by the
  adapters, and patient data in analyser messages is masked.
* Every network call has a timeout. The TCP listeners limit message size, connection count and idle time and apply
  per-instrument allow-lists.
* External ids and paths (Razorpay ids, POS transaction ids, WhatsApp phone-number id and API version, ICD child
  links, PACS viewer templates) are validated before they are used in URLs.
* Queries order only by timestamps. Enum columns (stored as text) are only compared for equality.

## Not implemented here

* **Biometric attendance**: by design there is no biometric abstraction. HR receives ZKTeco/eSSL ADMS pushes on
  `/iclock/*` itself, so no adapter lives here.
* **HL7 host query** (QRY^Q02 / QBP): not implemented. The ASTM host query is.
* **ABDM (HIP/HIU)** and a **FHIR R4 facade** are out of scope for this project. The FHIR facade is planned at
  integration level.
* **ICD-10 free-text search online**: the WHO ICD-API does not support it, so the local catalogue answers instead.

## Database

The tables `integrations.simulator_messages`, `integrations.simulator_transactions` and
`integrations.whatsapp_inbound_messages` are configured in `Persistence/IntegrationsConfigurations.cs`. The EF
migration is created centrally. They have unique `external_id` and `provider_message_id` indexes, and all three
entities are `[NotAudited]` because the integration log already traces them.
