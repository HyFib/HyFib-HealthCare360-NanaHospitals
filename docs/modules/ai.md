# AI module

Voice and typed commands for staff, server speech-to-text, and clinical dictation structured into SOAP drafts. AI is
**off by default**, and nothing is ever saved without a clinician.

| | |
|---|---|
| Project | `src/server/Modules/HyFib.HealthCare360.Modules.AI` (module key and schema `ai`; references Patients) |
| Schema | `ai.usage_records` (`AiUsage`, `[NotAudited]`; indexes `(user_id, created_at)` and `created_at`) |
| Requirements | AIV-01..06, ADR-011 (AI provider), SEC-12 (AI data protection) |
| Tests | `tests/Modules/HyFib.HealthCare360.Modules.AI.Tests`: 215 tests covering the redactor, spoken numbers, the offline command parser, the AI answer interpreter, the command service, note parsing and structuring, the Claude request builder and response mapper, SDK error mapping, settings, audio and Whisper, validators and DI |

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
* **Redaction before anything leaves the hospital.** `PhiRedactor` is pure, offline and deterministic. It replaces
  e-mail and ABHA addresses, UHIDs (keyword-introduced or `{FAC}{YY}{SEQ}`), ABHA and Aadhaar numbers, Indian mobile
  and landline numbers, other long digit runs, and the context patient's identifiers and names. Names include the
  guardian and emergency contact, loaded by `PatientDirectory`. Each distinct value gets a stable placeholder such
  as `[PHONE_1]`. Clinical content (vitals, doses, dates) is untouched.
* **Clinical drafts.** `POST /clinical/structure-note` returns a SOAP draft with `status: "draft"` and
  `requiresClinicianReview: true`. It includes up to 5 well-formed ICD-10 suggestions (confidence 0–1) and warnings.
  The first warning always says the draft is AI-generated, and another says how many identifiers were replaced.
  Nothing is stored; the clinician edits and saves through the EMR. Restricted (VIP) patients need the same access
  as in the EMR (`PatientAccessGuard`).
* **Accounting, never content.**
  * Every AI call writes an `AiUsage` row (user, facility, patient, purpose, provider, model, tokens, latency,
    success, error) and a **clinical audit entry** `ai.<purpose>` (`ai.assistant.command`, `ai.clinical.structure-note`,
    `ai.voice.transcribe`) in one transaction.
  * Neither record contains the transcript, prompt or answer.
  * Quotas are per user: `ai.limits`, default 60 per hour and 300 per day, counted from the usage rows.
  * The `ai` rate-limit policy allows 20 requests per minute per user on every AI endpoint.

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
| Client | One `AnthropicClient` per API key and timeout (`AnthropicClientProvider`), `MaxRetries` 2. The key is read from `ai.anthropic.secret` only when AI is enabled. |

## Configuration (per facility, falling back to global)

| Key | Value |
|---|---|
| `ai.enabled` | `true` to allow AI calls. **Default false.** |
| `ai.provider` | `anthropic` (default) or anything else to disable |
| `ai.model` | Model id, default `claude-opus-5-5` |
| `ai.timeoutSeconds` | 10–300, default 90 |
| `ai.anthropic.secret` (encrypted) | The API key, as a JSON string or `{ "apiKey": "…" }` |
| `ai.speech` | `{ provider: "none" \| "whisper", url, model ("whisper-1"), language ("en"), timeoutSeconds (120) }`. `none` (the default) means HC360 Desktop dictates with offline Windows speech. |
| `ai.speech.secret` (encrypted) | Optional bearer key for the Whisper-compatible server |
| `ai.limits` | `{ perUserPerHour: 60, perUserPerDay: 300 }` |

Malformed values disable the feature instead of failing the request. They are logged by key, never by value.

## Endpoints

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| POST | `/api/v1/ai/assistant/command` | `ai.assistant.use` | `{ text, context: { route, patientId } }` → `{ intent, parameters, confirmationText, requiresConfirmation, source ("rules" \| "ai") }`. Always answered; AI is used only as a fallback. |
| POST | `/api/v1/ai/clinical/structure-note` | `ai.dictation.use` | `{ transcript, specialty, patientId }` → `ClinicalNoteDraft`. 503 `ai.disabled`, 429 `ai.quota`, 502 `ai.provider_failed`. |
| POST | `/api/v1/ai/voice/transcribe` | `ai.dictation.use` or `ai.assistant.use` | multipart `audio` (WebM, WAV or Ogg, ≤ 10 MB; the declared type must match the file signature), `language` (ISO 639-1), `patientId` → `{ text, provider, language, durationMs }`. 503 `ai.speech_disabled` when no server STT is configured. Antiforgery is disabled because this is a bearer API. |
| GET | `/api/v1/ai/status` | any AI permission | → `{ enabled, provider, model, reason, serverSpeechToText, speechProvider, speechReason }`, so the UI knows which AI actions to show |
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

* Settings and Claude:
  * Scoped `AiSettingsReader`.
  * Singleton `IAnthropicClientProvider` → `AnthropicClientProvider`.
  * Scoped `ClaudeTextService`.
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

## Not included

* No AI text or transcripts are stored anywhere, and no conversation history is kept.
* AI does not take any action by itself: no booking, no note saving, no ordering.
