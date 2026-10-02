import { api } from '@/lib/api';

/**
 * The assistant's command contract (src/server/Modules/HyFib.HealthCare360.Modules.AI/Features/Assistant): the records
 * in camelCase. The assistant only interprets commands; nothing is executed on the server. AI status and speech-to-text
 * are shared with clinical dictation in `@/lib/ai`.
 */

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

/** Interprets a typed or spoken command (offline rules first; AI only as a fallback when an administrator enabled it). */
export function sendAssistantCommand(request: AssistantCommandRequest): Promise<AssistantCommandResponse> {
  return api.post<AssistantCommandResponse>('/api/v1/ai/assistant/command', request, { timeoutMs: 60_000 });
}
