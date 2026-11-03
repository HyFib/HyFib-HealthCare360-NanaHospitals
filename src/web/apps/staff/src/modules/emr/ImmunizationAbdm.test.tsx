import type * as Core from '@hc360/core';
import { saveFile } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { fakeServer, stubBrowserApis } from '@/test/appScreen';
import type { ImmunizationDto } from './api';
import { ImmunizationsTab } from './RecordTabs';

// Gap #25: a dose given here is the hospital's ABDM immunisation record, downloaded or linked to the patient's ABHA from
// its row. A dose copied from the patient's card, or marked as entered in error, is not the hospital's to send.

const state = vi.hoisted(() => ({ abdmOn: true, permissions: ['clinical.immunizations.record', 'abdm.export'] }));
vi.mock('@/lib/features', () => ({ useFeatureOn: (feature: string) => feature !== 'abdm' || state.abdmOn }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string | string[]) =>
    (Array.isArray(permission) ? permission : [permission]).some((p) => state.permissions.includes(p)),
}));
vi.mock('@hc360/core', async (importOriginal) => ({ ...(await importOriginal<typeof Core>()), saveFile: vi.fn() }));

const PATIENT = '0192a000-0000-7000-8000-00000000e001';
const DOSES = `/api/v1/emr/patients/${PATIENT}/immunizations`;

const dose = (overrides: Partial<ImmunizationDto>): ImmunizationDto => ({
  id: '0192a000-0000-7000-8000-00000000e101',
  patientId: PATIENT,
  encounterId: null,
  vaccineName: 'Td',
  vaccineCode: 'TD',
  doseNumber: 1,
  administeredOn: '2026-11-03',
  route: 'IM',
  site: 'Left deltoid',
  batchNumber: 'TD2608-114',
  manufacturer: 'Biological E. Limited',
  expiryDate: '2027-08-31',
  administeredBy: null,
  isExternal: false,
  nextDueOn: '2026-12-01',
  notes: null,
  isEnteredInError: false,
  enteredInErrorReason: null,
  ...overrides,
});

const GIVEN_HERE = dose({});
const FROM_CARD = dose({
  id: '0192a000-0000-7000-8000-00000000e102',
  vaccineName: 'Hepatitis B',
  vaccineCode: 'HEPB',
  isExternal: true,
  administeredBy: 'PHC Thiruvallur',
});

let server: ReturnType<typeof fakeServer>;

function renderTab() {
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}
    >
      <Hc360ThemeProvider>
        <ImmunizationsTab patientId={PATIENT} />
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

/** The actions in a dose's row menu, or null when the row has no menu. */
async function actions(vaccine: string): Promise<string[] | null> {
  await screen.findByText(vaccine);
  const button = screen.queryByRole('button', { name: `Actions for ${vaccine}` });
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
  state.permissions = ['clinical.immunizations.record', 'abdm.export'];
  vi.mocked(saveFile).mockReset();
  stubBrowserApis();
  server = fakeServer({
    [`GET ${DOSES}`]: [GIVEN_HERE, FROM_CARD],
    'POST /api/v1/abdm/links': {
      id: '0192a000-0000-7000-8000-00000000e201',
      patientId: PATIENT,
      abhaAddress: 'kiran.r@sbx',
      status: 'Linked',
      records: [],
      createdAt: '2026-11-03T10:00:00Z',
      sentAt: '2026-11-03T10:00:01Z',
      answeredAt: '2026-11-03T10:00:02Z',
      errorCode: null,
      error: null,
    },
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('a dose as an ABDM immunisation record', () => {
  it('is offered for a dose given here, and not for one copied from the card', async () => {
    renderTab();

    expect(await actions('Td')).toEqual(['Download ABDM record', "Link to patient's ABHA", 'Mark as entered in error']);
    expect(await actions('Hepatitis B')).toEqual(['Mark as entered in error']);
  });

  it('downloads the dose under the name the server gave it', async () => {
    const blob = new Blob(['{"resourceType":"Bundle","type":"document"}'], { type: 'application/fhir+json' });
    const download = vi.spyOn(api, 'download').mockResolvedValue({
      blob,
      fileName: 'immunization-NH-UH-2600123-TD-2026-11-03-abdm.json',
      headers: new Headers(),
    });
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: 'Actions for Td' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Download ABDM record' }));

    await waitFor(() =>
      expect(saveFile).toHaveBeenCalledWith(blob, 'immunization-NH-UH-2600123-TD-2026-11-03-abdm.json'),
    );
    expect(download).toHaveBeenCalledWith(`/api/v1/abdm/bundles/immunizations/${GIVEN_HERE.id}`);
  });

  it("links the dose to the patient's ABHA once staff confirm", async () => {
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: 'Actions for Td' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: "Link to patient's ABHA" }));
    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent('The Td dose goes to the Ayushman Bharat Digital Mission');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Link' }));

    await waitFor(() => expect(server.requests.some((r) => r.method === 'POST')).toBe(true));
    const post = server.requests.find((r) => r.method === 'POST')!;
    expect(post.path).toBe('/api/v1/abdm/links');
    expect(post.body).toEqual({ records: [{ type: 'Immunization', id: GIVEN_HERE.id }] });
  });

  it('is offered alone to staff who export ABDM records but do not record doses', async () => {
    state.permissions = ['abdm.export'];
    renderTab();

    expect(await actions('Td')).toEqual(['Download ABDM record', "Link to patient's ABHA"]);
    expect(await actions('Hepatitis B')).toBeNull();
  });

  it('is not offered while ABDM health records are switched off', async () => {
    state.abdmOn = false;
    renderTab();

    expect(await actions('Td')).toEqual(['Mark as entered in error']);
  });
});
