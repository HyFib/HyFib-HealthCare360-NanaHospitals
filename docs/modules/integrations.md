# Integrations

Adapters between HC360 and external systems: SMS, WhatsApp, e-mail, payment gateway, card/UPI POS, Tally Prime,
PACS (DICOMweb), laboratory analysers (HL7 v2 / ASTM), barcode label printers and the WHO ICD-API. It also holds the
server's PDF renderer, which drives headless Microsoft Edge.

| | |
|---|---|
| Project | `src/server/HyFib.HealthCare360.Integrations` (module key `integrations`, registered by `IntegrationsModule`) |
| Schema | `integrations`: `simulator_messages`, `simulator_transactions`, `whatsapp_inbound_messages`, `lab_order_downloads` (the integration log itself is the platform's `integration_logs`) |
| Requirements | SRS §5: INT-SMS, INT-WA, INT-PG, INT-POS, INT-TALLY, INT-PACS, INT-LAB, INT-BAR, INT-ICD; SEC-10 (PHI in logs) |
| Tests | `tests/HyFib.HealthCare360.Integrations.Tests`: 556 tests covering signatures, templates, every provider against a fake HTTP handler, the routers other modules resolve (payment, POS, PACS, ICD), webhook parsers, WhatsApp STOP/START handling, Tally XML, DICOM JSON, the HL7/MLLP and ASTM E1381/E1394 sessions over an in-memory socket (host queries, order download, contention, size limits), the lab agent relay over an in-memory WebSocket, PHI masking, labels, validators and DI wiring. `tests/HyFib.HealthCare360.LabAgent.Tests` runs the lab agent against the server's relay end to end (Windows). `tests/HyFib.HealthCare360.IntegrationTests` (`LabAnalyserTests`, `LabConnectionTests`) covers the connection rules, facility scoping, the relay endpoint and the order queue's claims against PostgreSQL |

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
| `ISmsSender`, `IWhatsAppSender`, `IEmailSender` | Platform `notify.sms`, `notify.whatsapp` and `notify.email` outbox handlers; the WhatsApp STOP/START confirmation (`IWhatsAppSender.SendTextAsync`) | `SmsRouter`, `WhatsAppRouter`, `EmailRouter` | `SendResult.Fail("… is not configured. Set it up in Administration → Integrations.")`, the same as the platform's `UnconfiguredChannelSender`. |
| `IPdfRenderer` | Document endpoints of Diagnostics, Clinical, Billing and Analytics (`…/pdf`, MIS export as PDF), and the notification document sources that attach reports to e-mails and WhatsApp messages | `EdgePdfRenderer` (singleton) → headless Microsoft Edge, else Google Chrome | Without a browser, `Status` says why and `RenderAsync` throws `PdfRenderException` with a message for staff. Document endpoints answer 503 `pdf.unavailable`, and the administrator dashboard shows the `pdf.unavailable` warning. Without this module, the platform's `UnavailablePdfRenderer` does the same. |
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
  stops, even after a crash. Its profile lives in `<data root>\temp\pdf\<server process id>-<random>`, is deleted when
  it closes, and a later start deletes any profile whose server process is gone. Extensions, sync, background
  networking, crash uploads and pings are switched off. The sandbox stays on.
* **Finding the browser.** `HealthCare360:Pdf:BrowserPath` if set. Otherwise Edge from Program Files or its App Paths
  registry entry, then Google Chrome. Without one, `Status` explains what to install.

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

* Connections from outside `allowedIps` are closed at once. Use the analyser's fixed IP.
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
* Live status per analyser (listening, connected through its agent, waiting for its agent, COM port problem, failed;
  connections, accepted and refused counts, last error, orders waiting) appears on the integration dashboard and in
  Lab setup › Analysers. Administration › Integrations › Test reports how many network analysers are listening and
  serial analysers connected, and which are not working.

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
| GET | `/api/v1/admin/integrations/whatsapp/inbound` | `admin.integrations.manage` | paging + `keyword` → `InboundWhatsAppMessageDto` (sender masked) with `keyword` and `keywordAction` (`OptedOut`, `OptedIn`, `Superseded`, `None`, or null for replies not acted on yet). The staff inbox shows it as the Result column |
| GET | `/api/v1/integrations/lab/connections` | `integrations.lab.connections`, `integrations.lab.status` or `admin.integrations.manage` | → `LabConnectionsDto { gatewayEnabled, listVersion, connections[], agents[] }`: each analyser's connection with its Lab setup state, live status and orders waiting, and the facility's enrolled lab agents (online when seen in the last 3 minutes or relaying) |
| PUT | `/api/v1/integrations/lab/connections/{code}` | `integrations.lab.connections` | `SaveLabConnectionRequest { transport, enabled, port, allowedIps, encoding, sampleIdFields, orderMessage, agentDeviceId, serial, listVersion }` → 204. 404 for an analyser not in Lab setup or of another facility; 400 for a port the server uses, a port or an agent's COM port already taken, or an agent that is not an enrolled lab agent of the analyser's facility; 409 when the list changed since `listVersion`. Clears the retired `hostQuery`. Audited as `lab.connection.saved` |
| DELETE | `/api/v1/integrations/lab/connections/{code}?version` | `integrations.lab.connections` | → 204; 409 on a stale version. Audited as `lab.connection.removed` |
| POST | `/api/v1/integrations/lab/agent/heartbeat` | anonymous, lab agent credential (`X-Device-Id`, `X-Device-Key`), `device` rate limit | `LabAgentHeartbeatRequest { appVersion, ports[] { code, open, error } }` (at most 64 ports) → `LabAgentAssignments { deviceId, deviceName, gatewayEnabled, heartbeatSeconds, instruments[] }`; 401 for a device that is not an enrolled lab agent |
| GET | `/api/v1/integrations/lab/agent/relay/{code}` | anonymous, lab agent credential, `device` rate limit | WebSocket, sub-protocol `hc360.lab-relay.v1`: the analyser's serial bytes. 401 not an enrolled lab agent, 404 analyser not assigned to it, 503 gateway off |
| POST | `/api/v1/integrations/labels` | `integrations.labels.print` | `PrintLabelRequest { value, symbology, textLines, widthMm, heightMm, copies, language }` → `PrintLabelResponse { language, content, copies }` |
| POST | `/api/v1/integrations/webhooks/sms/msg91` | anonymous, token | delivery reports (JSON or form `data`) |
| POST | `/api/v1/integrations/webhooks/sms/twilio` | anonymous, `X-Twilio-Signature` | status callbacks (form) |
| GET, POST | `/api/v1/integrations/webhooks/sms/generic` | anonymous, token | `{ id\|messageId\|msgid…, status }` |
| GET | `/api/v1/integrations/webhooks/whatsapp` | anonymous, verify token | Meta subscription check |
| POST | `/api/v1/integrations/webhooks/whatsapp` | anonymous, `X-Hub-Signature-256` | statuses and inbound messages |

Webhooks use the platform's `public` rate-limit policy, read at most 256 KB and accept `?facility=<id>`.

Permissions:
* `IntegrationsModule` declares `integrations.labels.print` (all staff roles), `integrations.lab.connections` (set up
  analyser connections: ports, allowed addresses, serial ports and lab agents; HospitalAdmin) and
  `integrations.lab.status` (see whether analysers and their agents are connected; LabTechnician, Pathologist,
  HospitalAdmin).
* The dashboard, test sends, logs and inboxes need the platform's `admin.integrations.manage`. It has no default role:
  only Super Admin holds it until an administrator grants it, because it exposes message logs and live test sends.
* Integration settings and credentials are saved through the platform settings API (`/api/v1/admin/settings`), which
  needs `admin.settings.manage`. Credentials are write-only, and each change is audited as a warning.

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
  * `IPdfRenderer` → `EdgePdfRenderer`, a singleton (disposed with the host, which closes the browser)
* **Singletons**: `SimulatorSigningKey`, `IcdTokenCache`, `LabGatewayState`, `LabRelayRegistry` (the live relays), and
  `ILabMessageProcessor` → `LabMessageProcessor`.
* **Lab, scoped**: `LabOrderQueue`, `LabAgentAuthenticator`, `LabConnectionService`.
* **Hosted service**: `LabGatewayHostedService`.
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
  per-instrument allow-lists.
* Lab agents authenticate with their device id and secret (compared in constant time) on every call, may relay only
  the analysers assigned to them, and are checked again every minute. A lab agent's credential is refused everywhere
  else, sign-in included. The agent trusts only the pinned hospital CA.
* External ids and paths (Razorpay ids, POS transaction ids, WhatsApp phone-number id and API version, ICD child
  links, PACS viewer templates) are validated before they are used in URLs.
* Queries order only by timestamps. Enum columns (stored as text) are only compared for equality.

## Not implemented here

* **Biometric attendance**: by design there is no biometric abstraction. HR receives ZKTeco/eSSL ADMS pushes on
  `/iclock/*` itself, so no adapter lives here.
* **ABDM (HIP/HIU)** is out of scope for this project. The **HL7 FHIR R4 read facade** (EHR-05) is not an adapter:
  it is the Interop module (`/fhir/r4`), documented in [interop.md](interop.md).
* **ICD-10 free-text search online**: the WHO ICD-API does not support it, so the local catalogue answers instead.

## Database

The tables `integrations.simulator_messages`, `integrations.simulator_transactions` and
`integrations.whatsapp_inbound_messages` are configured in `Persistence/IntegrationsConfigurations.cs`. The EF
migration is created centrally. They have unique `external_id` and `provider_message_id` indexes, and all three
entities are `[NotAudited]` because the integration log already traces them. `whatsapp_inbound_messages.keyword_action`
(text, nullable) records what a STOP or START did; rows stored before it existed stay empty until
`WhatsAppReplyBacklog` acts on them. The number-level opt-out itself is the platform's `platform.whatsapp_opt_outs`.

`integrations.lab_order_downloads` (`LabOrderDownload`, `Persistence/LabOrderDownloadConfiguration.cs`, migration
`LabAnalyserOrders`) is the order download queue: one row per analyser and request, with `facility_id`,
`instrument_code`, `sample_barcode`, `action` and `status` (text), `channel_codes` (text array), `attempts`,
`created_at`, `next_attempt_at`, `claimed_until` and `claimed_by` (the sending session's claim), `completed_at`,
`last_error` and `source_message_id`. A unique index on (`source_message_id`, `instrument_code`) makes a redelivered
outbox message add nothing, and (`instrument_code`, `status`, `next_attempt_at`) serves the sessions' look-ups. Rows
hold no patient data and are `[NotAudited]`; finished rows are deleted after 30 days. The lab connections themselves are
the global setting `integrations.lab.instruments`. Lab agents are rows of the platform's `identity.devices` with kind
`LabAgent` (stored as text, so no schema change).
