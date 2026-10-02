import { formatNumber, isApiError } from '@hc360/core';
import {
  SPECIALTY_MAX_LENGTH,
  TRANSCRIPT_MAX_LENGTH,
  TRANSCRIPT_MIN_LENGTH,
  type ClinicalNoteDraft,
  type StructureNoteRequest,
  type SuggestedIcdCode,
} from '@/lib/ai';
import type { VoiceUse } from '@/lib/voice/recorder';
import type { AddDiagnosisRequest, DiagnosisDto } from '../api';
import { appendText, NOTE_FIELD_LIMITS, type EncounterFormValues } from '../encounterForm';

/**
 * AI dictation for the encounter note (SRS AIV-04..06), the pure part: preparing the dictation, turning the server's
 * SOAP draft into text the doctor reviews, merging it into the note, the ICD suggestions and plain-language errors.
 * DictationDialog.tsx is the screen.
 */

/** The note fields a draft fills, in the order of the note. */
export const SOAP_FIELDS = ['chiefComplaint', 'historyOfPresentIllness', 'examination', 'assessment', 'plan'] as const;
export type SoapField = (typeof SOAP_FIELDS)[number];
export type SoapText = Record<SoapField, string>;

/** Where the note already has text: add the reviewed text below it, or replace it. */
export type InsertMode = 'append' | 'replace';

export const SOAP_LABELS: Record<SoapField, string> = {
  chiefComplaint: 'Chief complaint',
  historyOfPresentIllness: 'History (Subjective)',
  examination: 'Examination (Objective)',
  assessment: 'Assessment',
  plan: 'Plan',
};

export const EMPTY_SOAP: SoapText = {
  chiefComplaint: '',
  historyOfPresentIllness: '',
  examination: '',
  assessment: '',
  plan: '',
};

/** Each browser recording ("take") can be up to 90 seconds; takes are added to the dictation one after another. */
export const DICTATION_TAKE_MS = 90_000;

export function soapOf(values: Pick<EncounterFormValues, SoapField>): SoapText {
  return {
    chiefComplaint: values.chiefComplaint,
    historyOfPresentIllness: values.historyOfPresentIllness,
    examination: values.examination,
    assessment: values.assessment,
    plan: values.plan,
  };
}

// ---------------------------------------------------------------------------------------------------- placeholders

/** The server's placeholder format (PhiRedactor): [NAME_1], [PHONE_2], [UHID_1], [ID_3] … */
const PLACEHOLDER = /\[[A-Z]+_\d+\]/g;

/** Placeholders in the texts, each once, in the order they first appear. */
export function findPlaceholders(...texts: readonly string[]): string[] {
  const found = new Set<string>();
  for (const text of texts) {
    for (const match of text.matchAll(PLACEHOLDER)) {
      found.add(match[0]);
    }
  }
  return [...found];
}

/** Placeholders still in the note's SOAP fields. */
export function notePlaceholders(values: Pick<EncounterFormValues, SoapField>): string[] {
  return findPlaceholders(...SOAP_FIELDS.map((field) => values[field]));
}

/** The sign checklist's warning while placeholders from an AI draft remain in the note. */
export function placeholderSignWarning(placeholders: readonly string[]): string {
  return `The note still has placeholders from the AI draft: ${placeholders.join(', ')}. Replace them with the right words before signing.`;
}

// ---------------------------------------------------------------------------------------------------- the dictation

/** Characters the server refuses in text (SafeText): control characters other than tab and new line. */
function isRefused(char: string): boolean {
  const code = char.charCodeAt(0);
  return (code < 0x20 && char !== '\t' && char !== '\n') || (code >= 0x7f && code <= 0x9f);
}

/** Text as sent: Windows line ends made "\n", control characters (pasted from other programs) removed, trimmed. */
export function cleanText(text: string): string {
  return Array.from(text.replace(/\r\n?/g, '\n'))
    .filter((char) => !isRefused(char))
    .join('')
    .trim();
}

/** Why the dictation cannot be turned into a draft yet, or null when it can. */
export function transcriptProblem(transcript: string): string | null {
  const length = cleanText(transcript).length;
  if (length === 0) {
    return 'Speak or type the consultation first.';
  }
  if (length < TRANSCRIPT_MIN_LENGTH) {
    return `Add a little more: a draft needs at least ${TRANSCRIPT_MIN_LENGTH} characters.`;
  }
  if (length > TRANSCRIPT_MAX_LENGTH) {
    return `The dictation is ${formatNumber(length)} characters; the limit is ${formatNumber(TRANSCRIPT_MAX_LENGTH)}. Shorten it, or make the draft in two parts.`;
  }
  return null;
}

/** Adds what was said at the end of the dictation, on a new line, cut at the 20,000-character limit. */
export function appendTake(transcript: string, take: string): string {
  return appendText(transcript, take.trim()).slice(0, TRANSCRIPT_MAX_LENGTH);
}

/** The encounter's department as the specialty the AI writes for (the doctor can change it). */
export function defaultSpecialty(departmentName: string | null | undefined): string {
  return (departmentName ?? '').trim().slice(0, SPECIALTY_MAX_LENGTH);
}

export function structureRequest(
  transcript: string,
  specialty: string,
  patientId: string | null,
): StructureNoteRequest {
  return {
    transcript: cleanText(transcript),
    specialty: cleanText(specialty).slice(0, SPECIALTY_MAX_LENGTH) || null,
    patientId,
  };
}

/** Closing would throw away a dictation or a draft, so the doctor is asked first. */
export function needsDiscardConfirmation(transcript: string, hasDraft: boolean): boolean {
  return hasDraft || transcript.trim() !== '';
}

// ---------------------------------------------------------------------------------------------------- the review

/** A list marker the model may put before a line: "-", "*", "•", "1." or "1)". */
const LIST_MARKER = /^(?:[-*•]|\d+[.)])\s+/;

/** Cuts text to the limit at a space (or mid-word when a word is longer than half the limit). */
function fitAtWord(text: string, limit: number): string {
  if (text.length <= limit) {
    return text;
  }
  const cut = text.slice(0, limit);
  const space = cut.lastIndexOf(' ');
  return (space > limit / 2 ? cut.slice(0, space) : cut).trimEnd();
}

/** The chief complaint suggested from a draft: the first line of the Subjective section, without a list marker. */
export function suggestChiefComplaint(subjective: string): string {
  const firstLine =
    subjective
      .split('\n')
      .map((line) => line.trim())
      .find(Boolean) ?? '';
  return fitAtWord(firstLine.replace(LIST_MARKER, '').trim(), NOTE_FIELD_LIMITS.chiefComplaint);
}

/**
 * What the doctor reviews: the draft's sections under the note's names. A chief complaint is suggested only when the
 * note has none, so one the doctor wrote is never doubled.
 */
export function reviewFromDraft(draft: ClinicalNoteDraft, note: SoapText): SoapText {
  return {
    chiefComplaint: note.chiefComplaint.trim() ? '' : suggestChiefComplaint(draft.subjective),
    historyOfPresentIllness: draft.subjective.trim(),
    examination: draft.objective.trim(),
    assessment: draft.assessment.trim(),
    plan: draft.plan.trim(),
  };
}

/** True when the doctor emptied every box. */
export function isEmptyReview(review: SoapText): boolean {
  return SOAP_FIELDS.every((field) => !review[field].trim());
}

/**
 * The note fields to change: every reviewed section with text, added below the note's text (on a new line) or
 * replacing it. An empty box leaves that part of the note as it is, in both modes.
 */
export function mergeIntoNote(note: SoapText, review: SoapText, mode: InsertMode): Partial<SoapText> {
  const patch: Partial<SoapText> = {};
  for (const field of SOAP_FIELDS) {
    const incoming = review[field].trim();
    if (incoming) {
      patch[field] = mode === 'replace' ? incoming : appendText(note[field], incoming);
    }
  }
  return patch;
}

export interface SectionOverflow {
  field: SoapField;
  /** Length of the note field after inserting. */
  length: number;
  limit: number;
}

/** Sections that would make their note field longer than it may be. Inserting waits until they fit. */
export function overflowingSections(note: SoapText, review: SoapText, mode: InsertMode): SectionOverflow[] {
  const patch = mergeIntoNote(note, review, mode);
  return SOAP_FIELDS.flatMap((field) => {
    const value = patch[field];
    const limit = NOTE_FIELD_LIMITS[field];
    return value !== undefined && value.length > limit ? [{ field, length: value.length, limit }] : [];
  });
}

export function overflowMessage(overflow: SectionOverflow, mode: InsertMode): string {
  const excess = formatNumber(overflow.length - overflow.limit);
  const limit = formatNumber(overflow.limit);
  return mode === 'append'
    ? `Added to what the note already has, this part would be ${formatNumber(overflow.length)} characters; the limit is ${limit}. Shorten it by ${excess}, or choose “Replace what’s written”.`
    : `This part is ${formatNumber(overflow.length)} characters; the limit is ${limit}. Shorten it by ${excess}.`;
}

/** What inserting does to this part of the note, shown under its box. */
export function sectionStatus(noteText: string, reviewText: string, mode: InsertMode): string {
  const noteHasText = noteText.trim() !== '';
  const draftHasText = reviewText.trim() !== '';
  if (!noteHasText) {
    return draftHasText ? 'Empty in the note, so this text goes in.' : 'Empty in the note and in the draft.';
  }
  if (!draftHasText) {
    return 'The note already has text here. It stays as it is.';
  }
  return mode === 'append'
    ? 'The note already has text here. This is added below it.'
    : 'The note already has text here. This replaces it.';
}

// ---------------------------------------------------------------------------------------------------- ICD suggestions

/** "87% confidence" (the model's own estimate, 0–1). */
export function confidenceLabel(confidence: number): string {
  const share = Number.isFinite(confidence) ? Math.min(1, Math.max(0, confidence)) : 0;
  return `${Math.round(share * 100)}% confidence`;
}

/** Suggested codes this encounter already has as a diagnosis: adding them again would record them twice. */
export function recordedCodes(
  suggestions: readonly SuggestedIcdCode[],
  diagnoses: readonly DiagnosisDto[] | undefined,
): Set<string> {
  const recorded = new Set((diagnoses ?? []).flatMap((d) => (d.icdCode ? [d.icdCode.toUpperCase()] : [])));
  return new Set(suggestions.map((s) => s.code).filter((code) => recorded.has(code.toUpperCase())));
}

/** The ticked codes in the order suggested (most specific first), leaving out codes already recorded. */
export function tickedCodes(
  suggestions: readonly SuggestedIcdCode[],
  ticked: ReadonlySet<string>,
  recorded: ReadonlySet<string>,
): string[] {
  return suggestions.map((s) => s.code).filter((code) => ticked.has(code) && !recorded.has(code));
}

/** A suggested code as a diagnosis of the encounter: provisional and active; the server takes the catalogue's title. */
export function diagnosisRequest(code: string, isPrimary: boolean): AddDiagnosisRequest {
  return { icdCode: code, title: null, type: 'Provisional', isPrimary, onsetDate: null, status: 'Active', notes: null };
}

export type NotAddedReason = 'not-in-catalogue' | 'unreachable' | 'not-allowed' | 'failed';

export interface NotAdded {
  code: string;
  reason: NotAddedReason;
}

export interface DiagnosesOutcome {
  added: DiagnosisDto[];
  notAdded: NotAdded[];
}

/** Why the server did not add a code. A code missing from the ICD catalogue comes back as a field error on icdCode. */
export function notAddedReason(error: unknown): NotAddedReason {
  if (!isApiError(error)) {
    return 'failed';
  }
  if (error.status === 400 && Object.keys(error.fieldErrors).some((key) => key.toLowerCase() === 'icdcode')) {
    return 'not-in-catalogue';
  }
  if (error.isNetworkError) {
    return 'unreachable';
  }
  return error.isForbidden ? 'not-allowed' : 'failed';
}

/**
 * Adds the ticked codes one at a time, in order. When the encounter has no primary diagnosis yet, the first code the
 * catalogue accepts becomes primary; an existing primary is never replaced (pass `hasPrimary: true` while the
 * diagnoses are unknown). A code that is not added is reported and the others are still tried.
 */
export async function addDiagnoses(
  codes: readonly string[],
  hasPrimary: boolean,
  add: (request: AddDiagnosisRequest) => Promise<DiagnosisDto>,
): Promise<DiagnosesOutcome> {
  const added: DiagnosisDto[] = [];
  const notAdded: NotAdded[] = [];
  for (const code of codes) {
    try {
      added.push(await add(diagnosisRequest(code, !hasPrimary && added.length === 0)));
    } catch (error) {
      notAdded.push({ code, reason: notAddedReason(error) });
    }
  }
  return { added, notAdded };
}

/** "A", "A and B", "A, B and C". */
function listOf(items: readonly string[]): string {
  return items.length <= 1 ? (items[0] ?? '') : `${items.slice(0, -1).join(', ')} and ${items.at(-1)}`;
}

/** What to tell the doctor about suggested codes that were not added, grouped by reason. */
export function notAddedSummary(notAdded: readonly NotAdded[]): string {
  const codes = (reason: NotAddedReason) => notAdded.filter((n) => n.reason === reason).map((n) => n.code);
  const sentences: string[] = [];
  const missing = codes('not-in-catalogue');
  if (missing.length > 0) {
    sentences.push(`${listOf(missing)} ${missing.length === 1 ? 'is' : 'are'} not in the hospital’s ICD list.`);
  }
  const unreachable = codes('unreachable');
  if (unreachable.length > 0) {
    sentences.push(`${listOf(unreachable)} could not be added because the server could not be reached.`);
  }
  const notAllowed = codes('not-allowed');
  if (notAllowed.length > 0) {
    sentences.push(`${listOf(notAllowed)} could not be added: your account may not add diagnoses.`);
  }
  const failed = codes('failed');
  if (failed.length > 0) {
    sentences.push(`${listOf(failed)} could not be added.`);
  }
  sentences.push('Add the right diagnosis from the Diagnoses section.');
  return sentences.join(' ');
}

/** What the review's Insert button will do, or why it cannot yet. */
export function insertHint(overflowing: boolean, nothingToInsert: boolean, codesTicked: number): string {
  if (overflowing) {
    return 'Shorten the parts marked in red before inserting.';
  }
  if (nothingToInsert) {
    return 'Nothing to insert: every box is empty and no code is ticked.';
  }
  if (codesTicked > 0) {
    return `Inserting fills the note for you to check and save, and adds the ${codesTicked === 1 ? 'ticked code' : `${codesTicked} ticked codes`} to the diagnoses straight away.`;
  }
  return 'Inserting fills the note for you to check and save. Nothing is saved before that.';
}

export interface InsertedToast {
  intent: 'success' | 'warning';
  title: string;
  body: string;
}

/** The message after inserting: what changed, and what to do next (a signed note is changed by an amendment). */
export function insertedToast(fieldsChanged: number, diagnosesAdded: number, amending = false): InsertedToast {
  const diagnoses = `${diagnosesAdded} provisional ${diagnosesAdded === 1 ? 'diagnosis was' : 'diagnoses were'} added.`;
  if (fieldsChanged > 0) {
    const next = amending ? 'Review it, then record the amendment.' : 'Review it and save (Ctrl+S).';
    return {
      intent: 'success',
      title: 'Draft added to the note',
      body: diagnosesAdded > 0 ? `${next} ${diagnoses}` : next,
    };
  }
  if (diagnosesAdded > 0) {
    return { intent: 'success', title: 'Diagnoses added', body: `${diagnoses} The note’s text was not changed.` };
  }
  return { intent: 'warning', title: 'Nothing was added', body: 'The ticked codes could not be added.' };
}

// ---------------------------------------------------------------------------------------------------- errors

/**
 * Plain words for a failed draft or speech-to-text request, shown in the dialog (never a raw error). The AI endpoints
 * answer with Problem Details carrying a `code`: ai.disabled, ai.speech_disabled, ai.quota, ai.provider_failed and
 * patient.restricted; the `ai` rate limit (20 requests a minute per user) answers 429 without one.
 */
export function dictationErrorMessage(error: unknown, during: 'draft' | 'speech'): string {
  const fallback =
    during === 'draft'
      ? 'The draft could not be made. Please try again.'
      : 'Your speech could not be turned into text. Please try again, or type instead.';
  if (error instanceof DOMException && error.name === 'TimeoutError') {
    return during === 'draft'
      ? 'The AI service took too long to answer. Try again in a moment; a shorter dictation is quicker.'
      : 'The speech service took too long to answer. Try again, or type the dictation.';
  }
  if (!isApiError(error)) {
    return fallback;
  }
  switch (error.code) {
    case 'ai.disabled':
      return 'AI help has been turned off, so a draft cannot be made now. Write the note yourself; your dictation is still here to copy.';
    case 'ai.speech_disabled':
      return 'Speaking in the browser needs server speech-to-text, which is not set up. Type the dictation, or use HC360 Desktop.';
    case 'ai.quota':
      return 'You have reached your AI usage limit for now. Try again later, or write the note yourself.';
    case 'ai.provider_failed':
      return during === 'draft'
        ? 'The AI service could not make a draft this time. Try again, or write the note yourself.'
        : 'The speech service could not turn this recording into text. Try again, or type the dictation.';
    case 'patient.restricted':
      return 'This is a restricted record, so AI dictation cannot be used for this patient. Write the note yourself.';
    default:
      break;
  }
  if (error.isNetworkError) {
    return during === 'draft'
      ? 'The draft did not arrive. Check the WiFi connection and try again; your dictation is still here.'
      : 'Cannot reach the HealthCare360 server. Check the WiFi connection and try again.';
  }
  if (error.status === 429) {
    return 'Too many requests in a short time. Wait a minute, then try again.';
  }
  if (error.status === 401) {
    return 'Your session has ended. Please sign in again.';
  }
  if (error.isForbidden) {
    return 'You do not have permission to use AI dictation. Ask your administrator if you need it.';
  }
  if (error.isValidation) {
    // The server's field messages are written for staff ("The dictation is too short to structure.").
    return error.userMessage;
  }
  if (error.status === 413) {
    return 'The recording is too long to send. Stop and start again more often.';
  }
  if (error.status >= 500) {
    return 'The server had a problem. Please try again in a moment.';
  }
  return fallback;
}

/** Voice input for dictation: 90-second takes, worded for typing the dictation instead. */
export const DICTATION_VOICE: VoiceUse = {
  maxRecordingMs: DICTATION_TAKE_MS,
  typeInstead: 'type the dictation',
  describeError: (error) => dictationErrorMessage(error, 'speech'),
};
