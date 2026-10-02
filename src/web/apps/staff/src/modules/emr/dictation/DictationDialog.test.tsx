import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Core from '@hc360/core';
import { ApiError, bridge } from '@hc360/core';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as Ai from '@/lib/ai';
import { structureNote, type AiStatusDto, type ClinicalNoteDraft } from '@/lib/ai';
import type * as EmrApi from '../api';
import { emrApi, type DiagnosisDto } from '../api';
import { emptyEncounterForm, type EncounterFormValues } from '../encounterForm';
import { DictationFlow, type DictationProps } from './DictationDialog';

// The dialog's content is tested without Fluent's Dialog, which flakes in jsdom; the server calls are replaced, and
// HC360 Desktop's offline speech can be switched on.
const desktop = vi.hoisted(() => ({ on: false }));
vi.mock('@hc360/core', async (importOriginal) => {
  const core = await importOriginal<typeof Core>();
  return {
    ...core,
    bridge: {
      ...core.bridge,
      get isDesktop() {
        return desktop.on;
      },
      dictate: vi.fn(),
    },
  };
});
vi.mock('@/lib/ai', async (importOriginal) => ({
  ...(await importOriginal<typeof Ai>()),
  structureNote: vi.fn(),
}));
vi.mock('../api', async (importOriginal) => ({
  ...(await importOriginal<typeof EmrApi>()),
  emrApi: { addDiagnosis: vi.fn() },
}));

const PATIENT = '3f2b8c1e-4a5d-4e6f-9a7b-1c2d3e4f5a6b';

const status: AiStatusDto = {
  enabled: true,
  provider: 'anthropic',
  model: 'claude-opus-5-5',
  reason: null,
  serverSpeechToText: false,
  speechProvider: 'none',
  speechReason: 'Server speech-to-text is not configured.',
  timeoutSeconds: 90,
};

const draft: ClinicalNoteDraft = {
  status: 'draft',
  subjective: 'Sore throat for 2 days\nBrought by [NAME_1]',
  objective: 'Throat congested',
  assessment: 'Acute pharyngitis',
  plan: 'Salt-water gargles\nCall [PHONE_1] if worse',
  suggestedIcd: [
    { code: 'J02.9', title: 'Acute pharyngitis, unspecified', confidence: 0.86 },
    { code: 'R50.9', title: 'Fever, unspecified', confidence: 0.4 },
  ],
  warnings: [
    'AI-generated draft: review and correct every section before saving. Nothing has been saved or signed.',
    'Dose of paracetamol not stated.',
  ],
  requiresClinicianReview: true,
  model: 'claude-opus-5-5',
  generatedAt: '2026-10-02T09:30:00Z',
  identifiersRedacted: 2,
};

function addedDiagnosis(icdCode: string | null, isPrimary: boolean): DiagnosisDto {
  return {
    id: `d-${icdCode}`,
    patientId: PATIENT,
    encounterId: 'enc-1',
    icdCode,
    title: 'From the catalogue',
    type: 'Provisional',
    isPrimary,
    onsetDate: null,
    status: 'Active',
    resolvedOn: null,
    recordedAt: '2026-10-02T09:31:00Z',
    notes: null,
    version: 1,
  };
}

function renderFlow(note: Partial<EncounterFormValues> = {}, overrides: Partial<DictationProps> = {}) {
  const values: EncounterFormValues = { ...emptyEncounterForm, ...note };
  const form = { getValues: vi.fn(() => values), setValue: vi.fn() };
  const props: DictationProps = {
    patientId: PATIENT,
    encounterId: 'enc-1',
    departmentName: 'General Medicine',
    form: form as unknown as DictationProps['form'],
    diagnoses: [],
    aiStatus: status,
    onClose: vi.fn(),
    onInserted: vi.fn(),
    ...overrides,
  };
  render(
    <FluentProvider theme={webLightTheme}>
      <DictationFlow {...props} />
    </FluentProvider>,
  );
  return { ...props, form };
}

async function makeDraft(text = 'Sore throat for two days, throat congested.') {
  await userEvent.type(screen.getByRole('textbox', { name: 'Dictation' }), text);
  await userEvent.click(screen.getByRole('button', { name: 'Make SOAP draft' }));
  await screen.findByText('Step 2 of 2: check the draft');
}

describe('DictationFlow', () => {
  beforeEach(() => {
    desktop.on = false;
    vi.mocked(bridge.dictate).mockReset();
    vi.mocked(structureNote).mockReset();
    vi.mocked(emrApi.addDiagnosis).mockReset();
  });

  it('makes a draft only from a real dictation, sending the department as the specialty', async () => {
    vi.mocked(structureNote).mockResolvedValue(draft);
    renderFlow();

    const make = screen.getByRole('button', { name: 'Make SOAP draft' });
    expect(make).toBeDisabled();
    expect(screen.getByRole('textbox', { name: 'Specialty' })).toHaveValue('General Medicine');
    expect(screen.getByText(/taken out before the text leaves the hospital/)).toBeInTheDocument();

    await userEvent.type(screen.getByRole('textbox', { name: 'Dictation' }), 'Fever 3d');
    expect(make).toBeDisabled();
    expect(screen.getByText('Add a little more: a draft needs at least 10 characters.')).toBeInTheDocument();

    await userEvent.type(screen.getByRole('textbox', { name: 'Dictation' }), ', dry cough');
    expect(make).toBeEnabled();
    await userEvent.click(make);

    expect(structureNote).toHaveBeenCalledWith(
      { transcript: 'Fever 3d, dry cough', specialty: 'General Medicine', patientId: PATIENT },
      90,
    );
    expect(await screen.findByText('Step 2 of 2: check the draft')).toBeInTheDocument();
  });

  it('shows the warnings, counts the placeholders and suggests the chief complaint for an empty note', async () => {
    vi.mocked(structureNote).mockResolvedValue(draft);
    renderFlow();
    await makeDraft();

    expect(screen.getByText('Dose of paracetamol not stated.')).toBeInTheDocument();
    expect(screen.getByText('2 placeholders to replace')).toBeInTheDocument();
    expect(screen.getByText('[NAME_1], [PHONE_1]')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Chief complaint' })).toHaveValue('Sore throat for 2 days');
    expect(screen.getByRole('textbox', { name: 'History (Subjective)' })).toHaveValue(draft.subjective);
    expect(screen.getByRole('checkbox', { name: /J02\.9/ })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: /R50\.9/ })).not.toBeChecked();
    // Nothing in the note yet, so there is nothing to keep or replace.
    expect(screen.queryByRole('radio', { name: 'Replace what’s written' })).not.toBeInTheDocument();

    await userEvent.clear(screen.getByRole('textbox', { name: 'History (Subjective)' }));
    await userEvent.type(
      screen.getByRole('textbox', { name: 'History (Subjective)' }),
      'Sore throat, brought by mother',
    );
    expect(screen.getByText('1 placeholder to replace')).toBeInTheDocument();
  });

  it('adds below the note’s text and adds only the ticked codes, the first as primary', async () => {
    vi.mocked(structureNote).mockResolvedValue(draft);
    vi.mocked(emrApi.addDiagnosis).mockImplementation(async (_, request) =>
      addedDiagnosis(request.icdCode, request.isPrimary),
    );
    const { form, onInserted } = renderFlow({ chiefComplaint: 'Sore throat', examination: 'Tonsils enlarged' });
    await makeDraft();

    // The note has a chief complaint, so none is suggested.
    expect(screen.getByRole('textbox', { name: 'Chief complaint' })).toHaveValue('');
    expect(screen.getByRole('radio', { name: 'Add below what’s written' })).toBeChecked();
    await userEvent.click(screen.getByRole('checkbox', { name: /J02\.9/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Insert into note' }));

    await waitFor(() => expect(onInserted).toHaveBeenCalledTimes(1));
    expect(form.setValue).toHaveBeenCalledWith('examination', 'Tonsils enlarged\nThroat congested', {
      shouldDirty: true,
    });
    expect(form.setValue).toHaveBeenCalledWith('assessment', 'Acute pharyngitis', { shouldDirty: true });
    expect(form.setValue).not.toHaveBeenCalledWith('chiefComplaint', expect.anything(), expect.anything());
    expect(emrApi.addDiagnosis).toHaveBeenCalledTimes(1);
    expect(emrApi.addDiagnosis).toHaveBeenCalledWith(
      'enc-1',
      expect.objectContaining({ icdCode: 'J02.9', isPrimary: true }),
    );
    expect(onInserted).toHaveBeenCalledWith({
      fields: ['historyOfPresentIllness', 'examination', 'assessment', 'plan'],
      diagnosesAdded: 1,
      notAdded: [],
    });
  });

  it('replaces what is written on request and reports a code the catalogue does not have', async () => {
    vi.mocked(structureNote).mockResolvedValue(draft);
    vi.mocked(emrApi.addDiagnosis).mockRejectedValue(
      new ApiError({
        status: 400,
        title: 'Please correct the highlighted fields.',
        errors: { icdCode: ['R50.9 is not in the ICD catalogue.'] },
      }),
    );
    const primary = addedDiagnosis('I10', true);
    const { form, onInserted } = renderFlow({ examination: 'Tonsils enlarged' }, { diagnoses: [primary] });
    await makeDraft();

    await userEvent.click(screen.getByRole('radio', { name: 'Replace what’s written' }));
    await userEvent.click(screen.getByRole('checkbox', { name: /R50\.9/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Insert into note' }));

    await waitFor(() => expect(onInserted).toHaveBeenCalledTimes(1));
    expect(form.setValue).toHaveBeenCalledWith('examination', 'Throat congested', { shouldDirty: true });
    // The encounter already has a primary diagnosis, which is kept.
    expect(emrApi.addDiagnosis).toHaveBeenCalledWith('enc-1', expect.objectContaining({ isPrimary: false }));
    expect(onInserted).toHaveBeenCalledWith(
      expect.objectContaining({ diagnosesAdded: 0, notAdded: [{ code: 'R50.9', reason: 'not-in-catalogue' }] }),
    );
  });

  it('does not offer a code the encounter already has', async () => {
    vi.mocked(structureNote).mockResolvedValue(draft);
    renderFlow({}, { diagnoses: [addedDiagnosis('J02.9', true)] });
    await makeDraft();

    expect(screen.getByRole('checkbox', { name: /J02\.9/ })).toBeDisabled();
    expect(screen.getByText('Already a diagnosis of this encounter')).toBeInTheDocument();
  });

  it('blocks a part that would not fit the note until it is shortened or replaces the text', async () => {
    vi.mocked(structureNote).mockResolvedValue(draft);
    renderFlow({ assessment: 'a'.repeat(3990) });
    await makeDraft();

    const insert = screen.getByRole('button', { name: 'Insert into note' });
    expect(insert).toBeDisabled();
    expect(screen.getByText(/this part would be 4,008 characters; the limit is 4,000/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('radio', { name: 'Replace what’s written' }));
    expect(insert).toBeEnabled();
  });

  it('explains a failed draft in plain words and keeps the dictation', async () => {
    vi.mocked(structureNote).mockRejectedValue(
      new ApiError({ status: 503, code: 'ai.disabled', title: 'AI features are not available', detail: 'ai.enabled' }),
    );
    renderFlow();
    await userEvent.type(screen.getByRole('textbox', { name: 'Dictation' }), 'Fever for three days');
    await userEvent.click(screen.getByRole('button', { name: 'Make SOAP draft' }));

    expect(await screen.findByText(/AI help has been turned off/)).toBeInTheDocument();
    expect(screen.queryByText(/ai\.enabled/)).not.toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Dictation' })).toHaveValue('Fever for three days');
    expect(screen.getByRole('button', { name: 'Make SOAP draft' })).toBeEnabled();
  });

  it('adds each spoken take at the end of the dictation, and Ctrl+Enter makes the draft', async () => {
    desktop.on = true;
    vi.mocked(bridge.dictate)
      .mockResolvedValueOnce({ text: 'Fever for three days' })
      .mockResolvedValueOnce({ text: ' no cough ' });
    vi.mocked(structureNote).mockResolvedValue(draft);
    renderFlow();
    const dictation = screen.getByRole('textbox', { name: 'Dictation' });

    await userEvent.click(screen.getByRole('button', { name: 'Speak' }));
    await waitFor(() => expect(dictation).toHaveValue('Fever for three days'));
    await userEvent.click(screen.getByRole('button', { name: 'Speak' }));
    await waitFor(() => expect(dictation).toHaveValue('Fever for three days\nno cough'));
    expect(bridge.dictate).toHaveBeenCalledWith('en-IN');
    expect(screen.getByText(/2 recordings added at the end/)).toBeInTheDocument();

    await userEvent.click(dictation);
    await userEvent.keyboard('{Control>}{Enter}{/Control}');
    expect(structureNote).toHaveBeenCalledWith(
      expect.objectContaining({ transcript: 'Fever for three days\nno cough' }),
      90,
    );
    expect(await screen.findByText('Step 2 of 2: check the draft')).toBeInTheDocument();
  });

  it('closes straight away when there is nothing to lose, and says why voice is not offered', async () => {
    const { onClose } = renderFlow();

    expect(screen.queryByRole('button', { name: 'Speak' })).not.toBeInTheDocument();
    expect(screen.getByText(/Type the dictation, or use HC360 Desktop/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
