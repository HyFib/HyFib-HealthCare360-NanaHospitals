import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, stubBrowserApis } from '@/test/appScreen';
import { problem } from '@/test/fakeApi';
import type { CurrentMedication } from './api';
import { CurrentMedications } from './CurrentMedications';

// The chart's current medications (EHR-03), from the server's own list of the medicines a patient should be taking now:
// what each line shows, the empty chart, and a list the server cannot give.

const PATIENT = '0192a000-0000-7000-8000-00000000f001';
const PATH = `/api/v1/emr/patients/${PATIENT}/medications`;

const medicine = (overrides: Partial<CurrentMedication> & Pick<CurrentMedication, 'itemId' | 'drugName'>) => ({
  prescriptionId: '0192a000-0000-7000-8000-00000000f101',
  prescriptionNumber: 'NHRX260000412',
  genericName: null,
  strength: null,
  dose: '1 tablet',
  frequency: 'Twice a day',
  durationDays: 30,
  startedOn: '2026-10-10',
  endsOn: '2026-11-08',
  practitionerId: '0192a000-0000-7000-8000-0000000000d1',
  prescriberName: 'Dr. Meera Iyer',
  isControlledDrug: false,
  ...overrides,
});

const METFORMIN = medicine({
  itemId: '0192a000-0000-7000-8000-00000000f201',
  drugName: 'Glycomet',
  genericName: 'metformin',
  strength: '500 mg',
});
const CLONAZEPAM = medicine({
  itemId: '0192a000-0000-7000-8000-00000000f202',
  drugName: 'Clonotril',
  strength: '0.5 mg',
  dose: '1 tablet',
  frequency: 'At bedtime',
  durationDays: 1,
  startedOn: '2026-10-24',
  endsOn: '2026-10-24',
  prescriberName: null,
  prescriptionNumber: 'NHRX260000431',
  isControlledDrug: true,
});

let server: ReturnType<typeof fakeServer>;

function renderCard() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Hc360ThemeProvider>
        <CurrentMedications patientId={PATIENT} />
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('current medications on the chart', () => {
  it('lists each medicine with its dose, course and prescriber, and marks a controlled drug', async () => {
    server = fakeServer({ [`GET ${PATH}`]: [METFORMIN, CLONAZEPAM] });
    renderCard();
    const table = await screen.findByRole('table', { name: 'Current medications' });
    expect(await within(table).findByText('Glycomet 500 mg')).toBeInTheDocument();
    expect(within(table).getByText('metformin')).toBeInTheDocument();
    expect(within(table).getByText('Twice a day')).toBeInTheDocument();
    expect(within(table).getByText('30 days from 10 Oct 2026')).toBeInTheDocument();
    expect(within(table).getByText('Until 08 Nov 2026')).toBeInTheDocument();
    expect(within(table).getByText('Dr. Meera Iyer')).toBeInTheDocument();
    expect(within(table).getByText('NHRX260000412')).toBeInTheDocument();

    expect(within(table).getByText('Clonotril 0.5 mg')).toBeInTheDocument();
    expect(within(table).getByText('H1/X')).toBeInTheDocument();
    expect(within(table).getByText('1 day from 24 Oct 2026')).toBeInTheDocument();
    // A prescriber whose name the server could not find still reads as a doctor.
    expect(within(table).getByText('Doctor')).toBeInTheDocument();
    expect(screen.getByText('Current medications (2)')).toBeInTheDocument();
    expect(server.requests.map((r) => `${r.method} ${r.path}`)).toEqual([`GET ${PATH}`]);
  });

  it('says so when the patient takes nothing prescribed here', async () => {
    server = fakeServer({ [`GET ${PATH}`]: [] });
    renderCard();
    expect(await screen.findByText('No current medications')).toBeInTheDocument();
    expect(screen.getByText('Current medications (0)')).toBeInTheDocument();
  });

  it('a list the server cannot give says so and offers to retry', async () => {
    server = fakeServer({ [`GET ${PATH}`]: problem(403, 'You may not open this record') });
    renderCard();
    expect((await screen.findAllByText('You may not open this record')).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });
});
