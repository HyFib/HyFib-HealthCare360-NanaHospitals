import type * as Core from '@hc360/core';
import { saveFile } from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type * as Features from '@/lib/features';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import { ANSWERS } from '@/test/roleAccess/rpm';
import { RECORD_ID } from '@/test/roleAccessIds';
import { istMonth, monthLabel, readingMonths } from './readingMonths';

// Gap #27: a patient's home readings in a monitoring programme go to ABDM as wellness records, a month at a time. From
// the enrolment, staff download a month's or link the programme to the patient's ABHA, once it has a reading.

const state = vi.hoisted(() => ({ abdmOn: true }));
vi.mock('@/lib/features', async (importOriginal) => ({
  ...(await importOriginal<typeof Features>()),
  useFeatureOn: (feature: string) => feature !== 'abdm' || state.abdmOn,
}));
vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  saveFile: vi.fn(),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  state.abdmOn = true;
  vi.mocked(saveFile).mockReset();
  stubBrowserApis();
  server = fakeServer({
    ...ANSWERS,
    'POST /api/v1/abdm/links': {
      id: '0192a000-0000-7000-8000-00000000f301',
      patientId: RECORD_ID,
      abhaAddress: 'sunil.d@sbx',
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

/** The enrolment's actions menu: what it offers. */
async function menuItems(): Promise<string[]> {
  await userEvent.click(await screen.findByRole('button', { name: 'More actions' }, SLOW));
  const menu = await screen.findByRole('menu');
  return within(menu)
    .getAllByRole('menuitem')
    .map((item) => item.textContent ?? '');
}

describe('the months of a programme', () => {
  it('go by the day in India, from the month before enrolment to the latest reading, newest first', () => {
    expect(istMonth('2026-10-31T18:29:59Z')).toBe('2026-10');
    expect(istMonth('2026-10-31T18:30:00Z')).toBe('2026-11');
    expect(readingMonths('2026-09-02', '2026-11-03T20:00:00Z')).toEqual(['2026-11', '2026-10', '2026-09', '2026-08']);
    expect(readingMonths('2026-01-15', '2026-01-20T05:00:00Z')).toEqual(['2026-01', '2025-12']);
    expect(monthLabel('2026-09')).toBe('September 2026');
  });
});

describe('home readings as ABDM wellness records', SCREEN_TESTS, () => {
  it('downloads the month chosen, the latest first, under the name the server gave it', async () => {
    const blob = new Blob(['{"resourceType":"Bundle","type":"document"}'], { type: 'application/fhir+json' });
    const download = vi.spyOn(api, 'download').mockResolvedValue({
      blob,
      fileName: 'home-readings-NH26000187-2026-10-abdm.json',
      headers: new Headers(),
    });
    openScreen(`/rpm/enrollments/${RECORD_ID}`);

    expect(await menuItems()).toEqual(expect.arrayContaining(['Download ABDM record', "Link to patient's ABHA"]));
    await userEvent.click(screen.getByRole('menuitem', { name: 'Download ABDM record' }));
    const dialog = await screen.findByRole('dialog', { name: 'Download ABDM record' });
    const month = within(dialog).getByRole('combobox', { name: 'Month' });
    const latest = istMonth(new Date(Date.now() - 3 * 3600_000).toISOString());
    expect(month).toHaveValue(latest);

    const earlier = within(dialog).getAllByRole('option')[1]!.getAttribute('value')!;
    await userEvent.selectOptions(month, earlier);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Download' }));

    await waitFor(() => expect(saveFile).toHaveBeenCalledWith(blob, 'home-readings-NH26000187-2026-10-abdm.json'));
    expect(download).toHaveBeenCalledWith(`/api/v1/abdm/bundles/home-readings/${RECORD_ID}?month=${earlier}`);
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Download ABDM record' })).not.toBeInTheDocument());
  });

  it("links the programme, with every reading taken in it, to the patient's ABHA once staff confirm", async () => {
    openScreen(`/rpm/enrollments/${RECORD_ID}`);

    await menuItems();
    await userEvent.click(screen.getByRole('menuitem', { name: "Link to patient's ABHA" }));
    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent(
      'The programme, with every reading taken in it, goes to the Ayushman Bharat Digital Mission',
    );
    await userEvent.click(within(dialog).getByRole('button', { name: 'Link' }));

    await waitFor(() => expect(server.requests.some((r) => r.method === 'POST')).toBe(true));
    const post = server.requests.find((r) => r.method === 'POST')!;
    expect(post.path).toBe('/api/v1/abdm/links');
    expect(post.body).toEqual({ records: [{ type: 'HomeReadings', id: RECORD_ID }] });
  });

  it('is not offered while ABDM health records are switched off', async () => {
    state.abdmOn = false;
    openScreen(`/rpm/enrollments/${RECORD_ID}`);

    const items = await menuItems();
    expect(items).not.toContain('Download ABDM record');
    expect(items).not.toContain("Link to patient's ABHA");
  });
});
