import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, stubBrowserApis, type FakeRequest } from '@/test/appScreen';
import type { ImmunizationDto, VaccineChoice } from './api';
import { ImmunizationsTab } from './RecordTabs';

// Gap #25: the immunisation form records a dose by its card code, which the server sends to other providers as the
// vaccine's SNOMED CT code. Choosing a code names the vaccine and fills in how it is usually given.

vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

const PATIENT = '0192a000-0000-7000-8000-00000000e001';
const DOSES = `/api/v1/emr/patients/${PATIENT}/immunizations`;
const VACCINES: VaccineChoice[] = [
  { code: 'BCG', name: 'BCG', route: 'Intradermal' },
  { code: 'OPV', name: 'Oral polio vaccine (bOPV)', route: 'Oral' },
  { code: 'JE', name: 'Japanese encephalitis', route: null },
];

const recorded = (request: Partial<ImmunizationDto>): ImmunizationDto => ({
  id: '0192a000-0000-7000-8000-00000000e101',
  patientId: PATIENT,
  encounterId: null,
  vaccineName: 'BCG',
  vaccineCode: 'BCG',
  doseNumber: null,
  administeredOn: '2026-11-03',
  route: 'ID',
  site: null,
  batchNumber: 'B2611-07',
  manufacturer: null,
  expiryDate: '2027-03-31',
  administeredBy: null,
  isExternal: false,
  nextDueOn: null,
  notes: null,
  isEnteredInError: false,
  enteredInErrorReason: null,
  ...request,
});

let server: ReturnType<typeof fakeServer>;

function renderTab() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Hc360ThemeProvider>
        <ImmunizationsTab patientId={PATIENT} />
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

async function openForm() {
  await userEvent.click(await screen.findByRole('button', { name: 'Record immunisation' }));
  const dialog = await screen.findByRole('dialog');
  // The list of card codes has loaded.
  await within(dialog).findByRole('option', { name: 'BCG: BCG' });
  return dialog;
}

/** A dose given here needs its batch and expiry. */
async function fillBatch(dialog: HTMLElement) {
  await userEvent.type(within(dialog).getByRole('textbox', { name: /Batch number/ }), 'B2611-07');
  fireEvent.change(within(dialog).getByLabelText(/Batch expiry/), { target: { value: '2027-03-31' } });
}

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer({
    [`GET ${DOSES}`]: [],
    'GET /api/v1/emr/vaccines': VACCINES,
    [`POST ${DOSES}`]: (request: FakeRequest) => recorded(request.body as Partial<ImmunizationDto>),
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('recording an immunisation by its card code', () => {
  it('names the vaccine and fills in its usual route, and sends the code with the dose', async () => {
    renderTab();
    const dialog = await openForm();

    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /Card code/ }), 'BCG');
    expect(within(dialog).getByRole('textbox', { name: /^Vaccine/ })).toHaveValue('BCG');
    expect(within(dialog).getByRole('textbox', { name: /^Route/ })).toHaveValue('ID');
    await fillBatch(dialog);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Record' }));

    await waitFor(() => expect(server.requests.some((r) => r.method === 'POST')).toBe(true));
    const post = server.requests.find((r) => r.method === 'POST')!;
    expect(post.path).toBe(DOSES);
    expect(post.body).toMatchObject({ vaccineName: 'BCG', vaccineCode: 'BCG', route: 'ID', isExternal: false });
  });

  it('keeps a vaccine name and a route staff typed', async () => {
    renderTab();
    const dialog = await openForm();
    const name = within(dialog).getByRole('textbox', { name: /^Vaccine/ });
    const route = within(dialog).getByRole('textbox', { name: /^Route/ });
    await userEvent.type(name, 'bOPV drops');
    await userEvent.clear(route);
    await userEvent.type(route, 'Oral drops');

    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /Card code/ }), 'OPV');
    expect(name).toHaveValue('bOPV drops');
    expect(route).toHaveValue('Oral drops');
  });

  it('leaves the route alone for a vaccine given more than one way', async () => {
    renderTab();
    const dialog = await openForm();

    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /Card code/ }), 'JE');
    expect(within(dialog).getByRole('textbox', { name: /^Vaccine/ })).toHaveValue('Japanese encephalitis');
    expect(within(dialog).getByRole('textbox', { name: /^Route/ })).toHaveValue('IM');
  });

  it('records a vaccine that is not on the list without a code', async () => {
    renderTab();
    const dialog = await openForm();
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Vaccine/ }), 'Kyasanur forest disease');
    await fillBatch(dialog);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Record' }));

    await waitFor(() => expect(server.requests.some((r) => r.method === 'POST')).toBe(true));
    expect(server.requests.find((r) => r.method === 'POST')!.body).toMatchObject({
      vaccineName: 'Kyasanur forest disease',
      vaccineCode: null,
    });
  });
});
