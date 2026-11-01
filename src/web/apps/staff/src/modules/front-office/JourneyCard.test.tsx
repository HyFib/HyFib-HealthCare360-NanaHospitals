import { useSession, type AuthUser } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { patientJourneys } from '@/test/accessibility/fixtures/frontOffice';
import { fakeServer, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { PatientJourneyCard } from './JourneyCard';
import type { PatientJourneys } from './journeys';

// The journey card on a patient's page: the visit and stay as full pipelines, operations and lab orders beneath as
// compact lines, each stage as the server derived it. The server is a fake.

const PATIENT_ID = '0192a000-0000-7000-8000-00000000c101';
const JOURNEYS = patientJourneys(PATIENT_ID);

let server: ReturnType<typeof fakeServer>;

function showCard(journeys: PatientJourneys = JOURNEYS, user: AuthUser = STAFF_USER) {
  server = fakeServer({ [`GET /api/v1/patients/${PATIENT_ID}/journey`]: journeys });
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 3600_000,
    user,
    locked: false,
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <Hc360ThemeProvider>
        <PatientJourneyCard patientId={PATIENT_ID} />
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

describe('the journey card', () => {
  it('shows the visit as a pipeline, with the stage the patient is in as the current step', async () => {
    showCard();

    const visit = await screen.findByRole('list', { name: 'Out-patient visit OP2600981' });
    const stages = within(visit).getAllByRole('listitem');
    expect(stages.map((s) => s.querySelector('.fui-Text')?.textContent)).toEqual(
      JOURNEYS.outpatient!.stages.map((s) => s.label),
    );
    const current = stages.filter((s) => s.getAttribute('aria-current') === 'step');
    expect(current).toHaveLength(1);
    expect(current[0]).toHaveTextContent('In consultation');
    expect(current[0]).toHaveTextContent('Now, 20 min');
    expect(stages[0]).toHaveTextContent('Done');
    expect(stages[0]).toHaveTextContent('Token GM-012');
    expect(stages[3]).toHaveTextContent('Pending');
    expect(screen.getByRole('heading', { name: 'Out-patient visit OP2600981' })).toBeInTheDocument();
    expect(screen.getByText('In progress')).toBeInTheDocument();
    expect(screen.getByText('General medicine · Dr. Meera Iyer')).toBeInTheDocument();
  });

  it('puts the operations and lab orders beneath as compact lines', async () => {
    showCard();

    const lab = await screen.findByRole('list', { name: 'Lab order NHLB260000123' });
    const current = within(lab)
      .getAllByRole('listitem')
      .find((s) => s.getAttribute('aria-current') === 'step');
    expect(current).toHaveTextContent('Sample collected');
    expect(current).toHaveTextContent('5 min');
    expect(within(lab).getAllByRole('listitem')[0]).toHaveTextContent(/^Ordered: Done/);

    // The slot is booked, but when it was given is not stored: the stage shows no time rather than one made up.
    const operation = screen.getByRole('list', { name: 'Operation OT2600045' });
    const scheduled = within(operation)
      .getAllByRole('listitem')
      .find((s) => s.getAttribute('aria-current') === 'step');
    expect(scheduled?.textContent).toBe('Scheduled');
    expect(screen.getByRole('heading', { name: 'Operations' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Lab orders' })).toBeInTheDocument();
  });

  it('says operations are left out of a confidential record the user may not open', async () => {
    showCard({ ...JOURNEYS, surgeries: [], surgeriesWithheld: true });

    expect(await screen.findByText(/Operations are not shown: the record is confidential/)).toBeInTheDocument();
    expect(screen.queryByRole('list', { name: 'Operation OT2600045' })).not.toBeInTheDocument();
  });

  it('says when there is nothing to follow', async () => {
    showCard({ ...JOURNEYS, outpatient: null, surgeries: [], labOrders: [] });

    expect(await screen.findByText('No visit, stay, operation or lab order to follow yet.')).toBeInTheDocument();
  });

  it('is not shown, and asks the server for nothing, to staff who may not see visits', () => {
    showCard(JOURNEYS, { ...STAFF_USER, roles: ['Canteen'], permissions: ['patients.read'] });

    expect(screen.queryByRole('heading', { name: 'Journey' })).not.toBeInTheDocument();
    expect(server.requests).toEqual([]);
  });
});
