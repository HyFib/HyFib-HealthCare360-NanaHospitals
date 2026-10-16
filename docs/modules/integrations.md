# Integrations

Adapters between HC360 and external systems: SMS, WhatsApp, e-mail, payment gateway, card/UPI POS, Tally Prime,
PACS (DICOMweb), the DICOM modality worklist server, laboratory analysers (HL7 v2 / ASTM), barcode label printers and
the WHO ICD-API. It also holds the server's PDF renderer, which drives headless Microsoft Edge.

| | |
|---|---|
| Project | `src/server/HyFib.HealthCare360.Integrations` (module key `integrations`, registered by `IntegrationsModule`) |
| Schema | `integrations`: `simulator_messages`, `simulator_transactions`, `whatsapp_inbound_messages`, `lab_order_downloads` (the integration log itself is the platform's `integration_logs`) |
| Requirements | SRS §5: INT-SMS, INT-WA, INT-PG, INT-POS, INT-TALLY, INT-PACS, INT-LAB, INT-BAR, INT-ICD; SEC-10 (PHI in logs) |
| Tests | `tests/HyFib.HealthCare360.Integrations.Tests`: about 800 tests covering signatures, templates, every provider against a fake HTTP handler, the routers other modules resolve (payment, POS, PACS, ICD), webhook parsers, WhatsApp STOP/START handling, Tally XML, DICOM JSON, QIDO-RS, WADO-RS and STOW-RS against a fake PACS (with the DICOM rewriting of objects sent), the modality worklist server answering fo-dicom's own client on a local port, worklist matching and settings, the HL7/MLLP and ASTM E1381/E1394 sessions over an in-memory socket (host queries, order download, contention, size limits), the lab agent relay over an in-memory WebSocket, PHI masking, labels, validators and DI wiring. `tests/HyFib.HealthCare360.LabAgent.Tests` runs the lab agent against the server's relay end to end (Windows). `tests/HyFib.HealthCare360.IntegrationTests` (`LabAnalyserTests`, `LabConnectionTests`) covers the connection rules, facility scoping, the relay endpoint and the order queue's claims against PostgreSQL, and `CardiologyPacsTests` sends an echo's DICOM files through the whole server to the PACS simulator |

## How it fits together

* **Platform contracts, one router each.** Other modules depend only on the contracts in
  `Platform/Integrations/IntegrationAbstractions.cs` (`ISmsSender`, `IWhatsAppSender`, `IEmailSender`,
  `IPaymentGateway`, `IPosTerminal`, `IAttendanceTerminalClient`, `IAccountingExporter`, `IPacsClient`, `IIcdLookup`,
  `ILabelPrinterService`).
  Integrations registers a **router** for each of them. On every call the router reads the facility's settings and
  delegates to the configured provider. Switching provider, or turning an integration off, takes effect within a
  minute (the settings cache) and needs no restart. In the other direction, the listeners here call contracts that
  Diagnostics implements: `IInstrumentResultSink` for analyser results and `IModalityWorklistSource` for the modality
  worklist.
* **Registration order.** The routers are registered with plain `Add*`. The platform adds its "not configured"
  fallbacks later with `TryAdd*`, so when this module is loaded the routers win. Without it, the fallbacks apply.
* **Simulator for every channel.** Setting `"provider": "simulator"` makes the integration work end to end with no
  credentials. SMS, WhatsApp, e-mail and Tally write to `integrations.simulator_messages`. Payment orders and links
  are captured immediately and POS sales are approved after a few seconds, all stored in
  `integrations.simulator_transactions`, with the punches HR makes on a training attendance terminal. Administrators browse these under
  `/api/v1/admin/integrations/simulator/*`. Payment links point to the reserved host `pay.simulator.invalid`. The
  others have their own: PACS answers searches and stores from a stateless training archive; ICD lookup answers from
  a short built-in list of common codes (`SimulatorIcdCatalogue`), with no internet; barcode labels
  (`integrations.labels`) are written as usual but kept in `simulator_messages` (channel `Labels`) instead of being
  printed, and the staff app shows each label in the print dialog; a laboratory analyser whose connection is
  `Simulator` is the training analyser, whose results are entered on screen (see *Training analyser* below).
* **Integration log.** Every external call, inbound webhook and analyser message writes one row through the platform
  `IIntegrationLogger`, via the internal `IntegrationTrace` helper. Rows record integration, provider, operation,
  direction, success, duration, reference and summaries. Adapters build their own summaries: masked recipient,
  template name, counts and status codes. They never log message bodies, patient names or credentials. The platform
  logger then masks phone numbers, e-mails and secrets as a second line of defence. Raw analyser messages are logged
  only after `LabPhiMasking` has removed names, dates of birth, addresses and phones, and has cut identifiers to their
  last four characters.
* **Transport.** Each HTTP provider is a typed `HttpClient` behind the standard resilience handler. Cloud APIs use a
  15 s attempt timeout and a 20 s total; Tally uses 20 s for both, because the day book posts from an outbox message
  that has 30 s in all (see [Tally Prime](#tally-prime-int-tally)). Unsafe methods (POST) are **never retried** at the HTTP
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
| `IPaymentGateway` | Billing payment links and portal bill payment, Scheduling portal prepayment, Billing webhook, Billing gateway reconciliation (settlements, refund status) | `PaymentGatewayRouter` → Razorpay / simulator | `CreateOrderAsync` and `CreatePaymentLinkAsync` throw `IntegrationException` (an `InvalidOperationException`) with the reason. Billing turns it into `billing.gateway_unavailable`; Scheduling switches to pay-at-hospital. `GetStatusAsync` returns `Pending` with the reason. `VerifySignature` returns false. `GetSettlementsAsync` returns a report marked unavailable with the reason; `GetRefundStatusAsync` returns `Pending` with the reason. Settlements and refund status are read even while online payments are switched off, so earlier payments are still reconciled. The signature convention is HMAC-SHA256 over `"{orderId}\|{paymentId}"` with the key secret; see below. |
| `IPosTerminal` | Billing card/UPI sales | `PosTerminalRouter` → Pine Labs / simulator | A **declined** result with the reason ("Card/UPI POS is not configured…"). Each declined attempt gets a unique `declined-…` transaction id, and `Provider` reports the provider that handled the call (`none`, `pinelabs` or `simulator`), because Billing stores sales under a unique (provider, transaction id) index. |
| `IAccountingExporter` | Billing Tally day book (outbox), its read-back check and the day-book file download | `AccountingExporterRouter` → Tally Prime / simulator | `GetConnectionAsync` says Not configured (no settings, or posting switched off) or Invalid (settings that cannot be used). `PushVouchersAsync` then returns a result with `NotConfigured` set and every voucher not sent, and Billing marks the day Not configured instead of retrying; Invalid settings fail the day until corrected. `ReadBackAsync` returns an unsuccessful result with the reason. `PingAsync` returns false. `BuildImportFile` always works (Tally XML) and throws only for a voucher Tally would reject. |
| `IPacsClient` | Diagnostics radiology: linking studies with PACS (by hand and by `PacsLinkJob`), the study's series and images, and viewer links. Diagnostics cardiology: sending a study's DICOM device files (`StoreAsync`) and its viewer link | `PacsClientRouter` → DICOMweb / simulator | `FindStudiesAsync`, `FindSeriesAsync`, `FindInstancesAsync` and `RetrieveImageAsync` throw **`HttpRequestException`** with a staff-facing reason: not configured, disabled, unreachable, an HTTP error, a UID that is not a DICOM UID, or an answer that is not a JPEG or PNG image within the size limit. Diagnostics reports it as `pacs.unreachable`. `StoreAsync` throws only when PACS is not configured or disabled, or the study has no valid UID or patient ID (`pacs.unavailable`); a file the PACS refuses or cannot be reached for gets its own `PacsStoreOutcome`. `StoreDestinationAsync` names where objects go now (the provider and the STOW-RS root, or `simulator`), so a caller can tell an outcome from another PACS; it throws like `StoreAsync`. Every call names the facility whose settings apply (null: the signed-in facility), because background linking has none. `BuildViewerUrl` returns null without a viewer template (`pacs.viewer_not_configured`). |
| `IModalityWorklistSource` (implemented by Diagnostics, scoped) | Consumed by the modality worklist server here, and by Diagnostics' own `/api/v1/radiology/worklist/mwl` | `ModalityWorklistSource` in Diagnostics, resolved in a new scope for every C-FIND | The server is off until `integrations.mwl.enabled` is set and modalities are listed. Without a registered source, every query is answered "unable to process" and logged. |
| `IInstrumentResultSink` (implemented by Diagnostics, scoped) | Consumed by the lab analyser gateway here | `LabMessageProcessor` resolves it in a new scope per message: `ReceiveAsync` for results, `GetOrderForSampleAsync` for ASTM host queries | The gateway is off until `integrations.lab.enabled` is set and instruments are listed. Without a registered sink, results are refused (HL7 `AE`, ASTM NAK) and logged. |
| `IIcdLookup` | Clinical online ICD search (`/api/v1/mrd/icd-codes/online`) | `IcdLookupRouter` → WHO ICD-API | Returns an empty list until configured. ICD-API failures throw `HttpRequestException`, which Clinical reports as `icd.online_failed`. |
| `ISmsSender`, `IWhatsAppSender`, `IEmailSender` | Platform `notify.sms`, `notify.whatsapp` and `notify.email` outbox handlers; the WhatsApp STOP/START confirmation (`IWhatsAppSender.SendTextAsync`) | `SmsRouter`, `WhatsAppRouter`, `EmailRouter` | `SendResult.Fail("… is not configured. Set it up in Administration → Integrations.")`, the same as the platform's `UnconfiguredChannelSender`. |
| `IPdfRenderer` | Document endpoints of Diagnostics, Clinical, Billing and Analytics (`…/pdf`, MIS export as PDF), and the notification document sources that attach reports to e-mails and WhatsApp messages | `EdgePdfRenderer` (singleton) → headless Microsoft Edge, else Google Chrome | Without a browser, `Status` says why and `RenderAsync` throws `PdfRenderException` with a message for staff. Document endpoints answer 503 `pdf.unavailable`, and the administrator dashboard shows the `pdf.unavailable` warning. Without this module, the platform's `UnavailablePdfRenderer` does the same. |
| `IAttendanceTerminalClient` | HR: reading attendance terminals that cannot push (`BiometricPullJob`, Read now, set the clock, training punches) | `AttendanceTerminalRouter` → ZKTeco (TCP 4370) / simulator | `ReadAsync` returns an unsuccessful read with the reason ("Biometric attendance is not configured…", "…is disabled"); `SetClockAsync` and `PunchOnTrainingTerminalAsync` return the reason. `ReadingAsync` tells HR the provider, or why there is none. Terminals that push (ADMS on `/iclock/*`, the JSON punch API) do not use it: HR receives those itself. |

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
| `integrations.payment` | `enabled`, `provider` (`razorpay` \| `simulator`), `baseUrl`, `keyId`, `paymentLinkCallbackUrl` (`https://<portal address>/portal/payment-return`), `notifyByRazorpay` (false), `paymentLinkExpiryHours` (72), `simulatorSettlementDays` (1; 0 to 7) | `keySecret`, `webhookSecret`, `simulatorSecret` |
| `integrations.pos` | `enabled`, `provider` (`pinelabs` \| `simulator`), `baseUrl`, `uploadPath`, `statusPath`, `cancelPath`, `merchantId`, `userId`, `autoCancelMinutes` (5), `terminals[] { id, name, imei, storePosCode }`, `paymentModes { all, card, upi }`, `pendingResponseCodes` ([1001]), `simulatorApproveAfterSeconds` (8) | `securityToken` |
| `integrations.tally` | `enabled`, `provider` (`tally` \| `simulator`), `url` (`http://localhost:9000`), `companyName`, `ledgerMap { hc360 → tally }`, `costCategory`, `batchSize` (50) | none (Tally's XML port has no authentication) |
| `integrations.pacs` | `enabled`, `provider` (`dicomweb` \| `simulator`), `qidoUrl` (the DICOMweb root), `wadoUrl` (WADO-RS root when it differs; empty means `qidoUrl`), `stowUrl` (STOW-RS root when it differs; empty means `qidoUrl`), `authType` (`none` \| `basic` \| `bearer`), `userName`, `viewerUrlTemplate` (`{studyUid}`, `{accession}`), `limit` (50, at most 500 studies per search), `timeZone` (`Asia/Kolkata`, for study times sent without an offset) | `password`, `bearerToken` |
| `integrations.mwl` (global) | `enabled`, `bindAddress` (`0.0.0.0`), `port` (11112), `aeTitle` (`HC360MWL`), `characterSet` (`ISO_IR 100`; `ISO_IR 192` or empty), `maxAssociations` (10, 1–64), `idleTimeoutSeconds` (60, 10–3600), `daysBack` (3, 0–30), `daysAhead` (1, 0–30), `maxResults` (500, 1–1000), `modalities[] { aeTitle, name, allowedIps[], facility, modalities[], enabled }` | none |
| `integrations.biometric` | `enabled`, `provider` (`zkteco` \| `simulator`), `timeoutSeconds` (30; 5 to 300, for connecting and reading a terminal) | `commKey` (the terminals' COMM Key, 0 to 999999; empty when they have none) |
| `integrations.labels` | `provider` (`printers`, the default: each workstation's printers \| `simulator`) | none |
| `integrations.icd` | `enabled`, `provider` (`who` \| `simulator` \| `disabled`), `tokenUrl`, `apiBaseUrl`, `icd11Release` (`2025-01`), `icd10Release` (`2019`), `language` (`en`), `cacheHours` (24) | `clientId`, `clientSecret` |
| `integrations.lab` (global) | `enabled`, `bindAddress` (`0.0.0.0`), `maxMessageKilobytes` (1024), `idleTimeoutSeconds` (300), `maxConnectionsPerInstrument` (4), `timeZone` | none |
| `integrations.lab.instruments` (global) | array of `{ code, name, protocol (Hl7v2 \| Astm), transport (Tcp \| Serial \| Simulator), port, enabled, allowedIps[], encoding (latin1 \| utf-8), sampleIdFields[], hostQuery }` | none |

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
* **Template document headers.** A free-form document reaches the person only within 24 hours of their last message,
  so a report goes out in a template instead. When a template carries a `HeaderDocument` (the notification names a
  document, and the HC360 template is marked `whatsAppDocumentHeader`), the file is uploaded first and sent as the
  template's `header` component (`type: "document"`, media id and file name), followed by the `body` parameters. The
  template must be approved in WhatsApp Manager with a document header, because Meta refuses a header the template
  does not have, and a template that has one without it. So a message that carries no document is not sent with a
  header template: its log is marked Skipped with the reason, and it is not retried. The log records the file's
  size, never its content.
* Webhook `GET` answers `hub.challenge` only when `hub.verify_token` matches (constant time) and the challenge is a
  plain token.
* Webhook `POST` requires `X-Hub-Signature-256`, an HMAC-SHA256 of the **raw** body with the app secret. Statuses
  update the notification log. Patient messages are stored idempotently in `whatsapp_inbound_messages`, and exact
  `STOP` or `START` style keywords are classified as opt-out or opt-in. Bodies are capped at 256 KB.
* Free-form **text** messages (`IWhatsAppSender.SendTextAsync`, `type: "text"`, 1–4096 characters, no link preview)
  are accepted by Meta only within 24 hours of the person's last message, so they are used only for replies. The
  simulator records them like templates. Only the masked recipient and the text length are logged.

#### STOP and START replies

A STOP stops every non-essential WhatsApp message to the sender's number, whichever module queued it; START resumes
them. SMS is a separate channel and is not affected.

* **Acted on once.** `WhatsAppInboundStore` acts on a keyword only when its message is stored for the first time, in
  the same transaction. A redelivered webhook finds the provider message id already stored and changes nothing. A
  concurrent redelivery hits the unique `provider_message_id` index, rolls back and gets 200, because the other
  delivery acted. Any other conflict fails the request, so that Meta delivers the webhook again.
* **Latest reply wins.** Meta can deliver late and out of order, so for each number only its most recent reply
  counts (WhatsApp's timestamp, else the time it arrived). A reply older than one already stored from the number, or
  than another in the same batch, is kept for the inbox as `Superseded` and changes nothing (`WhatsAppReplies`).
* **What a reply does.** The message raises the platform integration event `WhatsAppOptOutChanged(phone, optedOut,
  repliedAt, providerMessageId, facilityId)`:
  * the platform records the number's opt-out in `platform.whatsapp_opt_outs`, which the `notify.whatsapp` outbox
    handler checks before every send (see [ARCHITECTURE.md §10](../architecture/ARCHITECTURE.md#10-integration-architecture));
  * Engagement withdraws, or gives back, the marketing consent of the patients registered with the number (see
    [engagement.md](engagement.md#automation-domain-event-handlers-run-inside-the-raisers-transaction)).
* **Recorded on the message.** `keyword_action` is `OptedOut`, `OptedIn`, `Superseded` or `None` (no keyword, or a
  sender number that could not be read). It is empty for replies stored before HC360 acted on keywords: at start-up
  `WhatsAppReplyBacklog` (a seeder, order 500) acts on those once, by the same latest-reply rule and without a
  confirmation. A failure there is logged and retried at the next start; it never stops HC360.
* **Confirmation.** While WhatsApp still allows a free-form reply (23 hours after the patient's message, a margin
  inside Meta's 24-hour window), the store queues a `whatsapp.reply_confirmation` outbox message that expires with the
  window. `WhatsAppReplyConfirmationHandler` sends it with `SendTextAsync`, straight to the sender and not through the
  notification pipeline, so the opt-out does not stop it. The facility is the webhook's `?facility=`, else the first
  facility:
  * STOP: "You won't get WhatsApp messages or offers from {hospital} any more, except sign-in codes. Reply START to
    get them again."
  * START: "You'll get WhatsApp messages from {hospital} again. Reply STOP to stop them."
* **Group-wide.** The opt-out applies to the number across the hospital group, whichever facility's WhatsApp number
  received the reply.
* The administrator's "send a test" is not an opt-out-aware message: it goes to the number the administrator types.

### E-mail

SMTP over STARTTLS, normally on port 587. Implicit TLS on 465 is rejected with an explanation, because
`System.Net.Mail` cannot do it. Credentials come only from the secret settings. The asynchronous send is bounded by
`timeoutSeconds`. Recipients are logged masked (`r***@example.com`). Attachments come from scheduled MIS reports, and
from notifications that name a document, such as the PDF of a released report. Together they must fit
`maxAttachmentMegabytes`.

### PDF renderer (server documents)

`EdgePdfRenderer` turns the documents the server lays out in HTML (reports, prescriptions, discharge summaries, bills,
MIS reports) into PDFs, as ADR-009 decided: no licensed PDF library, just the Microsoft Edge that every Windows 10/11
and Windows Server 2022 machine has.

* **One browser, started on demand.** The first PDF starts headless Edge (about a second). It then stays running and
  makes each PDF in a pooled tab in about a third of a second. It closes after `HealthCare360:Pdf:IdleMinutes` (10)
  without work, and is replaced after `RecycleAfter` (500) PDFs, which also applies Edge updates. A tab is reused for at
  most 50 PDFs. `MaxConcurrent` (2) PDFs are made at once. A PDF that takes longer than `TimeoutSeconds` (30) is
  abandoned, and that browser is replaced.
* **Private control channel.** The browser is driven over the DevTools protocol on a pair of anonymous pipes
  (`--remote-debugging-pipe` with `--remote-debugging-io-pipes`), never a TCP port, so no other program on the server can
  take control of it.
* **Locked-down rendering.** Each PDF is printed in a tab of its own browser context, with scripts switched off
  (`Emulation.setScriptExecutionDisabled`) and every http, https, ws, wss, ftp and file request blocked
  (`Network.setBlockedURLs`). The document's own content security policy forbids scripts and network access a second
  time. Images must be `data:` URIs: logos and signatures are embedded by the platform's `DocumentAssets`.
* **No leftovers.** The browser runs in a Windows job object that ends it and its child processes when the server
  stops, even after a crash. It starts inside the job, because Edge starts its crash handler at once; a browser put in
  the job after it started could leave that process outside. Its profile lives in
  `<data root>\temp\pdf\<server process id>-<random>`. Closing the browser waits until every one of its processes has
  exited, so the profile's files are free, then deletes it; a later start deletes any profile whose server process is
  gone. Extensions, sync, background networking, crash uploads and pings are switched off. The sandbox stays on.
* **Finding the browser.** `HealthCare360:Pdf:BrowserPath` if set. Otherwise Edge from Program Files or its App Paths
  registry entry, then Google Chrome. Without one, `Status` explains what to install.

### Payment gateway (INT-PG) and the signature convention

* **Razorpay**: orders (`POST v1/orders`), payment links (`POST v1/payment_links`, not sent by Razorpay unless
  `notifyByRazorpay`), status (`GET v1/orders/{id}/payments` or `v1/payment_links/{id}`) and refunds
  (`POST v1/payments/{id}/refund`). Amounts travel in paise and must be between ₹1 and ₹1,00,00,000. Ids are
  validated (`order_…`, `plink_…`, `pay_…`, `rfnd_…`, `setl_…`) before they are put in a URL. When one order has
  several attempts, a captured attempt wins over earlier failures.
  * **Status** reports when the payment was taken (`PaidAt`, the captured attempt's `created_at`), which Billing uses
    as the receipt's time. A link Razorpay reports `expired` or `cancelled` is `GatewayPaymentStatus.Expired` or
    `Cancelled`, unless a payment was captured just before it closed, which is still reported as captured.
  * **Settlements** (`GetSettlementsAsync`): every page of `GET v1/settlements/recon/combined?year&month&day`
    (1,000 items a page, at most 100 pages), grouped by `settlement_id`, each completed with its header from
    `GET v1/settlements/{id}` (status, amount paid to the bank, settlement-level fees and tax, UTR, time). Items without
    a settlement id are not settled yet and are skipped. All or nothing: a failed call fails the day, so a half-read
    settlement is never stored. From each item only the id, type, the payment a refund came from, the order, amount,
    fee (GST included), GST, credit, debit, method and times are kept; the card network, issuer and type, the
    description and the notes are left behind.
  * **Refunds** carry the caller's idempotency key (`RefundAsync(…, idempotencyKey, …)`, at most 40 letters and
    digits) in the `X-Refund-Idempotency` header and the refund's `receipt`, so Razorpay answers a repeated request,
    such as a retry after a timeout or a second click, with the refund it already made.
  * **Refund status** (`GetRefundStatusAsync`): `GET v1/refunds/{id}` → `pending`, `processed` or `failed`, with the
    bank's reference (`acquirer_data.arn`, else `rrn` or `utr`). Normal-speed refunds take 5 to 7 working days.
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
  * Refunds are kept as their own simulator transactions (kind `PaymentRefund`, the refund id, the payment id in
    `reference`) and are processed at once. With an idempotency key the refund id is derived from the payment and the
    key, and a refund id already kept is not refunded again, as Razorpay does.
  * **Settlements** work like a real gateway's, so the reconciliation can be practised: with
    `simulatorSettlementDays` = 1 (the default), the payments and refunds of a day are paid to a pretend bank at 10:00
    IST the next day, less a 2 % fee and 18 % GST on it; 2 to 7 days settle later. With 0, everything captured so far
    settles when settlements are read. A run happens when a day's settlements are read and it is due; each
    transaction is settled once (`settlement_id`, `settled_at` on `simulator_transactions`). Simulated UTRs start with
    `SIMUTR`.

### Card/UPI POS (INT-POS)

Pine Labs Plutus Cloud-style push-to-terminal:

* **Upload**: the billed transaction goes to the terminal. The body carries the security token and is never logged.
* **Poll**: status codes are mapped, and `pendingResponseCodes` means the customer is still paying.
* **Cancel**: cancels the transaction with its original amount.

Transaction ids encode what status and cancel need (`OPD-1:987654:125075`, meaning terminal, provider reference and
paise), so no extra table is required. A sale that never reached a terminal gets a unique `declined-…` id. A lost
status or cancel response reports "still waiting" rather than a decline. Only the last four card digits are kept. The admin "test" checks configuration only, because pushing a sale
would prompt a customer at a counter.

**Reconcile.** The terminal's RRN and approval code are kept with the receipt, and Billing matches them with the
acquirer's settlement report, which an accountant imports; see *Card and UPI reconciliation* in
[billing.md](billing.md#card-and-upi-reconciliation-int-pos). The simulator's sales can be turned into a training
report there.

### Biometric attendance terminals (INT-BIO)

Terminals that can push send their punches to HR directly, over ZKTeco/eSSL ADMS (`/iclock/*`) or the JSON punch
API; see [hr.md](hr.md#biometric-attendance-int-bio). Terminals that cannot are **read** by HC360 through
`IAttendanceTerminalClient`, over the protocol ZKTeco, eSSL and compatible terminals speak on TCP port 4370 (the one
their own attendance software uses):

* **What is read.** Connect (with the COMM Key when the terminal asks for one), check the serial number, read the
  clock and the number of punches, then read the attendance buffer in pieces of at most 65,472 bytes, free it and
  exit. Users, fingerprint templates and faces are never asked for, and no punch is deleted from the terminal. The only
  change ever made is setting its clock, when HR asks.
* **Serial number.** A terminal that reports a serial number other than the registered one is not read, so punches
  are never put on the wrong terminal when an address changes hands.
* **Records.** 40-byte records (firmware of the last decade) and 16-byte records with a work code (older firmware) are
  read. If the terminal took punches between counting and reading, the buffer is read in the layout that fits it.
  8-byte records of the oldest firmware are refused with what to do instead. A record whose time is not a date is
  counted as unreadable and left out. A buffer over 16 MB is refused.
* **Protocol details** (`ZkTecoProtocol`, unit-tested against bytes worked out with the open-source pyzk client): each
  packet is framed by `50 50 82 7D` and its length; the checksum is the SDK's ones'-complement-style sum, computed with
  the previous reply number while the packet carries the next one; the COMM Key is bit-reversed, added to the session
  id and mixed with "ZKSO" and a tick count; times count 31-day months from 2000.
* **Failures in words.** Every failure becomes a sentence for HR staff: no answer within the timeout, connection
  refused, not an attendance terminal, the key missing or refused, another serial number, firmware too old. Each read
  and clock change is logged as `terminal.read` or `terminal.set_clock` with the serial number and counts; the COMM Key
  is never logged.
* **Simulator.** The training terminal keeps the punches HR makes on it (`TerminalPunch` rows in
  `integrations.simulator_transactions`: serial number, employee number and the terminal's time) and returns those of
  the last 90 days on every read, as a real terminal does. Its clock is India time.
* **Dashboard and test.** The biometric row shows the provider (or `push` while terminals only push), and its health
  from the log: reads, pushes, refused serial numbers and terminals HR was told had gone quiet. The test checks the
  settings; HR reads each terminal with Read now.

### Tally Prime (INT-TALLY)

Billing builds the day book and keeps what Tally answered for every voucher; see
[billing.md, The Tally day book](billing.md#the-tally-day-book). This module sends the vouchers, reads a day back and
builds the import file.

* Vouchers are sent as `ENVELOPE / HEADER / TALLYREQUEST = Import Data` with `SVCURRENTCOMPANY`, one
  `TALLYMESSAGE / VOUCHER` per voucher (`REMOTEID="HC360-<reference>"`, `VCHTYPE`, `ACTION="Create"`,
  `OBJVIEW="Accounting Voucher View"`), and `ALLLEDGERENTRIES.LIST` lines, built with LINQ to XML so every value is
  escaped.
* REMOTEIDs: bills `HC360-<bill number>`, cancellations `HC360-<bill number>/C`, receipts, refunds, credit notes and
  payouts their own numbers, claim deductions `HC360-<claim>/<settlement digits>`, doctor-fee journals
  `HC360-DF-<facility>-<yyyyMMdd>-<doctor code>` (`HC360-DF-<yyyyMMdd>-001` on days posted before per-voucher
  tracking), pharmacy and canteen days `HC360-PH-<facility>-<yyyyMMdd>` and `HC360-CT-…` with `-R` and `-C` for
  returns and balances collected. The `HC360-` prefix tells HC360's vouchers from the hospital's own.
* Sign convention: a debit is `ISDEEMEDPOSITIVE=Yes` with a negative `AMOUNT`, a credit is `No` with a positive one.
* Cost centres go under `CATEGORYALLOCATIONS.LIST`. Ledger names are mapped with `ledgerMap`.
* Every voucher is validated first: balanced, positive amounts, ledgers present, at least two lines. One bad voucher
  is reported by number instead of Tally's generic totals error, and nothing of the set is sent.
* The import goes in batches of `batchSize`. `REMOTEID` makes a retried batch alter rather than duplicate.
* The response counters `CREATED`, `ALTERED` and `ERRORS`, plus `LINEERROR` texts, are parsed from both the classic
  and the Prime layout. Tally's illegal XML character references are tolerated and DTDs are prohibited.
* Tally answers a batch with counts, not per voucher. When the counts account for every voucher, each is reported as
  created, altered or (mixed counts) accepted. Otherwise the batch's vouchers are sent again one at a time to learn
  which ones Tally refused and why; the REMOTEID makes the second sending alter what the first created.
* The caller's cancellation token is its deadline. Once it is cancelled no further envelope is sent (also inside the
  one-at-a-time loop), a call in progress is cut short, and the vouchers not answered come back as not sent ("the
  attempt ran out of time") rather than as an exception. Billing stops calling Tally 24 s into a post attempt, inside
  the outbox's 30 s, and each HTTP call has 20 s.
* `ReadDayAsync` (behind `ReadBackAsync`) first checks that the configured company is open (a closed company would
  read as an empty day), then asks for the day's vouchers as a collection export and streams the answer (20 s).
  `TallyReadBack` matches HC360's vouchers by REMOTEID: present, amount, type, and ledgers as net amounts per ledger
  after the `ledgerMap` renaming, ignoring case and line order. Voucher numbers are not compared (Tally may renumber);
  Tally's number is reported. HC360 vouchers on the day that the expected list does not have are returned as
  `Others`; the hospital's own vouchers are ignored.
* `PingAsync` asks for the list of open companies and checks the configured one.
* `BuildImportFile` produces the same envelope for manual import (Tally → Import → Vouchers) and works even while the
  integration is disabled.
* The simulator keeps a company per `companyName` in `integrations.simulator_tally_vouchers` (one row per company and
  REMOTEID, so an import alters as Tally does) and answers the read-back from it, so the whole flow can be tried
  without Tally.

### PACS (INT-PACS)

PACS work has three parts: searches and images over DICOMweb (`DicomWebPacsProvider`, behind `IPacsClient`), which
Diagnostics uses to link studies and to show their images; the viewer launch link; and the DICOM modality worklist
server, which modalities query over DICOM. Diagnostics owns the rules about studies: see the radiology section of
[diagnostics.md](diagnostics.md).

#### Searches (QIDO-RS)

* **Studies**: `GET {qidoUrl}/studies?StudyInstanceUID&AccessionNumber&PatientID&StudyDate=from-to&ModalitiesInStudy`
  with `includefield` for description, series and object counts, modalities and timezone offset, and `limit` (at most
  500). DICOM wildcards `*` and `?` and control characters are stripped and values are cut at 64 characters, so one
  lookup can never scan the whole archive.
* **Series of a study**: `studies/{study}/series` with description, body part and object count.
* **Objects of a series**: `studies/{study}/series/{series}/instances` with frames, rows, columns and instance number,
  read in pages of 500 up to 2000 objects, returned in instance-number order.
* DICOM JSON is parsed by tag: PN `SHARMA^RAVI` becomes "SHARMA RAVI", IS values may arrive as numbers or strings,
  and date and time are read with offset `00080201` or the PACS zone.
* Authentication is none, basic or bearer. Accept is `application/dicom+json`, then `application/json`.
* UIDs go into URL paths only after they pass the DICOM UID check (digits and dots, at most 64 characters). The router
  refuses any other value before a provider sees it.
* The integration log gets one line per search, with the query shape, the patient ID masked to its last four
  characters, and counts. Results hold patient names, so they are never logged.

#### Images (WADO-RS)

* **Rendered image**: `studies/{study}/series/{series}/instances/{object}/rendered`, or `…/frames/{n}/rendered` for
  one frame of a multi-frame object. **Series thumbnail**: `studies/{study}/series/{series}/thumbnail`.
  Each asks `viewport={size},{size}`, and Accept prefers `image/jpeg`, then `image/png`. The root is `wadoUrl`, or
  `qidoUrl` when that is empty.
* Only JPEG or PNG is passed on: the declared type must be `image/jpeg` (or `image/jpg`) or `image/png`, and the bytes
  must start with that format's signature. HTML error pages, multipart bodies and DICOM itself are refused.
* **Size limits**: 16 MB for a rendered image, 2 MB for a thumbnail. A declared length over the limit is refused before
  anything is read. An image of declared length is read straight into one array of that size and refused if it ends
  early or runs on; one of undeclared length grows a single buffer up to the limit. The filled part goes to the
  response without another copy.
* 404, 410 and 204 mean the image is not there; 400, 406, 415 and 501 mean the PACS does not offer that
  representation (thumbnails are optional in DICOMweb). Other statuses are failures. When the PACS gives no series
  thumbnail, Diagnostics renders the middle image of the series at 160 pixels instead.
* A viewer fetches hundreds of images, so image retrievals are written to the integration log only when they fail.
* The images reach the browser only through the server, never straight from PACS, so PACS credentials stay on the
  server. The endpoints are Diagnostics' `/api/v1/radiology/studies/{id}/images…`: they need `radiology.images.view`,
  pass the restricted-record guard, write the PHI access audit, answer `Cache-Control: no-store`, and accept only the
  series and object UIDs PACS lists for that study. The thumbnail and rendered-image endpoints use the
  `radiology-images` rate-limit policy: per person, 6 images fetched at once and 32 waiting, then 429.

#### Sending (STOW-RS)

Cardiology sends the DICOM files of echos and other studies that machines export (see *Cardiology to the PACS* in
[diagnostics.md](diagnostics.md)). `StoreAsync` takes the files and the patient and study they belong to
(`PacsStudyIdentity`); the router prepares each file (`DicomStow.Prepare`, fo-dicom) and the provider sends it.

* **Filed under HC360's patient and study.** A machine records whatever was typed at its console, often a name without
  the UHID, and its own study. HC360 writes Patient ID (the UHID), Patient's Name, Birth Date and Sex, Study Instance
  UID, Accession Number (left empty when HC360's is longer than DICOM's 16 characters) and Study Description over them,
  so the PACS files the object with the patient's other studies and the viewer link finds it. Values are cut to their
  DICOM lengths (64 characters for LO and each PN component group).
* **Nothing the machine recorded is lost.** The values replaced go in the object's Original Attributes Sequence
  (0400,0561; PS3.3 §C.12.1) with Modifying System `HC360`, reason `COERCE` and the time; an earlier entry (from another
  system) is kept. A value HC360 does not change is not listed.
* **Same object, same UID.** The SOP class and instance UIDs (and the series UID) are kept, so sending a file again
  replaces the same object in the PACS instead of adding a copy. A file without a SOP class or instance UID in the
  object, a DICOMDIR (a CD's index) or a file fo-dicom cannot read is not sent (`NotDicom`).
* **Character sets.** When a new value holds characters the object's character set cannot (a name in Devanagari in a
  Latin-1 or ASCII object), the object is changed to UTF-8 (`ISO_IR 192`); fo-dicom then writes all of its text,
  nested sequences included, in UTF-8. ASCII values never change the character set.
* **The request.** One object per request: `POST {stowUrl or qidoUrl}/studies` with
  `Content-Type: multipart/related; type="application/dicom"` and one `application/dicom` part, Accept
  `application/dicom+json`, and the PACS's sign-in. Sending has its own HTTP client with two minutes per file (a file is
  at most 25 MB); searches keep their 20 seconds. POST is never retried at the HTTP layer.
* **The answer.** 200 (and 204) is stored; 202 is stored with a warning. The DICOM JSON body is read for this object:
  in the Failed SOP Sequence (0008,1198) it is refused with the Failure Reason (0008,1197) in words (out of resources,
  SOP class not supported, does not match its SOP class, transfer syntax not supported, cannot understand); in the
  Referenced SOP Sequence (0008,1199) a Warning Reason (0008,1196) is passed on. Failure reason 0111 (the PACS already
  holds the object) counts as stored, and a failure in a 200 answer still refuses it. Without a body: 409, 400, 413 and
  415 are refusals; 401 and 403 (sign-in), 404, 405 and 501 (wrong address) and other statuses are failures, which
  sending again may get past.
* **Log.** One `stow` line per object, with its SOP instance UID, its size and the HTTP status, and the reason when it
  was not stored. The patient's details are never logged.
* **Where it went.** `StoreDestinationAsync` gives the provider and the STOW-RS root, for example
  `dicomweb http://orthanc:8042/dicom-web`, or `simulator`. Cardiology keeps it with each outcome, so an object stored
  in the simulator while training, or in the PACS used before, is not taken as held by the PACS set up now. URLs cannot
  carry credentials, so neither does this.

#### Viewer launch

* `BuildViewerUrl` fills `viewerUrlTemplate` with URL-encoded values and returns only http(s) URLs. A template must
  name the study with `{studyUid}`, `{accession}` or both, and gets no link when a value it needs is missing. It works
  even while the integration is disabled.
* Diagnostics decides when to offer it. Once a study is linked, the template gets the study UID and the accession
  number. Before that, the server first asks PACS for the study and offers the link only when PACS holds exactly one
  study under the accession, with the patient's UHID as its patient ID; a template with only `{accession}` is then
  enough. An accession number longer than DICOM's 16 characters cannot reach PACS, so a template with only
  `{accession}` gets `pacs.accession_too_long`.

#### Simulator

`"provider": "simulator"` answers every accession-number or study-UID query with one study, held under the patient ID
HC360 expects (the query's `ExpectedPatientId`, which real PACS never receive), with three series: a localiser (one
image), an image series (eight images) and a cine loop (one object of twelve frames, without a series thumbnail).
Images are generated PNGs. UIDs are derived from the accession number or study UID, so they stay the same. Objects sent
to it (STOW-RS) are prepared as for a real PACS and taken as stored; they are not kept.

#### Modality worklist server (DICOM MWL SCP)

Modalities (CT, MR, X-ray, ultrasound, mammography, fluoroscopy) ask HC360 over DICOM for the studies they are to do,
and get the patient's details, the accession number and the study UID HC360 assigned at ordering, so nobody types
them at the console and the images reach PACS under the right study. The server is built on **fo-dicom** 5
(`ModalityWorklistService`, `ModalityWorklistGateway`, `ModalityWorklistHostedService`).

* **Settings**: one global row, `integrations.mwl` (table above), edited in Administration › Integrations › Modality
  worklist. It is off until turned on. Each modality on the allow-list has its own **calling AE title** (1–16
  letters, digits, inner spaces, `.`, `-` or `_`; case does not matter), at least one **IP address**, the **facility**
  whose studies it sees (by facility code; may be left out when there is one facility) and the **kinds of study** it
  does (`CR` or `DX` for X-ray, `CT`, `MR`, `US`, `MG`, `RF`; `XR` is read as `CR`). An entry that cannot be used is
  left out and its reason is listed on the dashboard; the rest still work. Two entries with the same AE title, or a
  modality with the server's own AE title, are refused.
* **Listener**: `ModalityWorklistHostedService` runs only where `PlatformOptions.RunsJobs` is true. Every minute it
  reads the settings. An allow-list change applies at once; a change of address, port, AE title or association limit
  restarts the listener; turning it off stops it; a port that could not be opened is tried again the next minute.
* **Who may speak**:
  * A connection from an address no listed modality has is closed before anything is read.
  * An association must call the server's AE title, from a listed AE title and one of its addresses. Others are
    refused (called or calling AE title not recognised) and the connection is closed two seconds later.
  * Associations beyond `maxAssociations` are refused as a temporary local limit. An association idle for
    `idleTimeoutSeconds`, while no query is being answered, is closed.
  * The server's own AE title from this machine is accepted for C-ECHO only: that is the administrator's test.
  * Every C-FIND checks the modality again against the settings in force. A modality removed from the list or from
    that address meanwhile gets "unable to process" and its connection is closed; one moved to another facility reads
    that facility's worklist from its next query.
* **Services**: Verification (C-ECHO) and Modality Worklist Information Model – FIND (C-FIND), Explicit or Implicit VR
  Little Endian. Nothing else is accepted.
* **Entries offered**: the modality's facility's imaging studies that are ordered, scheduled, arrived or started, of the
  kinds of study the modality does, whose step starts from today minus `daysBack` to today plus `daysAhead` (hospital
  time). The step starts at arrival, else at the appointment, else when the study was ordered. A study with a station
  assigned goes only to that station; one without goes to every station.
* **Matching keys**:
  * Patient's Name: wildcards `*` and `?`, without regard to case, ignoring empty trailing components; a key without
    `^` or wildcards also finds the family name alone, as staff type "SHARMA" at the console.
  * Patient ID, Accession Number and Requested Procedure ID: exact or with wildcards. An accession number longer than
    16 characters is matched only by an empty or `*` key.
  * Modality: `CR` and `DX` stand for each other. Scheduled Station AE Title: wildcards; entries without a station
    match.
  * Scheduled Procedure Step Start Date and Time: one value or a range; the time range applies to each day.
  * Other keys are not used for matching; they come back empty.
* **Paging and the cap**: the database filters on the facility, kinds of study, days, start times of each day,
  station, the patient ID and accession number with their wildcards, and the literal start of the family name. Entries
  nearest to now come first. The server reads page after page until `maxResults` entries match every key, at most 5000
  entries for one query, so entries the database cannot rule out never push matching ones out of the answer. When more
  entries match than `maxResults`, the first `maxResults` are sent and the integration log line says the answer was cut
  short.
* **Answers**: each entry holds exactly the attributes the modality asked for (plus Specific Character Set), the ones
  HC360 knows filled, the rest empty. Patient name as `FAMILY^GIVEN^MIDDLE` (a patient registered without a surname
  goes whole in the family component; a name over 64 characters loses the middle, then the given name), UHID as
  Patient ID, birth date, sex, the accession number, Requested Procedure ID and Scheduled Procedure Step ID (the
  accession number, or for one longer than 16 characters a 16-character code made from the study's id), the Study
  Instance UID (`2.25.…`), the step's modality, station, start date and time, status (SCHEDULED, ARRIVED or STARTED),
  room, description, body part, referring doctor, priority (STAT, HIGH or ROUTINE), and the placer and filler order
  numbers. An accession number longer than 16 characters is never cut: it is sent empty and in full as the Filler Order
  Number, and the images are linked by study UID instead. A value that does not fit its DICOM type is left out, never
  shortened. The step's modality is the X-ray code the modality uses (a DR room sees DX, a CR reader sees CR).
* **Audit and logs**: before anything is sent, one PHI access audit entry (`worklist.queried`) records who asked, from
  where, the facility and how many entries went; if it cannot be written, nothing is sent ("unable to process"). One
  integration-log line per C-FIND records the query shape with the patient name as `***` and the patient ID masked to
  its last four characters, and the number of entries. fo-dicom's dataset and PDU logging are off, and its own
  per-association lines are logged at Debug. Exception messages are not logged, only their type, because they can
  quote patient data.
* **Refusals**: every refused connection, association or query is counted on the dashboard and logged at Debug. A
  warning and an integration-log line are written at most once per ten minutes for each kind of refusal and network
  (/24 for IPv4, /64 for IPv6), with the number left out since. Past 1000 networks, refusals are throttled by kind
  alone. So a scan of the hospital network cannot flood either log.
* **Status and test**: the dashboard shows the listener (state, open associations, answered and refused counts, last
  error). `POST /api/v1/admin/integrations/mwl/test` sends a C-ECHO from the server to itself; in a split deployment,
  call it on the node that runs background jobs.

### Laboratory analysers (INT-LAB)

Analysers send results to HC360, ask it which tests to run on a tube (host query), and are sent orders without asking
(order download). The analyser's protocol always runs on the server, whichever way the analyser is connected.

* **Setting up an analyser.** The analyser itself (code, name, bench, protocol HL7 or ASTM, Bidirectional, active) is
  registered in Lab setup › Analysers (Diagnostics). Its connection is set up on the same screen (Connection), which
  saves it in `integrations.lab.instruments` through `/api/v1/integrations/lab/connections`. The protocol is always
  taken from Lab setup. The laboratory gateway as a whole is switched on in Administration › Integrations ›
  Laboratory equipment (`integrations.lab.enabled`). Changes apply within a minute.
* **Bidirectional is the one switch.** Host queries and order download are on exactly when the analyser is active and
  Bidirectional in Lab setup. Otherwise a host query is acknowledged and answered with nothing (ASTM), or answered
  `QAK|…|AR` (HL7), and no orders are queued for it. The retired per-connection `"hostQuery": false` still keeps both
  off for a connection that carries it; saving the connection in Lab setup removes it, and the connection list flags
  it until then.
* **Facilities.** Analyser codes are unique across facilities, so the gateway finds an analyser by its code. Lab
  setup › Analysers › Connection shows and edits only the signed-in facility's analysers and lab agents (an account
  with no facility sees all). A serial analyser can only be relayed by a lab agent enrolled at its own facility.
  Results, host queries and orders only ever reach tubes of the analyser's own facility.

#### Network analysers (transport `Tcp`)

`LabGatewayHostedService` runs only where `PlatformOptions.RunsJobs` is true. Every minute it reconciles one TCP
listener per enabled network analyser with the settings: it starts new listeners, restarts changed ones, stops
removed ones and retries ports that could not be bound.

* An analyser connects without signing in, so its address is the only check. A connection needs at least one
  address in `allowedIps` (Lab setup does not save one without), and connections from anywhere else are closed at
  once. Use the analyser's fixed IP. A connection saved without an address before one was required takes any address:
  the gateway logs a warning when it starts listening, and Lab setup › Analysers shows it as *Open to any device*.
* At most `maxConnectionsPerInstrument` connections are served at a time.
* Idle connections close after `idleTimeoutSeconds`.
* A port the HC360 server uses itself (HTTPS, the HTTP redirect, mDNS 5353) is refused, and two analysers cannot share
  a port.
* The Windows firewall must allow the port (`Install-HC360Server.ps1 -LabPorts … -LabAnalyserAddresses …`).

#### Serial analysers through a lab agent (transport `Serial`)

An analyser with an RS-232 port is cabled to a Windows PC in the lab that runs the **HC360 lab agent** (`src/agent`,
`HC360LabAgent.exe`, Windows service `HC360LabAgent`). It is a separate program from HC360 Desktop, because analysers
run day and night and HC360 Desktop closes when its user signs out. Installing and enrolling it is described in
[DEPLOYMENT.md](../deployment/DEPLOYMENT.md#lab-analysers).

* **Enrolment.** The agent is an enrolled device of kind `LabAgent` ("Lab analyser agent" in Administration ›
  Devices), with a one-time code like HC360 Desktop. Its device secret is protected with machine DPAPI on the lab PC.
  A lab agent's credential is accepted only by the lab agent endpoints: it never counts as an enrolled workstation for
  sign-in, and the desktop heartbeat refuses it.
* **Pinned CA.** Enrolment requires the hospital CA fingerprint (there is nobody at a service to confirm one). Every
  call the agent makes, the relay included, accepts only a server certificate that chains to exactly that CA
  (intermediates the server sends are used), is for a TLS server (serverAuth) and names the host the agent contacts.
* **Heartbeat.** Every minute the agent calls `POST /api/v1/integrations/lab/agent/heartbeat` with its version and the
  state of each COM port it was asked to open (open, or the Windows error). The answer lists its analysers and their
  port settings (COM port, baud rate, data bits, parity, stop bits, handshake, DTR, RTS). The agent starts, restarts or
  stops relays to match and leaves working lines alone.
* **Relay.** For each analyser the agent opens the COM port and a WebSocket to
  `/api/v1/integrations/lab/agent/relay/{code}` (sub-protocol `hc360.lab-relay.v1`, keep-alive pings every 15 s). Binary
  messages carry the serial line's bytes both ways; a text message ends the relay as a protocol error. The server runs
  the same protocol session on the relay as on a TCP socket, and a relayed line never times out as idle. The agent
  never answers the analyser itself, and bytes read while no relay is open are dropped (E1381's timers make the
  analyser send again).
* **Checks.** Every agent request carries `X-Device-Id` and `X-Device-Key` and uses the `device` rate limit. An agent
  that is not enrolled, is revoked, or is another kind of device gets 401; an analyser not assigned to that agent gets
  404; with the gateway off the relay answers 503. One relay per analyser: a new one replaces the old. Every minute the
  server checks again that the agent is enrolled and the analyser is still assigned to it, unchanged.
* **Ending a relay.** The server sends a WebSocket close frame that says why (replaced by a newer relay, agent
  revoked, analyser no longer assigned, its settings changed, gateway switched off, server stopping), waits up to 5 s
  for the agent's reply, then closes the connection. The agent logs the reason and connects again after a wait that
  grows to a minute.
* **Logging.** The agent logs ports, connections and byte counts, never the bytes. Warnings and errors go to the
  Windows Application log (source *HyFib HC360 Lab Agent*).

#### HL7 v2 over MLLP

Messages are framed `0x0B … 0x1C 0x0D`. Every message the analyser sends gets exactly one framed answer, and
acknowledgements are never acknowledged in turn.

* **Results**: ORU^R01/R30/R32 and OUL^R21–R24.
  * Samples are identified by `sampleIdFields`, default SPM-2, SAC-3, OBR-3, OBR-2.
  * OBX handling:
    * NM, ST and TX values are read as text.
    * CE, CWE and CNE use the coded value's text.
    * SN is joined, for example `<5` or `1:160`.
    * ED, RP and AD values, and D, W and X statuses, are skipped.
  * Results are timed from OBX-14, falling back to OBR-7 and then MSH-7, interpreted in the lab's zone.
  * Acknowledgements: `MSA|AA` once the LIS has stored the results; `MSA|AE` when it could not, so the analyser keeps
    and resends them; `MSA|AR` for other message types or text that cannot be read. The ACK swaps sender and receiver
    and echoes the version.
* **Host query**:
  * `QBP^Q11` (HL7 2.5, IHE LAW): the sample is QPD-3, else QPD-4. The answer is `RSP^K11` with MSA, QAK (the query
    tag; `OK` with orders, `NF` without, `AR` when host queries are off, `AE` when the LIS cannot answer), the QPD
    echoed, then the patient and one order group per test.
  * `QRY^Q02` (HL7 2.3.1): the sample is QRD-8. The answer is `QCK^Q02` (MSA, and QAK with QRD-4 as the tag); the
    orders found then follow on the connection as order messages.
* **Orders** are sent in the connection's `orderMessage`: `OML^O21` (HL7 2.5.1, the default) or `ORM^O01` (2.3.1, also
  chosen for an analyser that writes 2.3 in MSH-12). PID with the UHID and the patient, then per test ORC (`NW` new,
  `CA` cancel; ORC-2 the barcode), a TQ1 with the priority (OBR-5 in 2.3.1), OBR (OBR-2 the barcode, OBR-4 the
  analyser's channel code, OBR-7 the collection time) and for 2.5.1 SPM (SPM-2 the barcode). Every value is escaped. One
  sample goes per message, each waiting up to 30 s for the analyser's acknowledgement before the next, and at most 10
  per quiet spell, so results never wait long behind a backlog.
* A message larger than `maxMessageKilobytes` closes the connection.

#### ASTM E1381/E1394

* **Link layer**: ENQ/ACK handshake, frame-number and checksum checks (NAK on error), a frame repeated after a lost
  ACK is acknowledged again without being applied twice, ETB continuation, frame numbers modulo 8. Frames up to 64 KB
  are accepted, since some analysers exceed E1381's 247 bytes.
* **Results**: the frame completing a message (the one with the L record) is ACKed **only after** the LIS stored the
  results. If the LIS is unavailable or rejects a sample, that frame is NAKed and the analyser retransmits it, so a
  result is never acknowledged and then lost. Records H/P/O/R/C/L use the delimiters from the header. The sample comes
  from O.3, falling back to O.4. Results with status X or I are skipped.
* **Size limit**: once a message passes `maxMessageKilobytes`, the transfer is refused as a whole: that frame and every
  later one are NAKed until the analyser gives up with EOT, and nothing of it is stored, not even frames already
  acknowledged. The rejection is logged without the message.
* **Host query**: Q records ask the LIS through `GetOrderForSampleAsync`. HC360 answers after the analyser's EOT with
  H, then P and O records (O.5 `^^^code` repeats, O.26 `Q`) and `L|1|F`, or `L|1|I` when there are no orders.
* **Orders** (order download): H, then a P and an O record per sample (P.3 the UHID, O.6 priority `S`, `A` or `R`, O.12
  the action `N` new, `A` add for a rerun, `C` cancel, O.26 `O`) and `L|1|N`, at most 10 samples per message, sent while
  the line is idle.
* **Sending and contention** (E1381): HC360 bids with ENQ. If the analyser bids at the same moment, the instrument has
  priority: HC360 does not answer the colliding ENQ, answers the analyser's next ENQ and receives its message, and bids
  again no sooner than 20 s after the contention. After a NAK (busy) it bids again no sooner than 10 s later. While it
  waits it keeps listening, so the analyser can send at any time. After three refused bids the send is given up (EOT).
  NAKed frames are retransmitted up to six times. A receiver interrupt (EOT instead of ACK) acknowledges the frame, and
  the rest of the message is still sent so the analyser never holds half an order.

#### Order download

* **What is sent.** Diagnostics asks for orders in the same transaction as the change that causes them (see
  [diagnostics.md](diagnostics.md#analyser-interface)): a tube received in the lab (`New`), a reopened result (`Rerun`),
  a cancelled test or a rejected tube (`Cancel`). It writes one `lab.analyser.order` outbox message naming the active
  bidirectional analysers of the tube's facility that run its tests, with their channel codes.
* **Queue.** `LabOrderOutboxHandler` adds one `integrations.lab_order_downloads` row per analyser, in the dispatcher's
  transaction; a redelivered message adds nothing. Rows hold the barcode and channel codes, never patient data. They
  wait until the analyser is connected and its line is quiet (looked for every 3 s).
* **Built when sent.** The order is built from the LIS when its turn comes (`GetOrderToSendAsync`), so a test
  cancelled or resulted in the meantime is not sent. A row with nothing left to send is closed as `Skipped` with the
  reason, and so are all of an analyser's rows when it is no longer active and bidirectional. A cancellation is sent
  only to an analyser that was sent the sample.
* **Claims.** A session claims the rows it sends for 2 minutes under a claim id of its own, and extends the claim every
  30 s for as long as the send takes (bids, back-offs, the analyser's own messages in between). Only the holder of the
  claim records the outcome, so a claim that lapsed (a crashed session) never lets two sessions record the same order.
* **Retries.** A refused send is tried again after 30 s, 1, 2, 4 and 8 minutes, then every 10 minutes, at most 10
  times (`Failed`). Rows not sent within 72 hours expire (`Expired`); finished rows are deleted after 30 days. The
  hourly housekeeping runs in `LabGatewayHostedService`.

#### LIS bridge, status and logging

* Every message gets its own DI scope and resolves `IInstrumentResultSink`, which the Diagnostics module implements.
  If no sink is registered, results are refused (AE or NAK) and logged, never dropped silently.
* Raw messages, host queries and orders sent are written to the integration log only after `LabPhiMasking` has removed
  names, dates of birth, addresses and phones and cut identifiers to their last four characters. Relay log rows hold the
  agent, the line settings and byte counts.
* Live status per analyser (listening, connected through its agent, waiting for its agent, COM port problem, failed,
  training; connections, accepted and refused counts, last error, orders waiting) appears on the integration dashboard
  and in Lab setup › Analysers. Administration › Integrations › Test reports how many network analysers are listening,
  serial analysers connected and training analysers ready, and which are not working.

#### Training analyser (transport `Simulator`)

The lab's simulator: an analyser in Lab setup whose connection is *Training analyser (simulator)*. Nothing connects to
it, so the lab can practise without an analyser. Code: `Features/LabConnections/LabTrainingAnalyser.cs`.

* **Sending results.** In Lab setup › Analysers, *Send results* (needs `integrations.lab.connections`) takes a received
  sample's barcode and lists the analyser's channel codes the sample still needs (the same answer as a host query,
  `IInstrumentResultSink.GetOrderForSampleAsync`). Someone enters the values, and HC360 writes the message the analyser
  would send: an ORU^R01 (HL7 2.5.1, the barcode in OBR-2, OBR-3 and SPM-2) or an ASTM E1394 message (the barcode in
  O.3), per the analyser's protocol in Lab setup.
* **The same path.** The message is read back with the gateway's own mappers (`Hl7ResultMapper`, honouring the
  analyser's sample-id fields, or `AstmMessageMapper`) and handed to the LIS through `ILabMessageProcessor`. So results
  are matched by channel code, auto-validated, delta-checked and held by QC lockout as a real analyser's are, and are
  logged (provider `simulator:<code>`) and counted on the dashboard.
* **Limits.** Only an analyser whose connection is `Simulator` takes results from the screen (otherwise 422
  `lab.not_training_analyser`), with the gateway on, the connection and the analyser switched on, at the user's own
  facility. Values and units cannot hold the characters analyser messages reserve (`| ^ ~ \ &`, line breaks); at most
  100 results a message. Use it only for training: the results are real results on the sample.

### Barcode labels (INT-BAR)

The server writes printer commands; it never prints. HC360 Desktop sends the commands to the workstation's label or
wristband printer (`printer.printRaw`). Each workstation keeps its own printers (see *Printers on this computer*
below); the facility only chooses, in `integrations.labels`, between those printers and the simulator. Code: `Labels/`
(canvas, writers, fonts, Code 128, QR sizing) and `Labels/Templates/`, endpoints and `LabelJobs` in
`Features/Labels/`.

**Log and health.** Every label request is traced in the integration log (integration `labels`): the kind of label
as the operation, the printer language as the provider, `n label(s) × copies, W × H mm at D dpi` as the request, and
the outcome, with the reason when a label is refused. What is on the labels is never logged. The integrations dashboard
takes Barcode labels' health from these rows, as for any integration.

**Simulator.** With `integrations.labels` `provider: "simulator"` (Administration › Integrations › Barcode labels),
nothing prints. `GET /api/v1/integrations/labels/mode` answers `LabelMode { simulator }` (cached a minute by the staff
app); the staff app still posts each job, for this computer's ZPL or TSPL printer if it has one and otherwise for
standard stock (50 × 25 mm labels, 25.4 × 279.4 mm bands, ZPL at 203 dpi). The server writes the commands as usual and
keeps them in the simulator inbox (channel `Labels`: the kind and size as the subject, the commands as the body), and
the staff app shows the label page in the print dialog instead of printing it silently.

**Typed labels.** Each kind of label has a template that lays out the same facts as the staff app's own label page, so
a label looks the same whichever way it is printed. The staff app sends the data; nothing is looked up here.

| Endpoint (`POST /api/v1/integrations/labels/…`) | Data (`LabelData.cs`) | What prints | Scanned value |
|---|---|---|---|
| `lab-tubes` | `LabTubeLabel { barcode, patientName, uhid, ageSex, accessionNumber, stat, tube, bench, tests[], collectedAt }` | Name and age/sex, UHID, STAT in white on black (or the accession number), the barcode and its number, tube and bench, the tests (`CBC, ESR +2` when they do not fit), collection time | Sample barcode (Code 128) |
| `wristbands` | `WristbandLabel { uhid, patientName, ageSex, admissionNumber, ward, bed, allergies }`, plus `code` (`Code128` or `QrCode`) for the request | Along the band: name, UHID and age/sex, admission number and ward / bed, the allergy mark, then the code | UHID (Code 128 or QR) |
| `dispensing` | `DispensingLabel { patientName, medicine, quantity, directions, batch, expiry, dispensedOn, hospital }` | Name, medicine and quantity, directions (up to two lines each), batch and expiry, hospital and date. On a short label the hospital gives way first, then the second line of the directions, then of the medicine; the expiry is never shortened | none |
| `blood-units` | `BloodUnitLabel { unitNumber, bloodGroup, component, volumeMl, cleared, expiresAt, storage }` | Group with POS or NEG in full, component and volume, TTI NON-REACTIVE, or QUARANTINE / NOT FOR ISSUE in white on black until cleared; the barcode and unit number, expiry, storage | Unit number (Code 128) |
| `cssd-packs` | `CssdPackLabel { packNumber, setName, method, sterileUntil }` | Set name, the barcode and pack number, method and use-by date, or NOT RELEASED in white on black while `sterileUntil` is null | Pack number (Code 128) |
| `asset-tags` | `AssetTagLabel { assetTag, assetName, hospital }` | Hospital, asset name, the barcode and tag | Asset tag (Code 128) |
| `pharmacy-tags` | `PharmacyTagLabel { barcode, medicine, batch, expiry, mrp, unit }` | Medicine, `B {batch}   Exp {Mon yyyy}`, `MRP Rs {mrp}/{unit}`, the barcode and its value. On a short label the MRP line gives way first, then the batch line; the barcode is never dropped | Item code and batch, `PCM500#AB1234` (Code 128) |
| `test` | `TestLabelRequest { printer, stock (Label or Wristband), computer }` | A frame 1 mm inside the edges, the printer as HC360 sees it (language, dpi, size), a sample barcode at real module width, the computer and time | `HC360-TEST` |

Each request carries `printer: LabelPrinterSetup { language, dpi, widthMm, heightMm }` and `copies` (1–10); the answer
is `LabelCommands { language, content, labels }`. At most 100 labels a request, 10 wristbands.

**Pharmacy tags.** A tag goes on a strip, bottle, box or shelf. Its barcode is the item code and the batch number
joined by `#` (`PCM500#AB1234`); item codes never hold a `#`, so the server splits the value unambiguously
(`BatchTag` in SupplyChain). A batch that cannot be coded (too long, or with `! > " \`) gets the item code alone. The
staff app prints tags from a posted goods receipt (Print tags) and from a batch on the stock screen (Tags), one per
batch with as many copies as needed, at most 100 labels a print. Scanning a tag:

* at the counter (`GET /api/v1/pharmacy/stock?search=PCM500%23AB1234`) finds the medicine by its exact code (and any
  item whose own barcode is the whole text, since a barcode may hold a `#`; that item gets no `scanned`) and answers
  `scanned: ScannedBatch { batchNumber, batchId, expiryDate, mrp, available, problem }`. The batch is the usable one
  of that number that expires first (the number matches whatever the case); otherwise `batchId` is null and `problem`
  says why: not in this store, blocked (with the reason), expired, or nothing left to sell. The counter adds the
  medicine with that batch, or warns.
* in the dispensing queue, the "Scan to check a medicine" box checks the strip or tag in hand against the prescription:
  a medicine not on it is warned about, and a tag's batch is picked for its line. Only an exact match counts (the tag's
  item, or the item whose code or barcode is the scan); a medicine that only matched on its name does not.

**Wristband allergy mark.** `GET /api/v1/inpatient/admissions/{id}/wristband` gives the chart's allergy status, and the
staff app passes it through as `allergies`:

* `Recorded` (active allergies on the chart): a black ALLERGY mark.
* `NoKnownAllergies` (a clinician recorded it): no mark.
* `NotRecorded` (nobody has recorded allergies or their absence): an outlined "ALLERGIES / NOT RECORDED" mark, black
  text in a frame, so a patient never assessed does not look like one with no known allergies.

A band is not reprinted by itself when allergies are recorded later; staff reprint it (see [inpatient.md](inpatient.md)).

**Printers and sizes.**

* Languages: `zpl` (ZPL II, Zebra and compatibles) and `tspl` (`tspl2` accepted; TSC and compatibles). Resolution 203
  or 300 dpi; positions are printer dots (8 per mm at 203 dpi, about 11.8 at 300 dpi).
* Label rolls: 25–110 mm wide, 15–100 mm tall. Wristband bands: 15–40 mm wide, 80–350 mm long. Sizes are to one
  decimal place. Wristband text and codes are turned a quarter turn so they read along the wrist.
* ZPL: one `^XA … ^XZ` per label with `^CI28`, `^PW`, `^LL` and `^PQ` for copies; text in font 0. TSPL: `SIZE`,
  `GAP 2 mm,0 mm`, then `CLS … PRINT 1,copies` per label; text in the dot fonts.
* **Code 128**: 1 to 48 printable ASCII characters (not `!`, `>`, `"` or `\`), code sets chosen for the shortest
  barcode. Every module is a whole number of dots, at least 0.19 mm and at most 0.5 mm, with quiet zones of ten
  modules, so barcodes scan. A value too long for the label is refused.
* **QR**: model 2, at most 400 bytes, the smallest version that fits. On a band each module is at least 0.33 mm, or
  the band is refused with "Use Code 128".
* **Text** is printed in the printer's own fonts, which cover printable ASCII only. Accents are dropped (José → Jose),
  typographic marks become ASCII (– → -, ° → deg, ₹ → Rs), white space collapses, and text too long for its line is
  shortened with "...". Letters with no ASCII form (Devanagari, say) cannot be printed this way.
* **Escaping**: in ZPL every field goes through `^FH`, with `^`, `~`, `_` and non-ASCII bytes written as `_hh`; in TSPL
  quotes are written as `\["]` and control characters dropped. Label data can never become a printer command.

**Refusals and the page fallback.** A label that does not fit its media answers 422 `labels.does_not_fit`, and text the
printer fonts cannot print answers 422 `labels.text_not_printable`, each with a message for staff. A request that
breaks the validation rules answers 400.

**How the staff app prints a label** (`printLabel` in `packages/core/src/print/labels.ts`, used through
`printLabelJob`):

1. It reads the workstation's printers from HC360 Desktop. Wristbands use the wristband printer, every other label the
   label printer.
2. For a ZPL or TSPL printer it posts the labels to the endpoint for their kind and sends the answer to the printer with
   `printer.printRaw`.
3. On `labels.text_not_printable`, or on a 400, it prints the label page (HTML) on the same printer through its Windows
   driver instead. Any other error (sign-in, permission, network, `labels.does_not_fit`) stops the print and is shown.
4. With a printer set to `driver`, it prints the label page silently on that printer. With no printer for the kind, or
   in a browser, it prints the page with the print dialog.

**General-purpose label.** `POST /api/v1/integrations/labels` (`ILabelPrinterService`) takes one value as Code 128 or
QR with up to six text lines of 80 characters: `PrintLabelRequest { value, symbology, textLines, widthMm, heightMm,
copies, language, dpi }` → `PrintLabelResponse { language, content, copies }`.

#### Printers on this computer

HC360 Desktop keeps each workstation's printers for every Windows account on it, in
`%ProgramData%\HyFib\HC360Desktop\Workstation\printers.json`. The staff app's screen is Printers on this computer
(`/workstation/printers`, in the user menu inside HC360 Desktop).

* **Roles.** `labels` and `wristbands` (each with a language `zpl`, `tspl` or `driver`, and for the first two the
  resolution and label or band size; wristbands also `code128` or `qr`), `receipts` (80 mm slips and token slips) and
  `documents` (A4 and A5). A page goes to the printer for its page size, silently; a role with no printer shows the
  print dialog, so nothing prints on whichever printer is the Windows default.
* **Who may change them.** Anyone may see the printers and print a test. Changing them needs
  `integrations.printers.setup`:
  1. `POST /api/v1/integrations/printers/changes/approval` checks the permission and the change and answers
     `PrinterChangeApproval { changedBy }`, the signed-in user's name. Nothing is recorded yet.
  2. HC360 Desktop saves the change (`printer.saveSetup`), with that name as "saved by".
  3. `POST /api/v1/integrations/printers/changes` records it in the audit trail as `workstation.printers_changed`, with
     each role's printer, language, resolution, size and the wristband code. A change HC360 Desktop refused is not
     recorded. If this call fails after the save, the screen says the printers were saved but not recorded.
* **What protects the setup.** HC360 Desktop cannot confirm the server's approval: it holds no user's sign-in and
  nothing the server signs, so `printer.saveSetup` saves what the staff app sends, and every Windows user on the
  computer may change `printers.json` (the installer gives Users modify rights so any logon can save). The "saved by"
  name is a note, not proof; the audit trail is the record. Where local users or scripts must not change the printers,
  IT sets them in the machine policy (`policy.json`, `printers` entry, see the deployment guide), which is read-only
  to users: HC360 then shows them and refuses changes.
* **Checked when read.** The saved file and the policy are checked like a save. A printer that is not installed any
  more, or not a valid setup, is not used (that kind of print shows the print dialog), and the screen says which and
  why.
* **Only installed printers.** `printer.printRaw` and `printer.printHtml` print only to printers installed on the
  workstation. A shared queue on another computer (`\\host\queue`) must be added in Windows first.
* **Test print.** For a ZPL or TSPL printer the screen prints the server's test label; for any other printer a test page
  in the role's paper size.

#### Serial barcode scanners

A scanner that types like a keyboard (USB HID, the usual kind) needs no setup: it types the code and presses Enter in
whatever field has the focus. A scanner on a COM port (RS-232, or USB set to its serial mode) is read by HC360 Desktop,
which hands each code to the staff app; the staff app types it into the focused field of the window in front and
presses Enter, so every scan box works with either kind. Code: `Bridge/SerialScannerService.cs`,
`Bridge/ScanAssembler.cs` and `Bridge/ScannerSetup.cs` in HC360 Desktop; `packages/core/src/bridge/scans.ts` in the
staff app.

* **Setup.** One scanner per workstation, on the Printers on this computer screen (Barcode scanner): the COM port
  (COM1–COM256), speed (1200–115200 baud), data bits (7 or 8), parity (none, even, odd) and stop bits (1 or 2), as set
  on the scanner; most leave the factory at 9600, 8, none, 1. Saved in
  `%ProgramData%\HyFib\HC360Desktop\Workstation\scanner.json`, or fixed by IT with a `scanner` entry in the machine
  policy. Changing it follows the printers' approval and record steps, with
  `/api/v1/integrations/scanners/changes/approval` and `/api/v1/integrations/scanners/changes` (audited as
  `workstation.scanner_changed`).
* **Reading.** A scan ends at CR, LF or tab, or after 100 ms of silence (for a scanner set to send no suffix). GS, RS,
  EOT and NUL are dropped, as a keyboard scanner drops them. Anything else outside printable ASCII means the port's
  settings do not match the scanner's, so that scan is refused rather than typed, and the screen says to check them.
  More than 512 characters without an end is refused as noise.
* **Where scans go.** Into a focused text, search, number, phone, URL or e-mail field, or a text area (where Enter
  starts a new line unless the page takes it); never a password, a disabled or read-only field, or anything else. A
  scan with no field focused, or while HC360 is not the window in front, goes nowhere.
* **Port trouble.** While the port is missing (a USB scanner unplugged), held by another program or failing, HC360
  Desktop tries again every 5 seconds, and the screen shows which (`not_found`, `busy`, `error`), with the last scan's
  time. "Try a scan" on the screen shows what a scan reads, from either kind of scanner.
* **Bridge.** `scanner.getSetup` → `ScannerSetupInfo { setup, lockedByPolicy, computerName, problems, status, ports }`;
  `scanner.saveSetup { setup }` saves and listens to it; each scan arrives as the `scanner.scan` event `{ text }`.

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
* **Full local catalogues.** The online lookup is not needed for ICD-10 search once the hospital imports its own copy
  of the WHO ICD-10 2019 ClaML file (WHO-licensed, supplied by the hospital) or the public-domain CMS ICD-10-PCS order
  file into Clinical's local catalogue: Medical records > ICD catalogue > Import, or `catalogue import --file <file>` on
  the Server Node. See [Clinical, Catalogue import](clinical.md#catalogue-import).
* **Simulator.** With `provider: "simulator"` the lookup answers from a short built-in list of common codes
  (`SimulatorIcdCatalogue`, 27 ICD-11 and 27 ICD-10 entries), the way the ICD-API does: ICD-11 by words in the title or
  the start of the code, ICD-10 by code with its subcategories. Nothing goes to WHO and no credentials are needed; each
  search is logged (provider `simulator`). The codes and titles are real, but the list is a sample for training, not
  the classification.
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
| GET | `/api/v1/admin/integrations` | `admin.integrations.manage` | → `IntegrationStatusDto[]`: provider, enabled/configured, health (`healthy`, `degraded`, `failing`, `idle`, `disabled` or `not_configured`), last success/failure, failures in 24 h, lab and modality worklist listener status |
| POST | `/api/v1/admin/integrations/{name}/test` | `admin.integrations.manage` | `TestIntegrationRequest { to, message, dltTemplateId, templateName, languageCode }` → `IntegrationTestResult`. Sends a real test SMS, WhatsApp or e-mail, creates a ₹1 order, pings Tally, PACS or ICD, echoes the modality worklist server (`mwl`), and checks lab, label and biometric configuration. Audited as `integration.tested`. |
| GET | `/api/v1/admin/integrations/logs` | `admin.integrations.manage` | paging + `integration, success, from, to, search` → `PagedResult<IntegrationLogDto>` |
| GET | `/api/v1/admin/integrations/simulator/messages` | `admin.integrations.manage` | paging + `channel` → simulated SMS, WhatsApp, e-mail and Tally deliveries |
| GET | `/api/v1/admin/integrations/simulator/transactions` | `admin.integrations.manage` | paging + `kind` → simulated payment orders, links and refunds, POS sales, and punches on training attendance terminals |
| GET | `/api/v1/admin/integrations/whatsapp/inbound` | `admin.integrations.manage` | paging + `keyword` → `InboundWhatsAppMessageDto` (sender masked) with `keyword` and `keywordAction` (`OptedOut`, `OptedIn`, `Superseded`, `None`, or null for replies not acted on yet). The staff inbox shows it as the Result column |
| GET | `/api/v1/integrations/lab/connections` | `integrations.lab.connections`, `integrations.lab.status` or `admin.integrations.manage` | → `LabConnectionsDto { gatewayEnabled, listVersion, connections[], agents[] }`: each analyser's connection with its Lab setup state, live status and orders waiting, and the facility's enrolled lab agents (online when seen in the last 3 minutes or relaying) |
| PUT | `/api/v1/integrations/lab/connections/{code}` | `integrations.lab.connections` | `SaveLabConnectionRequest { transport, enabled, port, allowedIps, encoding, sampleIdFields, orderMessage, agentDeviceId, serial, listVersion }` → 204. 404 for an analyser not in Lab setup or of another facility; 400 for a port the server uses, a port or an agent's COM port already taken, or an agent that is not an enrolled lab agent of the analyser's facility; 409 when the list changed since `listVersion`. Clears the retired `hostQuery`. Audited as `lab.connection.saved` |
| DELETE | `/api/v1/integrations/lab/connections/{code}?version` | `integrations.lab.connections` | → 204; 409 on a stale version. Audited as `lab.connection.removed` |
| GET | `/api/v1/integrations/lab/connections/{code}/training/samples/{barcode}` | `integrations.lab.connections` | Training analyser only → `TrainingSampleDto { sampleBarcode, patientName, patientId, channels[] }`; 422 `lab.training_no_tests` when the sample needs nothing from the analyser, `lab.not_training_analyser`, `lab.gateway_off` or `lab.training_off` |
| POST | `/api/v1/integrations/lab/connections/{code}/training/results` | `integrations.lab.connections` | `TrainingResultsRequest { sampleBarcode, results[{ channel, value, units }] }` → `TrainingResultsOutcome { accepted, message, rawMessage }`; the same 422s; 400 for values analyser messages cannot carry |
| POST | `/api/v1/integrations/lab/agent/heartbeat` | anonymous, lab agent credential (`X-Device-Id`, `X-Device-Key`), `device` rate limit | `LabAgentHeartbeatRequest { appVersion, ports[] { code, open, error } }` (at most 64 ports) → `LabAgentAssignments { deviceId, deviceName, gatewayEnabled, heartbeatSeconds, instruments[] }`; 401 for a device that is not an enrolled lab agent |
| GET | `/api/v1/integrations/lab/agent/relay/{code}` | anonymous, lab agent credential, `device` rate limit | WebSocket, sub-protocol `hc360.lab-relay.v1`: the analyser's serial bytes. 401 not an enrolled lab agent, 404 analyser not assigned to it, 503 gateway off |
| POST | `/api/v1/integrations/labels` | `integrations.labels.print` | `PrintLabelRequest { value, symbology, textLines, widthMm, heightMm, copies, language, dpi }` → `PrintLabelResponse { language, content, copies }` |
| POST | `/api/v1/integrations/labels/{lab-tubes\|wristbands\|dispensing\|blood-units\|cssd-packs\|asset-tags\|pharmacy-tags}` | `integrations.labels.print` | `{ printer: LabelPrinterSetup, labels[], copies }` (wristbands also `code`) → `LabelCommands { language, content, labels }`; 422 `labels.does_not_fit` or `labels.text_not_printable`. See [Barcode labels](#barcode-labels-int-bar) |
| POST | `/api/v1/integrations/labels/test` | `integrations.labels.print` | `TestLabelRequest { printer, stock, computer }` → `LabelCommands` |
| GET | `/api/v1/integrations/labels/mode` | `integrations.labels.print` | → `LabelMode { simulator }`: whether the facility's labels go to the simulator |
| POST | `/api/v1/integrations/printers/changes/approval` | `integrations.printers.setup` | `PrinterSetupChange { computer, deviceId, roles[{ role, printer, language, dpi, widthMm, heightMm, code }] }` → `PrinterChangeApproval { changedBy }`; records nothing |
| POST | `/api/v1/integrations/printers/changes` | `integrations.printers.setup` | `PrinterSetupChange` → 204; audited as `workstation.printers_changed` |
| POST | `/api/v1/integrations/scanners/changes/approval` | `integrations.printers.setup` | `ScannerSetupChange { computer, deviceId, port, baudRate, dataBits, parity, stopBits }` → `PrinterChangeApproval { changedBy }`; records nothing |
| POST | `/api/v1/integrations/scanners/changes` | `integrations.printers.setup` | `ScannerSetupChange` → 204; audited as `workstation.scanner_changed` |
| POST | `/api/v1/integrations/webhooks/sms/msg91` | anonymous, token | delivery reports (JSON or form `data`) |
| POST | `/api/v1/integrations/webhooks/sms/twilio` | anonymous, `X-Twilio-Signature` | status callbacks (form) |
| GET, POST | `/api/v1/integrations/webhooks/sms/generic` | anonymous, token | `{ id\|messageId\|msgid…, status }` |
| GET | `/api/v1/integrations/webhooks/whatsapp` | anonymous, verify token | Meta subscription check |
| POST | `/api/v1/integrations/webhooks/whatsapp` | anonymous, `X-Hub-Signature-256` | statuses and inbound messages |

Webhooks use the platform's `public` rate-limit policy, read at most 256 KB and accept `?facility=<id>`.

The PACS images and the modality worklist have no endpoints here: the images and the viewer link are served by
Diagnostics (`/api/v1/radiology/studies/{id}/images…` and `/viewer`), and the worklist is DICOM on its own port.

Permissions:
* `IntegrationsModule` declares `integrations.labels.print` (all staff roles), `integrations.printers.setup` (changes
  a workstation's printers; Hospital Admin by default), `integrations.lab.connections` (set up analyser connections:
  ports, allowed addresses, serial ports and lab agents; HospitalAdmin) and `integrations.lab.status` (see whether
  analysers and their agents are connected; LabTechnician, Pathologist, HospitalAdmin).
* The dashboard, test sends, logs and inboxes need the platform's `admin.integrations.manage`. It has no default role:
  only Super Admin holds it until an administrator grants it, because it exposes message logs and live test sends.
* Integration settings and credentials are saved through the platform settings API (`/api/v1/admin/settings`), which
  needs `admin.settings.manage`. Credentials are write-only, and each change is audited as a warning.
* The Tally day book's own endpoints (`/api/v1/billing/tally`, posting, the file, the checks, mark imported and the
  ledger names) belong to Billing and need `billing.tally.export` or `billing.tally.configure`; see
  [billing.md](billing.md#tally--apiv1billingtally).

## DI registrations (`IntegrationsServiceCollectionExtensions.AddIntegrations`)

* **Scoped helpers**: `IntegrationSettingsReader`, `IntegrationTrace`, `SimulatorInbox`, `NotificationLogUpdater`,
  `WhatsAppInboundStore`, `IntegrationStatusService`, `IntegrationTestService`.
* **Typed HTTP clients** with the standard resilience handler: `Msg91SmsProvider`, `TwilioSmsProvider`,
  `GenericHttpSmsProvider`, `MetaWhatsAppProvider`, `RazorpayGateway`, `PineLabsPosTerminal`, `DicomWebPacsProvider`
  and `WhoIcdApiClient` use a 15 s attempt timeout and 20 s total. `TallyPrimeExporter` uses 20 s for both.
* **Providers** (scoped, keyed by `Key`):
  * `ISmsProvider`: msg91, twilio, generic, simulator.
  * `IWhatsAppProvider`: meta, simulator.
  * `IEmailProvider`: smtp, simulator.
  * `IPaymentProvider`: razorpay, simulator.
  * `IPosProvider`: pinelabs, simulator.
  * `IAttendanceTerminalProvider`: zkteco, simulator.
  * `IAccountingProvider`: tally, simulator.
  * `IPacsProvider`: dicomweb, simulator.
* **Platform contracts** (scoped routers):
  * `ISmsSender` → `SmsRouter` (with `ISmsTemplateCatalog` → `SmsTemplateCatalog`)
  * `IWhatsAppSender` → `WhatsAppRouter`
  * `IEmailSender` → `EmailRouter`
  * `IPaymentGateway` → `PaymentGatewayRouter`
  * `IPosTerminal` → `PosTerminalRouter`
  * `IAttendanceTerminalClient` → `AttendanceTerminalRouter`
  * `IAccountingExporter` → `AccountingExporterRouter`
  * `IPacsClient` → `PacsClientRouter`
  * `IIcdLookup` → `IcdLookupRouter`
  * `ILabelPrinterService` → `LabelPrinterService`, a singleton
  * `IPdfRenderer` → `EdgePdfRenderer`, a singleton (disposed with the host, which closes the browser)
* **Singletons**: `SimulatorSigningKey`, `IcdTokenCache`, `LabGatewayState`, `LabRelayRegistry` (the live relays), and
  `ILabMessageProcessor` → `LabMessageProcessor`.
* **Modality worklist** (`AddModalityWorklist`): fo-dicom's services (`AddFellowOakDicom`, with dataset and PDU logging
  off), the singletons `ModalityWorklistState` and `ModalityWorklistGateway`, the scoped `IWorklistFacilities` →
  `WorklistFacilities` and `ModalityWorklistSelfTest`, and the hosted service `ModalityWorklistHostedService`.
* **Lab, scoped**: `LabOrderQueue`, `LabAgentAuthenticator`, `LabConnectionService`.
* **Hosted services**: `LabGatewayHostedService`, `ModalityWorklistHostedService`.
* **Found by the module scan** (not in `AddIntegrations`): the outbox handlers `WhatsAppReplyConfirmationHandler`
  (`whatsapp.reply_confirmation`) and `LabOrderOutboxHandler` (`lab.analyser.order`), and the seeder
  `WhatsAppReplyBacklog`.

## Security summary

* Every inbound webhook is verified with a token, signature or verify token, all compared in constant time
  (`CryptographicOperations.FixedTimeEquals`; tokens are compared by SHA-256 so their length does not leak). Optional
  IP allow-lists apply. Bodies are capped, and the anonymous rate limit applies.
* Secrets are read only from `*.secret` settings and never logged or returned. Log summaries are built by the
  adapters, and patient data in analyser messages is masked.
* Every network call has a timeout. The TCP listeners limit message size, connection count and idle time and apply
  per-instrument allow-lists, which every network analyser's connection must have.
* Lab agents authenticate with their device id and secret (compared in constant time) on every call, may relay only
  the analysers assigned to them, and are checked again every minute. A lab agent's credential is refused everywhere
  else, sign-in included. The agent trusts only the pinned hospital CA.
* The modality worklist listener admits only listed modalities, by calling AE title and IP address together, and
  closes other connections before reading anything. It checks the modality again on every query, serves only the
  modality's own facility, writes one PHI access audit entry per answer before sending it, masks patient keys in the
  integration log and throttles refusal logging by network. DICOM has no encryption or passwords here, so keep the
  port on the imaging network (see [SECURITY.md](../security/SECURITY.md)).
* PACS images reach the browser only through the server, as JPEG or PNG within a size limit, for people with
  `radiology.images.view`, with a per-person limit on images in flight.
* Attendance terminals are read only at an IP address that HR registered, never at a host name, a loopback, link-local
  (cloud metadata), multicast or broadcast address; the address is checked when it is saved and again before every
  read. Each read has a time limit (`timeoutSeconds`) and a size limit.
* External ids and paths (Razorpay ids, POS transaction ids, WhatsApp phone-number id and API version, ICD child
  links, PACS viewer templates, DICOM UIDs) are validated before they are used in URLs.
* Queries order only by timestamps. Enum columns (stored as text) are only compared for equality.

## Not implemented here

* **Biometric attendance**:
  * Pushes are received by HR itself (ZKTeco/eSSL ADMS on `/iclock/*`); only reading terminals lives here.
  * Terminals that speak the protocol over UDP only, and Suprema, Matrix or other makes' own protocols, are not read:
    they can push over ADMS-compatible firmware or the JSON punch API through their middleware.
  * Real-time events (the terminal reporting each punch on an open connection) are not used: terminals are read on a
    schedule, every few minutes.
* **ABDM (HIP/HIU)** is out of scope for this project. The **HL7 FHIR R4 read facade** (EHR-05) is not an adapter:
  it is the Interop module (`/fhir/r4`), documented in [interop.md](interop.md).
* **ICD-10 free-text search online**: the WHO ICD-API does not support it, so the local catalogue answers instead.
* **DICOM beyond the worklist and DICOMweb reads**:
  * No C-STORE: HC360 sends objects over STOW-RS only, and only cardiology's DICOM device files; modalities send their
    images to PACS directly. PDFs and SCP-ECG are not turned into DICOM (Encapsulated PDF, ECG waveform) to send.
  * No C-MOVE or C-GET, no WADO-URI, and no download of DICOM files: HC360 shows JPEG or PNG renderings only.
    Windowing, measurements and the diagnostic read happen in the PACS viewer.
  * No Modality Performed Procedure Step (N-CREATE/N-SET) and no storage commitment: a study's progress is recorded by
    the radiographer in HC360, and its images are found by `PacsLinkJob`.
  * No DICOM TLS or user identity negotiation on the worklist port, and no query/retrieve SCP: the worklist answers
    C-FIND for modality worklist and C-ECHO only.
  * QIDO-RS searches by patient name, and searches with wildcards, are not offered.

### Not covered yet: payment gateway
* **PayU** and other gateways: only Razorpay (and the simulator) implement `IPaymentProvider`, settlements and refund
  status included.
* **Instant (on-demand) settlements** are read like any other settlement; HC360 does not request them.
* **Settlement webhooks** (`settlement.processed`) are recorded but not acted on; the hourly reconciliation job reads
  the settlements instead.
* **Card/UPI settlement reports** are imported by an accountant (Billing); HC360 does not fetch them from the acquirer.

## Database

The tables `integrations.simulator_messages`, `integrations.simulator_transactions`,
`integrations.simulator_tally_vouchers` (unique company and remote id) and `integrations.whatsapp_inbound_messages`
are configured in `Persistence/IntegrationsConfigurations.cs` and `Tally/SimulatedTallyCompany.cs`. The EF
migration is created centrally. They have unique `external_id` and `provider_message_id` indexes, and all three
entities are `[NotAudited]` because the integration log already traces them. `whatsapp_inbound_messages.keyword_action`
(text, nullable) records what a STOP or START did; rows stored before it existed stay empty until
`WhatsAppReplyBacklog` acts on them. The number-level opt-out itself is the platform's `platform.whatsapp_opt_outs`.
`simulator_transactions.settlement_id` and `settled_at` record which simulated settlement paid a payment or took a
refund (indexed by `settled_at`); the gateway's real settlements are stored by Billing (`billing.gateway_settlements`).

`integrations.lab_order_downloads` (`LabOrderDownload`, `Persistence/LabOrderDownloadConfiguration.cs`, migration
`LabAnalyserOrders`) is the order download queue: one row per analyser and request, with `facility_id`,
`instrument_code`, `sample_barcode`, `action` and `status` (text), `channel_codes` (text array), `attempts`,
`created_at`, `next_attempt_at`, `claimed_until` and `claimed_by` (the sending session's claim), `completed_at`,
`last_error` and `source_message_id`. A unique index on (`source_message_id`, `instrument_code`) makes a redelivered
outbox message add nothing, and (`instrument_code`, `status`, `next_attempt_at`) serves the sessions' look-ups. Rows
hold no patient data and are `[NotAudited]`; finished rows are deleted after 30 days. The lab connections themselves are
the global setting `integrations.lab.instruments`. Lab agents are rows of the platform's `identity.devices` with kind
`LabAgent` (stored as text, so no schema change).
