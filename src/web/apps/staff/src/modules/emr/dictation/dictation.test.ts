import { ApiError } from '@hc360/core';
import { describe, expect, it, vi } from 'vitest';
import type { AiStatusDto, ClinicalNoteDraft, SuggestedIcdCode } from '@/lib/ai';
import { microphoneProblem, shouldStopRecording, voiceAvailability } from '@/lib/voice/recorder';
import type { AddDiagnosisRequest, DiagnosisDto } from '../api';
import {
  addDiagnoses,
  appendTake,
  cleanText,
  confidenceLabel,
  defaultSpecialty,
  DICTATION_VOICE,
  dictationErrorMessage,
  diagnosisRequest,
  EMPTY_SOAP,
  findPlaceholders,
  insertedToast,
  insertHint,
  isEmptyReview,
  mergeIntoNote,
  needsDiscardConfirmation,
  notAddedReason,
  notAddedSummary,
  notePlaceholders,
  overflowingSections,
  overflowMessage,
  placeholderSignWarning,
  recordedCodes,
  reviewFromDraft,
  sectionStatus,
  soapOf,
  structureRequest,
  suggestChiefComplaint,
  tickedCodes,
  transcriptProblem,
  type SoapText,
} from './dictation';

const PATIENT = '3f2b8c1e-4a5d-4e6f-9a7b-1c2d3e4f5a6b';

const status: AiStatusDto = {
  enabled: true,
  provider: 'anthropic',
  model: 'claude-opus-5-5',
  reason: null,
  serverSpeechToText: true,
  speechProvider: 'whisper',
  speechReason: null,
  timeoutSeconds: 90,
};

function draft(overrides: Partial<ClinicalNoteDraft> = {}): ClinicalNoteDraft {
  return {
    status: 'draft',
    subjective: 'Fever for 3 days with dry cough\nNo breathlessness',
    objective: 'Temp 101 F, pulse 96\nThroat congested',
    assessment: 'Acute pharyngitis',
    plan: 'Tab paracetamol 650 mg TDS x 3 days\nSalt-water gargles',
    suggestedIcd: [],
    warnings: ['AI-generated draft: review and correct every section before saving. Nothing has been saved or signed.'],
    requiresClinicianReview: true,
    model: 'claude-opus-5-5',
    generatedAt: '2026-10-02T09:30:00Z',
    identifiersRedacted: 0,
    ...overrides,
  };
}

function problem(status: number, code?: string, errors?: Record<string, string[]>) {
  return new ApiError({ status, code, title: 'Raw server title', detail: 'Raw server detail with internals', errors });
}

function diagnosis(icdCode: string | null, isPrimary = false): DiagnosisDto {
  return {
    id: crypto.randomUUID(),
    patientId: PATIENT,
    encounterId: 'enc-1',
    icdCode,
    title: icdCode ?? 'Free text',
    type: 'Provisional',
    isPrimary,
    onsetDate: null,
    status: 'Active',
    resolvedOn: null,
    recordedAt: '2026-10-02T09:00:00Z',
    notes: null,
    version: 1,
  };
}

const suggestions: SuggestedIcdCode[] = [
  { code: 'J02.9', title: 'Acute pharyngitis, unspecified', confidence: 0.86 },
  { code: 'R50.9', title: 'Fever, unspecified', confidence: 0.41 },
  { code: 'J06.9', title: 'Acute upper respiratory infection, unspecified', confidence: 0.35 },
];

describe('placeholders', () => {
  it('finds each placeholder once, in the order it first appears, across texts', () => {
    expect(
      findPlaceholders('Seen with [NAME_2], son of [NAME_1]. Call [PHONE_1].', 'Review [NAME_1] on [UHID_1]'),
    ).toEqual(['[NAME_2]', '[NAME_1]', '[PHONE_1]', '[UHID_1]']);
  });

  it('ignores brackets that are not the server’s placeholders', () => {
    expect(findPlaceholders('BP [sitting] 130/80, [name_1], [NAME], [NAME_], NAME_1, [ID_12]')).toEqual(['[ID_12]']);
    expect(findPlaceholders('')).toEqual([]);
  });

  it('checks every SOAP field of the note and words the sign warning', () => {
    const values = { ...EMPTY_SOAP, chiefComplaint: 'Fever', plan: 'Inform [NAME_1] at [PHONE_1]' };
    expect(notePlaceholders(values)).toEqual(['[NAME_1]', '[PHONE_1]']);
    expect(notePlaceholders(EMPTY_SOAP)).toEqual([]);
    expect(placeholderSignWarning(['[NAME_1]'])).toBe(
      'The note still has placeholders from the AI draft: [NAME_1]. Replace them with the right words before signing.',
    );
  });
});

describe('the dictation', () => {
  it('cleans what is sent: line ends, control characters from pasted text, outer spaces', () => {
    expect(cleanText('  Fever\r\nCough\rBP 120/80\u0007\u000b\tok\u0085  ')).toBe('Fever\nCough\nBP 120/80\tok');
  });

  it('needs at least 10 characters and at most 20,000 before a draft can be made', () => {
    expect(transcriptProblem('   ')).toBe('Speak or type the consultation first.');
    expect(transcriptProblem(' Fever 3d ')).toContain('at least 10 characters');
    expect(transcriptProblem('Fever 3 days')).toBeNull();
    expect(transcriptProblem('a'.repeat(20_000))).toBeNull();
    expect(transcriptProblem('a'.repeat(20_001))).toBe(
      'The dictation is 20,001 characters; the limit is 20,000. Shorten it, or make the draft in two parts.',
    );
  });

  it('adds each spoken take at the end on a new line, never beyond the limit', () => {
    expect(appendTake('', '  fever for three days ')).toBe('fever for three days');
    expect(appendTake('fever for three days  \n', 'no cough')).toBe('fever for three days\nno cough');
    expect(appendTake('a'.repeat(19_995), 'more words here')).toHaveLength(20_000);
  });

  it('suggests the encounter’s department as the specialty and sends an empty one as null', () => {
    expect(defaultSpecialty(' General Medicine ')).toBe('General Medicine');
    expect(defaultSpecialty(null)).toBe('');
    expect(defaultSpecialty('x'.repeat(150))).toHaveLength(100);
    expect(structureRequest(' Fever 3 days\r\n', '  ', PATIENT)).toEqual({
      transcript: 'Fever 3 days',
      specialty: null,
      patientId: PATIENT,
    });
    expect(structureRequest('Fever 3 days', ' Paediatrics ', null).specialty).toBe('Paediatrics');
  });

  it('asks before closing only when something would be lost', () => {
    expect(needsDiscardConfirmation('  ', false)).toBe(false);
    expect(needsDiscardConfirmation('Fever', false)).toBe(true);
    expect(needsDiscardConfirmation('', true)).toBe(true);
  });
});

describe('the review', () => {
  it('suggests the chief complaint from the first line of the history, without a list marker', () => {
    expect(suggestChiefComplaint('\n  - Fever for 3 days with dry cough\nNo breathlessness')).toBe(
      'Fever for 3 days with dry cough',
    );
    expect(suggestChiefComplaint('1) Headache')).toBe('Headache');
    expect(suggestChiefComplaint('• Burning micturition')).toBe('Burning micturition');
    expect(suggestChiefComplaint('')).toBe('');
  });

  it('cuts a long first line at a word so it fits the chief complaint', () => {
    const suggestion = suggestChiefComplaint(`${'pain '.repeat(300)}end`);
    expect(suggestion.length).toBeLessThanOrEqual(1000);
    expect(suggestion.endsWith('pain')).toBe(true);
  });

  it('fills the review from the draft, suggesting a chief complaint only for an empty one', () => {
    expect(reviewFromDraft(draft(), EMPTY_SOAP)).toEqual({
      chiefComplaint: 'Fever for 3 days with dry cough',
      historyOfPresentIllness: 'Fever for 3 days with dry cough\nNo breathlessness',
      examination: 'Temp 101 F, pulse 96\nThroat congested',
      assessment: 'Acute pharyngitis',
      plan: 'Tab paracetamol 650 mg TDS x 3 days\nSalt-water gargles',
    });
    expect(reviewFromDraft(draft(), { ...EMPTY_SOAP, chiefComplaint: 'Sore throat' }).chiefComplaint).toBe('');
    expect(reviewFromDraft(draft({ objective: '', subjective: '' }), EMPTY_SOAP)).toMatchObject({
      chiefComplaint: '',
      historyOfPresentIllness: '',
      examination: '',
    });
  });

  it('knows when every box was emptied', () => {
    expect(isEmptyReview(EMPTY_SOAP)).toBe(true);
    expect(isEmptyReview({ ...EMPTY_SOAP, plan: '  \n ' })).toBe(true);
    expect(isEmptyReview({ ...EMPTY_SOAP, plan: 'Review' })).toBe(false);
  });

  it('picks the SOAP fields out of the form values', () => {
    expect(
      soapOf({
        chiefComplaint: 'a',
        historyOfPresentIllness: 'b',
        examination: 'c',
        assessment: 'd',
        plan: 'e',
        followUpAdvice: 'not SOAP',
      } as SoapText & { followUpAdvice: string }),
    ).toEqual({ chiefComplaint: 'a', historyOfPresentIllness: 'b', examination: 'c', assessment: 'd', plan: 'e' });
  });
});

describe('inserting into the note', () => {
  const note: SoapText = { ...EMPTY_SOAP, chiefComplaint: 'Sore throat', examination: 'Tonsils enlarged  \n' };
  const review: SoapText = {
    chiefComplaint: '',
    historyOfPresentIllness: ' Fever for 3 days ',
    examination: 'Throat congested',
    assessment: 'Acute pharyngitis',
    plan: '',
  };

  it('adds below what is written by default, on a new line', () => {
    expect(mergeIntoNote(note, review, 'append')).toEqual({
      historyOfPresentIllness: 'Fever for 3 days',
      examination: 'Tonsils enlarged\nThroat congested',
      assessment: 'Acute pharyngitis',
    });
  });

  it('replaces what is written on request, and leaves parts with an empty box alone in both modes', () => {
    const patch = mergeIntoNote(note, review, 'replace');
    expect(patch).toEqual({
      historyOfPresentIllness: 'Fever for 3 days',
      examination: 'Throat congested',
      assessment: 'Acute pharyngitis',
    });
    expect(patch).not.toHaveProperty('chiefComplaint');
    expect(patch).not.toHaveProperty('plan');
  });

  it('blocks a part that would not fit its field, and explains how to fix it', () => {
    const long = { ...EMPTY_SOAP, assessment: 'a'.repeat(3990) };
    const incoming = { ...EMPTY_SOAP, assessment: 'Viral fever with dehydration' };
    expect(overflowingSections(long, incoming, 'append')).toEqual([
      { field: 'assessment', length: 3990 + 1 + 28, limit: 4000 },
    ]);
    expect(overflowingSections(long, incoming, 'replace')).toEqual([]);
    expect(overflowingSections(EMPTY_SOAP, { ...EMPTY_SOAP, chiefComplaint: 'x'.repeat(1001) }, 'replace')).toEqual([
      { field: 'chiefComplaint', length: 1001, limit: 1000 },
    ]);
    expect(overflowMessage({ field: 'assessment', length: 4019, limit: 4000 }, 'append')).toBe(
      'Added to what the note already has, this part would be 4,019 characters; the limit is 4,000. Shorten it by 19, or choose “Replace what’s written”.',
    );
    expect(overflowMessage({ field: 'chiefComplaint', length: 1001, limit: 1000 }, 'replace')).toBe(
      'This part is 1,001 characters; the limit is 1,000. Shorten it by 1.',
    );
  });

  it('says under each box what inserting does to that part of the note', () => {
    expect(sectionStatus('', 'Fever', 'append')).toBe('Empty in the note, so this text goes in.');
    expect(sectionStatus(' ', '', 'append')).toBe('Empty in the note and in the draft.');
    expect(sectionStatus('Fever', '', 'replace')).toBe('The note already has text here. It stays as it is.');
    expect(sectionStatus('Fever', 'Cough', 'append')).toBe('The note already has text here. This is added below it.');
    expect(sectionStatus('Fever', 'Cough', 'replace')).toBe('The note already has text here. This replaces it.');
  });
});

describe('ICD suggestions', () => {
  it('shows the model’s confidence as a percentage', () => {
    expect(confidenceLabel(0.856)).toBe('86% confidence');
    expect(confidenceLabel(1.4)).toBe('100% confidence');
    expect(confidenceLabel(Number.NaN)).toBe('0% confidence');
  });

  it('marks codes this encounter already has, so they are not recorded twice', () => {
    expect(recordedCodes(suggestions, [diagnosis('j02.9'), diagnosis(null)])).toEqual(new Set(['J02.9']));
    expect(recordedCodes(suggestions, undefined)).toEqual(new Set());
  });

  it('takes the ticked codes in the suggested order, without recorded ones', () => {
    expect(tickedCodes(suggestions, new Set(['J06.9', 'J02.9', 'R50.9']), new Set(['R50.9']))).toEqual([
      'J02.9',
      'J06.9',
    ]);
    expect(tickedCodes(suggestions, new Set(), new Set())).toEqual([]);
  });

  it('adds a code as a provisional, active diagnosis titled by the catalogue', () => {
    expect(diagnosisRequest('J02.9', true)).toEqual({
      icdCode: 'J02.9',
      title: null,
      type: 'Provisional',
      isPrimary: true,
      onsetDate: null,
      status: 'Active',
      notes: null,
    });
  });

  it('makes the first accepted code primary when the encounter has none, and reports the codes not added', async () => {
    const add = vi.fn(async (request: AddDiagnosisRequest) => {
      if (request.icdCode === 'J02.9') {
        throw problem(400, 'validation', { icdCode: ['J02.9 is not in the ICD catalogue.'] });
      }
      if (request.icdCode === 'K21.0') {
        throw new ApiError({ status: 0, code: 'network', title: 'Cannot reach' });
      }
      return { ...diagnosis(request.icdCode), isPrimary: request.isPrimary };
    });

    const outcome = await addDiagnoses(['J02.9', 'R50.9', 'K21.0', 'J06.9'], false, add);

    expect(add.mock.calls.map(([request]) => [request.icdCode, request.isPrimary])).toEqual([
      ['J02.9', true],
      ['R50.9', true],
      ['K21.0', false],
      ['J06.9', false],
    ]);
    expect(outcome.added.map((d) => d.icdCode)).toEqual(['R50.9', 'J06.9']);
    expect(outcome.notAdded).toEqual([
      { code: 'J02.9', reason: 'not-in-catalogue' },
      { code: 'K21.0', reason: 'unreachable' },
    ]);
  });

  it('never replaces the primary diagnosis the encounter already has', async () => {
    const add = vi.fn(async (request: AddDiagnosisRequest) => diagnosis(request.icdCode));
    await addDiagnoses(['J02.9', 'R50.9'], true, add);
    expect(add.mock.calls.every(([request]) => !request.isPrimary)).toBe(true);
  });

  it('tells why a code was not added', () => {
    expect(notAddedReason(problem(400, 'validation', { icdCode: ['X is not in the ICD catalogue.'] }))).toBe(
      'not-in-catalogue',
    );
    expect(notAddedReason(new ApiError({ status: 0, code: 'network', title: 'x' }))).toBe('unreachable');
    expect(notAddedReason(problem(403, 'patient.restricted'))).toBe('not-allowed');
    expect(notAddedReason(problem(500))).toBe('failed');
    expect(notAddedReason(new Error('boom'))).toBe('failed');
  });

  it('sums up the codes not added and where to add them instead', () => {
    expect(notAddedSummary([{ code: 'J02.9', reason: 'not-in-catalogue' }])).toBe(
      'J02.9 is not in the hospital’s ICD list. Add the right diagnosis from the Diagnoses section.',
    );
    expect(
      notAddedSummary([
        { code: 'J02.9', reason: 'not-in-catalogue' },
        { code: 'R50.9', reason: 'not-in-catalogue' },
        { code: 'J06.9', reason: 'not-in-catalogue' },
        { code: 'K21.0', reason: 'unreachable' },
        { code: 'I10', reason: 'failed' },
      ]),
    ).toBe(
      'J02.9, R50.9 and J06.9 are not in the hospital’s ICD list. K21.0 could not be added because the server could not be reached. I10 could not be added. Add the right diagnosis from the Diagnoses section.',
    );
  });

  it('says what Insert will do, or why it cannot yet', () => {
    expect(insertHint(true, false, 2)).toBe('Shorten the parts marked in red before inserting.');
    expect(insertHint(false, true, 0)).toBe('Nothing to insert: every box is empty and no code is ticked.');
    expect(insertHint(false, false, 0)).toBe(
      'Inserting fills the note for you to check and save. Nothing is saved before that.',
    );
    expect(insertHint(false, false, 1)).toContain('adds the ticked code to the diagnoses straight away');
    expect(insertHint(false, false, 2)).toContain('adds the 2 ticked codes');
  });

  it('says what changed after inserting and what to do next', () => {
    expect(insertedToast(3, 0)).toEqual({
      intent: 'success',
      title: 'Draft added to the note',
      body: 'Review it and save (Ctrl+S).',
    });
    expect(insertedToast(3, 1).body).toBe('Review it and save (Ctrl+S). 1 provisional diagnosis was added.');
    expect(insertedToast(0, 2)).toEqual({
      intent: 'success',
      title: 'Diagnoses added',
      body: '2 provisional diagnoses were added. The note’s text was not changed.',
    });
    expect(insertedToast(0, 0).intent).toBe('warning');
    expect(insertedToast(2, 0, true).body).toBe('Review it, then record the amendment.');
  });
});

describe('errors in plain words', () => {
  it('explains each AI problem code', () => {
    expect(dictationErrorMessage(problem(503, 'ai.disabled'), 'draft')).toContain('AI help has been turned off');
    expect(dictationErrorMessage(problem(429, 'ai.quota'), 'draft')).toContain('usage limit');
    expect(dictationErrorMessage(problem(502, 'ai.provider_failed'), 'draft')).toContain('could not make a draft');
    expect(dictationErrorMessage(problem(502, 'ai.provider_failed'), 'speech')).toContain('speech service');
    expect(dictationErrorMessage(problem(403, 'patient.restricted'), 'draft')).toContain('restricted record');
    expect(dictationErrorMessage(problem(503, 'ai.speech_disabled'), 'speech')).toContain('server speech-to-text');
  });

  it('says when the AI took too long, without suggesting the server is unreachable', () => {
    const timedOut = new DOMException('signal timed out', 'TimeoutError');
    expect(dictationErrorMessage(timedOut, 'draft')).toBe(
      'The AI service took too long to answer. Try again in a moment; a shorter dictation is quicker.',
    );
    expect(dictationErrorMessage(timedOut, 'speech')).toContain('took too long');
  });

  it('explains the rate limit, permissions, the session and the network', () => {
    expect(dictationErrorMessage(problem(429), 'draft')).toContain('Wait a minute');
    expect(dictationErrorMessage(problem(403), 'draft')).toContain('permission to use AI dictation');
    expect(dictationErrorMessage(problem(401), 'draft')).toContain('sign in again');
    const offline = new ApiError({ status: 0, code: 'network', title: 'Cannot reach' });
    expect(dictationErrorMessage(offline, 'draft')).toBe(
      'The draft did not arrive. Check the WiFi connection and try again; your dictation is still here.',
    );
    expect(dictationErrorMessage(offline, 'speech')).toContain('WiFi');
  });

  it('passes through the server’s staff-facing validation messages', () => {
    expect(
      dictationErrorMessage(
        problem(400, undefined, { transcript: ['The dictation is too short to structure.'] }),
        'draft',
      ),
    ).toBe('The dictation is too short to structure.');
  });

  it('never shows raw details', () => {
    for (const error of [problem(500), problem(418), problem(413), new Error('TypeError: x is undefined'), 'boom']) {
      for (const during of ['draft', 'speech'] as const) {
        const message = dictationErrorMessage(error, during);
        expect(message).not.toContain('Raw server');
        expect(message).not.toContain('TypeError');
      }
    }
  });

  it('records 90-second takes and words voice problems for the dictation', () => {
    expect(shouldStopRecording(89_999, 1000, DICTATION_VOICE.maxRecordingMs)).toBeNull();
    expect(shouldStopRecording(90_000, 1000, DICTATION_VOICE.maxRecordingMs)).toBe('time');
    expect(microphoneProblem(undefined, DICTATION_VOICE.typeInstead)).toBe(
      'The microphone could not be started. Try again, or type the dictation.',
    );
    const browser = { isDesktop: false, recorderSupported: true, statusFailed: false };
    expect(voiceAvailability({ ...browser, recorderSupported: false, status: undefined }, DICTATION_VOICE).reason).toBe(
      'Voice input is not supported in this browser. Type the dictation, or use HC360 Desktop.',
    );
    expect(
      voiceAvailability({ ...browser, status: { ...status, serverSpeechToText: false } }, DICTATION_VOICE).reason,
    ).toBe(
      'Voice input in a browser needs server speech-to-text, which is not set up. Use HC360 Desktop, or type the dictation.',
    );
    expect(DICTATION_VOICE.describeError(problem(503, 'ai.speech_disabled'))).toContain('Type the dictation');
  });
});
