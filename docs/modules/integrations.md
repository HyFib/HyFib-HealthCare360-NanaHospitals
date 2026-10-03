# Integrations

Adapters between HC360 and external systems: SMS, WhatsApp, e-mail, payment gateway, card/UPI POS, Tally Prime,
PACS (DICOMweb), the DICOM modality worklist server, laboratory analysers (HL7 v2 / ASTM), barcode label printers and
the WHO ICD-API. It also holds the server's PDF renderer, which drives headless Microsoft Edge.

| | |
|---|---|
| Project | `src/server/HyFib.HealthCare360.Integrations` (module key `integrations`, registered by `IntegrationsModule`) |
| Schema | `integrations`: `simulator_messages`, `simulator_transactions`, `whatsapp_inbound_messages` (the integration log itself is the platform's `integration_logs`) |
| Requirements | SRS §5: INT-SMS, INT-WA, INT-PG, INT-POS, INT-TALLY, INT-PACS, INT-LAB, INT-BAR, INT-ICD; SEC-10 (PHI in logs) |
| Tests | `tests/HyFib.HealthCare360.Integrations.Tests`: about 635 tests covering signatures, templates, every provider against a fake HTTP handler, the routers other modules resolve (payment, POS, PACS, ICD), webhook parsers, WhatsApp STOP/START handling, Tally XML, DICOM JSON, QIDO-RS and WADO-RS against a fake PACS, the modality worklist server answering fo-dicom's own client on a local port, worklist matching and settings, the HL7/MLLP and ASTM E1381/E1394 sessions over an in-memory socket, PHI masking, labels, validators and DI wiring |

## How it fits together

* **Platform contracts, one router each.** Other modules depend only on the contracts in
  `Platform/Integrations/IntegrationAbstractions.cs` (`ISmsSender`, `IWhatsAppSender`, `IEmailSender`,
  `IPaymentGateway`, `IPosTerminal`, `IAccountingExporter`, `IPacsClient`, `IIcdLookup`, `ILabelPrinterService`).
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
| `IPacsClient` | Diagnostics radiology: linking studies with PACS (by hand and by `PacsLinkJob`), the study's series and images, and viewer links | `PacsClientRouter` → DICOMweb / simulator | `FindStudiesAsync`, `FindSeriesAsync`, `FindInstancesAsync` and `RetrieveImageAsync` throw **`HttpRequestException`** with a staff-facing reason: not configured, disabled, unreachable, an HTTP error, a UID that is not a DICOM UID, or an answer that is not a JPEG or PNG image within the size limit. Diagnostics reports it as `pacs.unreachable`. Every call names the facility whose settings apply (null: the signed-in facility), because background linking has none. `BuildViewerUrl` returns null without a viewer template (`pacs.viewer_not_configured`). |
| `IModalityWorklistSource` (implemented by Diagnostics, scoped) | Consumed by the modality worklist server here, and by Diagnostics' own `/api/v1/radiology/worklist/mwl` | `ModalityWorklistSource` in Diagnostics, resolved in a new scope for every C-FIND | The server is off until `integrations.mwl.enabled` is set and modalities are listed. Without a registered source, every query is answered "unable to process" and logged. |
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
| `integrations.pacs` | `enabled`, `provider` (`dicomweb` \| `simulator`), `qidoUrl` (the DICOMweb root), `wadoUrl` (WADO-RS root when it differs; empty means `qidoUrl`), `authType` (`none` \| `basic` \| `bearer`), `userName`, `viewerUrlTemplate` (`{studyUid}`, `{accession}`), `limit` (50, at most 500 studies per search), `timeZone` (`Asia/Kolkata`, for study times sent without an offset) | `password`, `bearerToken` |
| `integrations.mwl` (global) | `enabled`, `bindAddress` (`0.0.0.0`), `port` (11112), `aeTitle` (`HC360MWL`), `characterSet` (`ISO_IR 100`; `ISO_IR 192` or empty), `maxAssociations` (10, 1–64), `idleTimeoutSeconds` (60, 10–3600), `daysBack` (3, 0–30), `daysAhead` (1, 0–30), `maxResults` (500, 1–1000), `modalities[] { aeTitle, name, allowedIps[], facility, modalities[], enabled }` | none |
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
Images are generated PNGs. UIDs are derived from the accession number or study UID, so they stay the same.

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
| GET | `/api/v1/admin/integrations` | `admin.integrations.manage` | → `IntegrationStatusDto[]`: provider, enabled/configured, health (`healthy`, `degraded`, `failing`, `idle`, `disabled` or `not_configured`), last success/failure, failures in 24 h, lab and modality worklist listener status |
| POST | `/api/v1/admin/integrations/{name}/test` | `admin.integrations.manage` | `TestIntegrationRequest { to, message, dltTemplateId, templateName, languageCode }` → `IntegrationTestResult`. Sends a real test SMS, WhatsApp or e-mail, creates a ₹1 order, pings Tally, PACS or ICD, echoes the modality worklist server (`mwl`), and checks lab and label configuration. Audited as `integration.tested`. |
| GET | `/api/v1/admin/integrations/logs` | `admin.integrations.manage` | paging + `integration, success, from, to, search` → `PagedResult<IntegrationLogDto>` |
| GET | `/api/v1/admin/integrations/simulator/messages` | `admin.integrations.manage` | paging + `channel` → simulated SMS, WhatsApp, e-mail and Tally deliveries |
| GET | `/api/v1/admin/integrations/simulator/transactions` | `admin.integrations.manage` | paging + `kind` → simulated payment orders and links, and POS sales |
| GET | `/api/v1/admin/integrations/whatsapp/inbound` | `admin.integrations.manage` | paging + `keyword` → `InboundWhatsAppMessageDto` (sender masked) with `keyword` and `keywordAction` (`OptedOut`, `OptedIn`, `Superseded`, `None`, or null for replies not acted on yet). The staff inbox shows it as the Result column |
| POST | `/api/v1/integrations/labels` | `integrations.labels.print` | `PrintLabelRequest { value, symbology, textLines, widthMm, heightMm, copies, language }` → `PrintLabelResponse { language, content, copies }` |
| POST | `/api/v1/integrations/webhooks/sms/msg91` | anonymous, token | delivery reports (JSON or form `data`) |
| POST | `/api/v1/integrations/webhooks/sms/twilio` | anonymous, `X-Twilio-Signature` | status callbacks (form) |
| GET, POST | `/api/v1/integrations/webhooks/sms/generic` | anonymous, token | `{ id\|messageId\|msgid…, status }` |
| GET | `/api/v1/integrations/webhooks/whatsapp` | anonymous, verify token | Meta subscription check |
| POST | `/api/v1/integrations/webhooks/whatsapp` | anonymous, `X-Hub-Signature-256` | statuses and inbound messages |

Webhooks use the platform's `public` rate-limit policy, read at most 256 KB and accept `?facility=<id>`.

The PACS images and the modality worklist have no endpoints here: the images and the viewer link are served by
Diagnostics (`/api/v1/radiology/studies/{id}/images…` and `/viewer`), and the worklist is DICOM on its own port.

Permissions:
* `IntegrationsModule` declares `integrations.labels.print` (all staff roles).
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
* **Singletons**: `SimulatorSigningKey`, `IcdTokenCache`, `LabGatewayState`, and `ILabMessageProcessor` →
  `LabMessageProcessor`.
* **Modality worklist** (`AddModalityWorklist`): fo-dicom's services (`AddFellowOakDicom`, with dataset and PDU logging
  off), the singletons `ModalityWorklistState` and `ModalityWorklistGateway`, the scoped `IWorklistFacilities` →
  `WorklistFacilities` and `ModalityWorklistSelfTest`, and the hosted service `ModalityWorklistHostedService`.
* **Hosted services**: `LabGatewayHostedService`, `ModalityWorklistHostedService`.
* **Found by the module scan** (not in `AddIntegrations`): the outbox handler `WhatsAppReplyConfirmationHandler`
  (`whatsapp.reply_confirmation`) and the seeder `WhatsAppReplyBacklog`.

## Security summary

* Every inbound webhook is verified with a token, signature or verify token, all compared in constant time
  (`CryptographicOperations.FixedTimeEquals`; tokens are compared by SHA-256 so their length does not leak). Optional
  IP allow-lists apply. Bodies are capped, and the anonymous rate limit applies.
* Secrets are read only from `*.secret` settings and never logged or returned. Log summaries are built by the
  adapters, and patient data in analyser messages is masked.
* Every network call has a timeout. The TCP listeners limit message size, connection count and idle time and apply
  per-instrument allow-lists.
* The modality worklist listener admits only listed modalities, by calling AE title and IP address together, and
  closes other connections before reading anything. It checks the modality again on every query, serves only the
  modality's own facility, writes one PHI access audit entry per answer before sending it, masks patient keys in the
  integration log and throttles refusal logging by network. DICOM has no encryption or passwords here, so keep the
  port on the imaging network (see [SECURITY.md](../security/SECURITY.md)).
* PACS images reach the browser only through the server, as JPEG or PNG within a size limit, for people with
  `radiology.images.view`, with a per-person limit on images in flight.
* External ids and paths (Razorpay ids, POS transaction ids, WhatsApp phone-number id and API version, ICD child
  links, PACS viewer templates, DICOM UIDs) are validated before they are used in URLs.
* Queries order only by timestamps. Enum columns (stored as text) are only compared for equality.

## Not implemented here

* **Biometric attendance**: by design there is no biometric abstraction. HR receives ZKTeco/eSSL ADMS pushes on
  `/iclock/*` itself, so no adapter lives here.
* **HL7 host query** (QRY^Q02 / QBP): not implemented. The ASTM host query is.
* **ABDM (HIP/HIU)** is out of scope for this project. The **HL7 FHIR R4 read facade** (EHR-05) is not an adapter:
  it is the Interop module (`/fhir/r4`), documented in [interop.md](interop.md).
* **ICD-10 free-text search online**: the WHO ICD-API does not support it, so the local catalogue answers instead.
* **DICOM beyond the worklist and DICOMweb reads**:
  * No STOW-RS and no C-STORE: HC360 never sends images to PACS; modalities send them there directly.
  * No C-MOVE or C-GET, no WADO-URI, and no download of DICOM files: HC360 shows JPEG or PNG renderings only.
    Windowing, measurements and the diagnostic read happen in the PACS viewer.
  * No Modality Performed Procedure Step (N-CREATE/N-SET) and no storage commitment: a study's progress is recorded by
    the radiographer in HC360, and its images are found by `PacsLinkJob`.
  * No DICOM TLS or user identity negotiation on the worklist port, and no query/retrieve SCP: the worklist answers
    C-FIND for modality worklist and C-ECHO only.
  * QIDO-RS searches by patient name, and searches with wildcards, are not offered.

## Database

The tables `integrations.simulator_messages`, `integrations.simulator_transactions` and
`integrations.whatsapp_inbound_messages` are configured in `Persistence/IntegrationsConfigurations.cs`. The EF
migration is created centrally. They have unique `external_id` and `provider_message_id` indexes, and all three
entities are `[NotAudited]` because the integration log already traces them. `whatsapp_inbound_messages.keyword_action`
(text, nullable) records what a STOP or START did; rows stored before it existed stay empty until
`WhatsAppReplyBacklog` acts on them. The number-level opt-out itself is the platform's `platform.whatsapp_opt_outs`.
