# AI module

Voice and typed commands for staff, server speech-to-text, and clinical dictation structured into SOAP drafts. AI is
**off by default**, and nothing is ever saved without a clinician. The hospital chooses where text goes: Anthropic's
cloud service, or an OpenAI-compatible server, which can run inside the hospital (see *Text providers*).

| | |
|---|---|
| Project | `src/server/Modules/HyFib.HealthCare360.Modules.AI` (module key and schema `ai`; references Patients) |
| Schema | `ai.usage_records` (`AiUsage`, `[NotAudited]`; indexes `(user_id, created_at)` and `created_at`) |
| Requirements | AIV-01..06, ADR-011 (AI provider), SEC-12 (AI data protection) |
| Tests | `tests/Modules/HyFib.HealthCare360.Modules.AI.Tests`: 320 tests covering the redactor, spoken numbers, the offline command parser, the AI answer interpreter, the command service, note parsing and structuring, provider selection and settings, the guards for every provider (`AiGuardTests`), the Claude request builder, response mapper and SDK error mapping, the OpenAI-compatible request, answers, errors and time limit, audio and Whisper, validators and DI |

## Behaviour

* **Offline first.** `AssistantIntentParser` is a deterministic rule set. It understands the everyday commands
  without any AI provider, so the assistant keeps working when AI is off or the internet is down:
  * "open patient UHID NH26000123"
  * "search patient 98765 43210"
  * "today's appointments" or "appointments next Friday"
  * "book a follow-up in two weeks"
  * "create a progress note: …"
  * "start dictation"
  * "go to billing"

  It strips wake words and politeness, reads spoken numbers and Indian day/month dates, and never treats "review
  tomorrow's appointments" as a follow-up. Only a command the rules do not recognise is sent to the AI provider, and
  only when AI is enabled and the user is within quota.
* **The model's answer is untrusted.** The command prompt forces a JSON schema. `AssistantAiInterpreter` accepts only
  the known intents. Routes must be real screens, dates must fall within a year, and follow-ups must be 1–365 days.
  Confirmation texts are generated locally and never taken from the model. Placeholders the model copies back, such
  as `[UHID_1]`, are restored to what the user said.
* **Nothing executes on the server.** The endpoint only interprets the command. Actions with side effects
  (`book_follow_up`, `create_note`) come back with `requiresConfirmation: true`. The UI asks the user, then calls
  the normal permission-checked APIs. A spoken UHID is resolved to `patientId` so the UI can open the chart
  directly, or it says "No patient was found with UHID …".
* **Redaction before anything is sent.** `PhiRedactor` is pure, offline and deterministic. It replaces e-mail and
  ABHA addresses, UHIDs (keyword-introduced or `{FAC}{YY}{SEQ}`), ABHA and Aadhaar numbers, Indian mobile and
  landline numbers, other long digit runs, and the context patient's identifiers and names. Names include the
  guardian and emergency contact, loaded by `PatientDirectory`. Each distinct value gets a stable placeholder such
  as `[PHONE_1]`. Clinical content (vitals, doses, dates) is untouched. The gateway redacts the whole request, the
  system prompt and every message with one set of placeholders, so the specialty and the screen route a user sends
  are covered too (see *Text providers*).
* **Clinical drafts.** `POST /clinical/structure-note` returns a SOAP draft with `status: "draft"` and
  `requiresClinicianReview: true`. It includes up to 5 well-formed ICD-10 suggestions (confidence 0–1) and warnings.
  The first warning always says the draft is AI-generated, and another says how many identifiers were replaced.
  Nothing is stored; the clinician edits and saves through the EMR. Restricted (VIP) patients need the same access
  as in the EMR (`PatientAccessGuard`).
* **Accounting, never content.**
  * Every AI call writes an `AiUsage` row (user, facility, patient, purpose, provider, model, tokens, latency,
    success, error) and a **clinical audit entry** `ai.<purpose>` (`ai.assistant.command`, `ai.clinical.structure-note`,
    `ai.voice.transcribe`) in one transaction. A text call is recorded by the gateway once it has reached a provider,
    whatever happened next: an answer, a failure, an answer that could not be read, an error, or a user who gave up
    waiting ("The call ended before the AI service answered.").
  * Neither record contains the transcript, prompt or answer.
  * Quotas are per user: `ai.limits`, default 60 per hour and 300 per day, counted from the usage rows.
  * The `ai` rate-limit policy allows 20 requests per minute per user on every AI endpoint.

## Text providers

`ai.provider` chooses the text provider, per facility, falling back to the setting for all facilities.

| `ai.provider` | Provider | Where the text goes |
|---|---|---|
| unset, or `anthropic` | `ClaudeTextService`: Anthropic Claude through the official SDK | Anthropic's cloud service, over https |
| `openai` | `OpenAiCompatibleTextService`: an OpenAI-compatible chat-completions server | The server the hospital names: a model server inside the hospital (vLLM, llama.cpp server, Ollama's OpenAI endpoint), so text need not leave the hospital, or Azure OpenAI |
| anything else, such as `disabled` | none | Nowhere: AI text is off ("AI provider '…' is not available.") |

An unset `ai.provider` means Anthropic, as before the provider could be chosen, so existing settings keep working. A
value no provider has never falls back to another provider: a misspelt choice must not send notes to a service the
hospital did not choose. Each provider is an `IAiTextProvider` with a `Key`, held by `AiTextProviderRegistry`. A provider
reads only its own settings and sends only what it is given.

How to choose:

* **Anthropic** when a cloud service is acceptable under the hospital's data-processing agreement (SRS A-08). Nothing
  to run; the key is all it needs.
* **An OpenAI-compatible server inside the hospital** when clinical text must stay on the hospital's network. The
  hospital runs the model server and chooses the model, and the quality of drafts and command fallbacks is that model's.
* **Azure OpenAI** when the hospital's agreements are with Microsoft. Text leaves the hospital, as with Anthropic.

### The guards, the same for every provider

Every provider is reached through `AiTextServiceRouter` (the module's `IAiGateway` and the platform's
`IAiTextService`). The guards sit there, around the provider call, so no provider can skip them:

1. **AI off sends nothing.** While `ai.enabled` is not `true` (the default), or the chosen provider is not configured,
   the router answers with the reason and calls no provider; no key is even decrypted.
2. **Only redacted text.** A provider's `CompleteAsync` takes a `RedactedAiRequest`, which only
   `RedactedAiRequest.Redact` can make. It runs `PhiRedactor` over the system prompt and every message, with the
   identifiers on file for the patient in context, so a provider cannot be handed text that was not redacted.
3. **Every call recorded.** The router writes the usage row and the audit entry for every call that reached a
   provider, whatever happened: an answer, a failure, an unreadable answer, an exception, or a user who gave up.
4. **The clinician reviews.** The answer goes only to the feature's reader, as untrusted input. A clinical answer
   becomes a draft with `status: "draft"` and `requiresClinicianReview: true`, set by HC360, never by the answer. The
   module does not reference the Clinical module, so it cannot save or sign a note.

`AiGuardTests` proves each guard for each registered provider. It drives the real provider through the real gateway and
feature to a fake transport (the Anthropic SDK client, the OpenAI-compatible server) and checks what arrived: AI off
(unset or `false`) sends nothing to either provider; only redacted text arrives; a success, a failure and an abandoned
call are all recorded; and an answer that claims to be signed is still a draft for review. From the module's types it
checks that only the registry takes the providers and only the router takes the registry, that a provider can be
handed only a `RedactedAiRequest`, and that the tests cover every provider the module registers.

Calls from other modules through the platform's `IAiTextService` pass the same guards. No patient is known there, so
the redaction removes what it recognises by form (phone numbers, UHIDs, ABHA and Aadhaar numbers, e-mail addresses),
not names.

### What is sent

* **A command:** the fixed system prompt (instructions and the list of screens), and one user message with today's
  date, the current screen route, whether a patient is open, and the command.
* **A note:** the fixed system prompt, and one user message with the specialty and the dictation.
* **With either:** the JSON schema the answer must follow, the model name, the answer budget, the provider's key and a
  random correlation id.

Every text is redacted first. Never sent: the patient's id, names, UHID, phone numbers, e-mail or ABHA address, Aadhaar
number, the user's identity, or audio (audio goes only to the speech server). Names the redactor cannot know, such as
a relative mentioned in passing, stay in the dictation.

## Claude (Anthropic)

The official **Anthropic C# SDK** (`Anthropic` NuGet, 12.50.0), `client.Beta.Messages.Create`.

| | |
|---|---|
| Model | `ai.model`, default **`claude-opus-5-5`**. An invalid value falls back to the default. |
| Output | Structured outputs (`output_config.format` = the caller's JSON schema) for both prompts. |
| Effort | `low` for `assistant.*` (latency-sensitive voice commands), `high` for clinical structuring. Thinking is adaptive (the model default), and `max_tokens` is clamped to 1 024–32 000 so thinking plus the answer always fit. |
| Refusal fallback | `fallbacks: "default"` with beta `server-side-fallback-2026-07-01`. If the safety classifiers decline benign clinical text, the API re-runs the request on Anthropic's recommended fallback model instead of returning a refusal. Turning it off means removing it from `ClaudeRequestBuilder`. |
| Stop reasons | `refusal` → "The AI service declined this request. Please write the note manually." `max_tokens` → "The AI response was cut off before it was complete." Neither is ever returned as a draft. |
| Errors | SDK exceptions become staff-facing messages: invalid key, model unavailable, busy (429), temporarily unavailable (5xx), unreachable, timed out. Logs carry the error category only. |
| Client | One `AnthropicClient` per API key and timeout (`AnthropicClientProvider`), `MaxRetries` 2. The key is read from `ai.anthropic.secret` only when AI is enabled and Anthropic is the chosen provider. |

## OpenAI-compatible server

`ai.provider` = `openai`: `OpenAiCompatibleTextService`, a typed `HttpClient` that sends `POST {baseUrl}/chat/completions`.
The same format serves Azure OpenAI and model servers inside the hospital: vLLM, llama.cpp server and Ollama's OpenAI
endpoint.

| | |
|---|---|
| Address | `ai.openai` `baseUrl`: the address `/chat/completions` is added to, usually ending in `/v1` (one that already ends in `/chat/completions` is used as it is). It must be **https**, or plain http only to the HC360 server itself (`localhost`, `127.0.0.0/8`, `::1`), and it may not hold a user name, password, query or fragment. |
| Why no plain http on the network | Unlike Tally or PACS, an AI server on another computer must use https. Dictation and notes are clinical text even without the identifiers, the bearer key would travel in clear text, and someone else on the WiFi is a threat HC360 is designed against (SECURITY.md §1). A model server elsewhere on the network needs a TLS certificate the HC360 server trusts, for example behind a reverse proxy. The rule (`AiEndpoint`) is the speech server's too. |
| Key | `ai.openai.secret`, optional. When set it is sent as `Authorization: Bearer …` (Azure OpenAI's v1 endpoint accepts the resource key this way); otherwise no key is sent. It is decrypted only while AI is on and the address and model are valid. |
| Model | `ai.openai` `model`, required: the name the server lists at `/v1/models` (for Azure OpenAI, the deployment name), of letters, digits and `. _ : / -`, up to 128. There is no default. Usage rows and drafts show this name, not the one the server answers with. |
| Request | `model`; `messages` (the system prompt, then the messages); `max_completion_tokens`, the caller's answer budget (2 048 for a command, 16 000 for a note, at most 32 000), which a server that does not know the field ignores; `stream: false`; and with a schema, `response_format` `{ type: "json_schema", json_schema: { name, strict: true, schema } }`. No temperature or other sampling settings are sent: models that reason refuse some of them, so they are left to the server's own configuration. |
| Time limit | `ai.openai` `timeoutSeconds`, 10–300, default 90, for the whole call. `/ai/status` reports it, so screens wait long enough. |
| Retries | None. The POST is never retried (`Retry.DisableForUnsafeHttpMethods`): the server may already be working on it. A busy or failing server gives the user a message, and they can try again. |
| Answers | Checked before they are read. `finish_reason` `length` → "The AI response was cut off before it was complete."; `content_filter` or a `refusal` → "The AI service declined this request. Please write the note manually."; no text → "The AI service returned an empty answer."; anything that is not a chat completion → "The AI service returned an answer HC360 could not read." Reasoning returned beside the answer (`reasoning_content`) is not used. |
| Errors | 401 → the key was rejected; 404 → "The AI server does not offer the model '…' at this address."; 429 → busy; 5xx, or the circuit open → temporarily unavailable; another status → could not process; no connection → could not be reached; past the time limit → did not respond in time. Logs carry the status code or error category only, never the body, which may echo the text. |
| Client | The `OutboundHttp` handler (no redirects, TLS 1.2 or later), the correlation id, and the standard resilience handler with 320-second timeouts as a backstop. Answers over 1 MB are refused. |

What a hospital supplies:

1. A server that answers OpenAI chat completions with JSON-schema structured output (`response_format` of type
   `json_schema`), as current vLLM, llama.cpp server, Ollama and Azure OpenAI do. HC360 reads every answer as untrusted
   input as well: free text is a failure, and missing or malformed fields are left empty or dropped, never invented.
2. Its address, reachable from the HC360 server: https with a certificate the HC360 server trusts, or
   `http://localhost` for a server on the HC360 server itself. For Azure OpenAI,
   `https://<resource>.openai.azure.com/openai/v1`.
3. The model name (for Azure OpenAI, the deployment name), and a key if the server needs one.
4. A context window that holds the prompt and the answer budget. A note of up to 20,000 characters of dictation with
   its 16,000-token budget needs about 24,000 tokens; vLLM refuses a request that does not fit.
5. A time limit that suits the hardware: structuring a long note on a server without a GPU can take minutes (300
   seconds at most).

## Configuration (per facility, falling back to global)

| Key | Value |
|---|---|
| `ai.enabled` | `true` to allow AI calls. **Default false.** |
| `ai.provider` | `anthropic` (the default when unset), `openai`, or anything else (such as `disabled`) to turn AI text off. A value no provider has never falls back to another provider. |
| `ai.model` | Anthropic's model id, default `claude-opus-5-5` |
| `ai.timeoutSeconds` | Anthropic's time limit: 10–300, default 90. It bounds the whole call to the AI service, the SDK's retries included. |
| `ai.anthropic.secret` (encrypted) | The API key, as a JSON string or `{ "apiKey": "…" }` |
| `ai.openai` | `{ baseUrl, model, timeoutSeconds (90) }` for the OpenAI-compatible server (see above) |
| `ai.openai.secret` (encrypted) | Optional bearer key for that server, as a JSON string or `{ "apiKey": "…" }` |
| `ai.speech` | `{ provider: "none" \| "whisper", url, model ("whisper-1"), language ("en"), timeoutSeconds (120) }`. `none` (the default) means HC360 Desktop dictates with offline Windows speech. Audio is sent only while `ai.enabled` is on, and only to an `https://` URL without credentials (plain `http://` only to `localhost`): dictation is PHI (SEC-12). |
| `ai.speech.secret` (encrypted) | Optional bearer key for the Whisper-compatible server |
| `ai.limits` | `{ perUserPerHour: 60, perUserPerDay: 300 }` |

Malformed values disable the feature instead of failing the request. They are logged by key, never by value.

## Endpoints

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| POST | `/api/v1/ai/assistant/command` | `ai.assistant.use` | `{ text, context: { route, patientId } }` → `{ intent, parameters, confirmationText, requiresConfirmation, source ("rules" \| "ai") }`. Always answered; AI is used only as a fallback. |
| POST | `/api/v1/ai/clinical/structure-note` | `ai.dictation.use` | `{ transcript, specialty, patientId }` → `ClinicalNoteDraft`. 503 `ai.disabled`, 429 `ai.quota`, 502 `ai.provider_failed`. |
| POST | `/api/v1/ai/voice/transcribe` | `ai.dictation.use` or `ai.assistant.use` | multipart `audio` (WebM, WAV or Ogg, ≤ 10 MB; the declared type must match the file signature), `language` (ISO 639-1), `patientId` → `{ text, provider, language, durationMs }`. 503 `ai.speech_disabled` when no server STT is configured, `ai.enabled` is off or the URL is not https. Antiforgery is disabled because this is a bearer API. |
| GET | `/api/v1/ai/status` | any AI permission | → `{ enabled, provider, model, reason, serverSpeechToText, speechProvider, speechReason, timeoutSeconds }`, so the UI knows which AI actions to show and how long to wait for a draft (`timeoutSeconds`, the chosen provider's time limit, + 30 s) |
| GET | `/api/v1/ai/usage` | `ai.settings.manage` | paging + `purpose, userId, from, to` → `PagedResult<AiUsageDto>` with the user's display name (never text) |

Problem responses carry a machine-readable `code` extension (`ai.disabled`, `ai.speech_disabled`, `ai.quota`,
`ai.provider_failed`) that the UI switches on.

## Permissions

| Permission | Default roles |
|---|---|
| `ai.assistant.use` | Doctor, Nurse, FrontOffice, HospitalAdmin |
| `ai.dictation.use` | Doctor, HospitalAdmin |
| `ai.settings.manage` | HospitalAdmin |

## DI registrations (`AIModule.AddServices`)

* Settings and text providers:
  * Scoped `AiSettingsReader`.
  * Singleton `IAnthropicClientProvider` → `AnthropicClientProvider`.
  * Scoped `IAiTextProvider` → `ClaudeTextService` (key `anthropic`).
  * Typed `HttpClient` `OpenAiCompatibleTextService` (key `openai`) with the standard resilience handler: 320 s
    timeouts and no retries for unsafe methods. It is also registered as a scoped `IAiTextProvider`.
  * Scoped `AiTextProviderRegistry`, the providers by key.
  * Scoped `AiTextServiceRouter`, exposed as both the platform's `IAiTextService` and the module's async `IAiGateway`.
* Speech:
  * Typed `HttpClient` `WhisperSpeechToText` with the standard resilience handler: 180 s timeouts and no retries for
    unsafe methods.
  * Scoped `SpeechToTextRouter`, also exposed as the platform's `ISpeechToText`.
* Other scoped services:
  * `IAiUsageRecorder` → `AiUsageRecorder`
  * `IAiQuota` → `AiQuota`
  * `IPatientDirectory` → `PatientDirectory`
  * `AssistantCommandService`
  * `ClinicalNoteStructurer`
* `RateLimiterOptions`: adds the `ai` policy (sliding window, 20 requests per minute per user).

The platform's `IAiTextService` and `ISpeechToText` contracts are synchronous in `IsEnabled`. The routers read the
memory-cached settings once per scope, and this module's own code uses the async `IAiGateway` instead.

## Staff app

The shared client code is in `src/lib/ai.ts` (contracts and calls) and `src/lib/voice/` (voice capture: HC360 Desktop's
offline Windows speech, or a browser recording sent to `/ai/voice/transcribe`).

* **Administration › AI assistant** (`ai.settings.manage`): the status (provider, model, why AI is off), the usage
  list, and the settings form (`AI_SETTINGS` in `modules/admin/settingsForms.ts`). The form shows the switch, the
  provider, then only the chosen provider's fields: Anthropic's model and time limit, or the OpenAI-compatible
  server's address, model and time limit. Below them come browser speech, the limits, and the three keys (Anthropic,
  the OpenAI-compatible server, the speech server). Its checks mirror the server's: an AI address must be https or
  `http://localhost`, the server address may not have a query, and the model name follows the server's pattern.
* **Assistant** (the shell's assistant panel): typed or spoken commands (see *Behaviour*). A browser recording lasts at
  most 30 seconds.
* **Dictate with AI** (`modules/emr/dictation`), on the encounter workspace. The button shows while the note can be
  edited (in progress, or being amended), for users with `clinical.emr.write` and `ai.dictation.use`, when the status
  says AI is on.
  1. *Dictate.* The doctor speaks, types or pastes the consultation. Each spoken take is added at the end: HC360
     Desktop's offline speech gives one utterance per take (up to 60 seconds, ending after 1.5 seconds of silence); a
     browser records takes of up to 90 seconds when server speech-to-text is set up. The specialty starts as the
     encounter's department. A draft needs 10–20,000 characters, and the screen waits the server's `timeoutSeconds`
     plus 30 seconds, without sending the request again.
  2. *Check the draft.* The warnings (the first always says it is an AI draft); the placeholders the redactor left
     (`[NAME_1]`…), counted, for the doctor to replace; editable boxes for the chief complaint (suggested from the first
     line of the history when the note has none), history (S), examination (O), assessment and plan. Where the note
     already has text, the doctor chooses to add below it (the default) or replace it; an empty box never clears
     anything. A part that would exceed the note's limit blocks inserting until it is shortened. The ICD-10 suggestions
     are listed with the model's confidence, none ticked; codes the encounter already has cannot be ticked again.
  3. *Insert into note.* The boxes fill the note's form: nothing is saved until the doctor saves (Ctrl+S) or signs.
     The ticked codes are added straight away as provisional, active diagnoses; the first the hospital's ICD list
     accepts becomes primary if the encounter has none. A code the list doesn't have is not added, and a bar on the
     workspace says so until dismissed.

  While a placeholder remains in the note, the signing checklist warns about it.

## Not included

* No AI text or transcripts are stored anywhere, and no conversation history is kept. The idempotency store keeps a
  request's answer for a day so that a retry can be answered again; for the AI endpoints it keeps only that the
  request was answered (`.WithoutStoredResponse()`, checked by `StoredResponseTests`). A retry with the same
  `Idempotency-Key` therefore makes no second AI call and no second quota charge. It gets 409
  `idempotency.answer_not_kept`, which the screens word as "The connection dropped before the answer arrived".
* AI does not take any action by itself: no booking, no note saving, no ordering.
