import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * AI module contracts shared by the assistant (shell/assistant), clinical dictation (modules/emr/dictation) and the AI
 * administration screen: the server's records (src/server/Modules/HyFib.HealthCare360.Modules.AI/Features) in
 * camelCase. Nothing the AI returns is saved by the server; the clinician reviews it and saves through the normal
 * screens.
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
  /** How long an AI call may take on the server (`ai.timeoutSeconds`, retries included). */
  timeoutSeconds: number;
}

/** POST /api/v1/ai/voice/transcribe → TranscriptionResponse (the text is returned to the caller only, never stored). */
export interface TranscriptionResponse {
  text: string;
  provider: string;
  language: string;
  durationMs: number;
}

/** StructureNoteRequestValidator: the transcript is 10–20,000 characters, the specialty at most 100. */
export const TRANSCRIPT_MIN_LENGTH = 10;
export const TRANSCRIPT_MAX_LENGTH = 20_000;
export const SPECIALTY_MAX_LENGTH = 100;

/** POST /api/v1/ai/clinical/structure-note. `patientId` lets the server remove that patient's identifiers too. */
export interface StructureNoteRequest {
  transcript: string;
  /** Helps the model use the right terms; null means general medicine. */
  specialty: string | null;
  patientId: string | null;
}

/** A well-formed WHO ICD-10 code (most specific first). It is not checked against the hospital's ICD catalogue. */
export interface SuggestedIcdCode {
  code: string;
  title: string;
  /** 0–1. */
  confidence: number;
}

/**
 * ClinicalNoteDraft: an AI SOAP draft for the clinician to review, never stored by the server. Each section is at most
 * 4,000 characters of short lines separated by "\n", and may be empty. Names, phone numbers and other identifiers
 * appear as placeholders such as [NAME_1]: they were removed before the dictation left the hospital and are
 * deliberately not put back. The first warning always says that this is an AI draft to review.
 */
export interface ClinicalNoteDraft {
  status: 'draft' | (string & {});
  subjective: string;
  objective: string;
  assessment: string;
  plan: string;
  /** At most five. */
  suggestedIcd: SuggestedIcdCode[];
  warnings: string[];
  requiresClinicianReview: boolean;
  model: string | null;
  generatedAt: string;
  identifiersRedacted: number;
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

/** How much longer than the server the screen waits, so the server's own answer (a draft, or "took too long") arrives. */
const SERVER_MARGIN_MS = 30_000;

/** The server's `ai.timeoutSeconds` when the status is not known yet. */
const DEFAULT_AI_TIMEOUT_SECONDS = 90;

/** How long to wait for a draft: the server's deadline for the AI call, plus a margin. */
export function structureNoteTimeoutMs(serverTimeoutSeconds: number | undefined): number {
  return (serverTimeoutSeconds ?? DEFAULT_AI_TIMEOUT_SECONDS) * 1000 + SERVER_MARGIN_MS;
}

/**
 * Turns a dictation into a SOAP draft with ICD-10 suggestions (permission `ai.dictation.use`). Every call is audited
 * and counts towards the user's AI quota. The two minutes are one deadline for the whole call: a request that ran out
 * of time is not sent again (it may still be running on the server) and rejects with a TimeoutError DOMException.
 */
export function structureNote(
  request: StructureNoteRequest,
  serverTimeoutSeconds?: number,
): Promise<ClinicalNoteDraft> {
  const deadline = structureNoteTimeoutMs(serverTimeoutSeconds);
  return api.post<ClinicalNoteDraft>('/api/v1/ai/clinical/structure-note', request, {
    signal: AbortSignal.timeout(deadline),
    timeoutMs: deadline + 5_000,
  });
}
