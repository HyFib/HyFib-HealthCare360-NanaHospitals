import type * as Core from '@hc360/core';
import { saveFile } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { fakeServer, stubBrowserApis } from '@/test/appScreen';
import type { ClinicalNoteDto } from './api';
import { NotesList } from './NotesPanel';

// Gap #27: a signed note is the hospital's ABDM health document, with the addenda signed to it, downloaded or linked to
// the patient's ABHA from the note. An addendum goes only with its note, a counselling note not at all, and a draft is
// not yet the hospital's record.

const state = vi.hoisted(() => ({ abdmOn: true, permissions: ['clinical.emr.write', 'abdm.export'] }));
vi.mock('@/lib/features', () => ({ useFeatureOn: (feature: string) => feature !== 'abdm' || state.abdmOn }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string | string[]) =>
    (Array.isArray(permission) ? permission : [permission]).some((p) => state.permissions.includes(p)),
}));
vi.mock('@hc360/core', async (importOriginal) => ({ ...(await importOriginal<typeof Core>()), saveFile: vi.fn() }));

const PATIENT = '0192a000-0000-7000-8000-00000000f001';
const NOTES = `/api/v1/emr/patients/${PATIENT}/notes`;

const note = (overrides: Partial<ClinicalNoteDto>): ClinicalNoteDto => ({
  id: '0192a000-0000-7000-8000-00000000f101',
  patientId: PATIENT,
  encounterId: null,
  admissionId: null,
  type: 'ProgressNote',
  title: 'Day 2 review',
  body: 'Afebrile since morning. Chest clear.',
  authorUserId: '0192a000-0000-7000-8000-00000000f901',
  authorPractitionerId: null,
  authorName: 'Dr. Asha Rao',
  amendsNoteId: null,
  isSigned: true,
  signedAt: '2026-11-04T04:00:00Z',
  createdAt: '2026-11-04T03:50:00Z',
  version: 2,
  ...overrides,
});

const PROGRESS = note({});
const ADDENDUM = note({
  id: '0192a000-0000-7000-8000-00000000f102',
  title: 'Addendum to Day 2 review',
  body: 'Rash on both forearms since last night.',
  amendsNoteId: PROGRESS.id,
});
const COUNSELLING = note({
  id: '0192a000-0000-7000-8000-00000000f103',
  type: 'Counselling',
  title: 'Pre-test counselling',
  body: 'Counselled before the test.',
});
const DRAFT = note({
  id: '0192a000-0000-7000-8000-00000000f104',
  type: 'Procedure',
  title: 'Ascitic tap',
  body: 'Planned for tomorrow.',
  isSigned: false,
  signedAt: null,
});

let server: ReturnType<typeof fakeServer>;

function renderList() {
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}
    >
      <Hc360ThemeProvider>
        <NotesList patientId={PATIENT} encounterId={null} />
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

/** The actions in a note's ABDM menu, or null when the note has none. */
async function actions(title: string): Promise<string[] | null> {
  await screen.findByRole('article', { name: title });
  const button = screen.queryByRole('button', { name: `More actions for ${title}` });
  if (!button) {
    return null;
  }

  await userEvent.click(button);
  const menu = await screen.findByRole('menu');
  const items = within(menu)
    .getAllByRole('menuitem')
    .map((item) => item.textContent ?? '');
  await userEvent.keyboard('{Escape}');
  await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument());
  return items;
}

beforeEach(() => {
  state.abdmOn = true;
  state.permissions = ['clinical.emr.write', 'abdm.export'];
  vi.mocked(saveFile).mockReset();
  stubBrowserApis();
  server = fakeServer({
    [`GET ${NOTES}`]: { items: [PROGRESS, ADDENDUM, COUNSELLING, DRAFT], page: 1, pageSize: 20, totalCount: 4 },
    'POST /api/v1/abdm/links': {
      id: '0192a000-0000-7000-8000-00000000f201',
      patientId: PATIENT,
      abhaAddress: 'asha.r@sbx',
      status: 'Linked',
      records: [],
      createdAt: '2026-11-04T10:00:00Z',
      sentAt: '2026-11-04T10:00:01Z',
      answeredAt: '2026-11-04T10:00:02Z',
      errorCode: null,
      error: null,
    },
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('a signed note as an ABDM health document', () => {
  it('is offered for a signed note, and not for its addendum, a counselling note or a draft', async () => {
    renderList();

    expect(await actions('Day 2 review')).toEqual(['Download ABDM record', "Link to patient's ABHA"]);
    expect(await actions('Addendum to Day 2 review')).toBeNull();
    expect(await actions('Pre-test counselling')).toBeNull();
    expect(await actions('Ascitic tap')).toBeNull();
  });

  it('downloads the note under the name the server gave it', async () => {
    const blob = new Blob(['{"resourceType":"Bundle","type":"document"}'], { type: 'application/fhir+json' });
    const download = vi.spyOn(api, 'download').mockResolvedValue({
      blob,
      fileName: 'progress-note-NH-UH-2600123-2026-11-04-abdm.json',
      headers: new Headers(),
    });
    renderList();

    await userEvent.click(await screen.findByRole('button', { name: 'More actions for Day 2 review' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Download ABDM record' }));

    await waitFor(() =>
      expect(saveFile).toHaveBeenCalledWith(blob, 'progress-note-NH-UH-2600123-2026-11-04-abdm.json'),
    );
    expect(download).toHaveBeenCalledWith(`/api/v1/abdm/bundles/clinical-notes/${PROGRESS.id}`);
  });

  it("links the note, with its addenda, to the patient's ABHA once staff confirm", async () => {
    renderList();

    await userEvent.click(await screen.findByRole('button', { name: 'More actions for Day 2 review' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: "Link to patient's ABHA" }));
    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent(
      'The progress note, with the addenda signed to it, goes to the Ayushman Bharat Digital Mission',
    );
    await userEvent.click(within(dialog).getByRole('button', { name: 'Link' }));

    await waitFor(() => expect(server.requests.some((r) => r.method === 'POST')).toBe(true));
    const post = server.requests.find((r) => r.method === 'POST')!;
    expect(post.path).toBe('/api/v1/abdm/links');
    expect(post.body).toEqual({ records: [{ type: 'ClinicalNote', id: PROGRESS.id }] });
  });

  it('is offered to staff who export ABDM records whether or not they write notes', async () => {
    state.permissions = ['abdm.export'];
    renderList();

    expect(await actions('Day 2 review')).toEqual(['Download ABDM record', "Link to patient's ABHA"]);
  });

  it('is not offered while ABDM health records are switched off', async () => {
    state.abdmOn = false;
    renderList();
    expect(await actions('Day 2 review')).toBeNull();
  });

  it('is not offered to staff without the right to export ABDM records', async () => {
    state.permissions = ['clinical.emr.write'];
    renderList();
    expect(await actions('Day 2 review')).toBeNull();
  });
});
