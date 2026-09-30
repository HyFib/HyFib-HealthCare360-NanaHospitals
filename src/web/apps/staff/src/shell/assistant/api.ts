import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * AI module contracts (src/server/Modules/HyFib.HealthCare360.Modules.AI/Features): the records in camelCase.
 * The assistant only interprets commands; nothing is executed on the server.
 */

/** GET /api/v1/ai/status → AiStatusDto: which AI actions the UI may offer (never contains secrets). */
export interface AiStatusDto {
  enabled: boolean;
  provider: string;
  model: string | null;
  reason: string | null;
  serverSpeechToText: boolean;
  speechProvider: string;
  speechReason: string | null;
}

/** AssistantContext: the current UI route and the patient on screen. */
export interface AssistantContext {
  route: string | null;
  patientId: string | null;
}

/** AssistantCommandRequest (text: 1–500 characters). */
export interface AssistantCommandRequest {
  text: string;
  context: AssistantContext | null;
}

/** AssistantIntents.All on the server. */
export type AssistantIntent =
  | 'navigate'
  | 'search_patient'
  | 'open_patient'
  | 'list_appointments'
  | 'book_follow_up'
  | 'create_note'
  | 'start_dictation'
  | 'unknown';

/**
 * AssistantCommandResponse. `parameters` are strings (see AssistantIntentParser's For* builders):
 * navigate {route, label}; open_patient {uhid, patientFound, patientId}; search_patient {query, phone, openIfSingleMatch};
 * list_appointments {date, scope}; book_follow_up {days, date, interval, patientId}; create_note {noteType, text, patientId};
 * start_dictation {patientId}. `source` is "rules" (offline parser) or "ai".
 */
export interface AssistantCommandResponse {
  intent: AssistantIntent | (string & {});
  parameters: Record<string, string>;
  confirmationText: string;
  requiresConfirmation: boolean;
  source: 'rules' | 'ai' | (string & {});
}

/** POST /api/v1/ai/voice/transcribe → TranscriptionResponse (the text is returned to the caller only, never stored). */
export interface TranscriptionResponse {
  text: string;
  provider: string;
  language: string;
  durationMs: number;
}

export const aiKeys = {
  all: ['ai'] as const,
  status: ['ai', 'status'] as const,
};

export const aiQueries = {
  status: () =>
    queryOptions({
      queryKey: aiKeys.status,
      queryFn: ({ signal }) => api.get<AiStatusDto>('/api/v1/ai/status', { signal }),
      staleTime: 5 * 60_000,
      retry: 1,
    }),
};

/** Interprets a typed or spoken command (offline rules first; AI only as a fallback when an administrator enabled it). */
export function sendAssistantCommand(request: AssistantCommandRequest): Promise<AssistantCommandResponse> {
  return api.post<AssistantCommandResponse>('/api/v1/ai/assistant/command', request, { timeoutMs: 60_000 });
}

/**
 * Server speech-to-text for browsers (multipart `audio`, optional `patientId` for the audit trail). The language is
 * left to the server's `ai.speech.language` setting. Transcribing on a CPU-only on-premises server can take a while.
 */
export function transcribeAudio(
  audio: Blob,
  fileName: string,
  patientId: string | null,
): Promise<TranscriptionResponse> {
  const form = new FormData();
  form.append('audio', audio, fileName);
  if (patientId) {
    form.append('patientId', patientId);
  }
  return api.post<TranscriptionResponse>('/api/v1/ai/voice/transcribe', form, { timeoutMs: 180_000 });
}
