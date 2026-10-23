import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError, formatDateOnly, formatDateTime } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { PreviousVisitResponse, PreviousVisitSummary } from './api';
import { PreviousVisit } from './PreviousVisit';

// The server, which picks the previous visit, is outside the test; what the panel shows and offers is what is tested.
vi.mock('@/lib/api', () => ({ api: { get: vi.fn() } }));

/** A diabetes review, signed: when, by whom and its diagnoses, with nothing else recorded. */
const REVIEW: PreviousVisitSummary = {
  encounterId: 'e05',
  type: 'OutPatient',
  status: 'Signed',
  startedAt: '2026-08-31T05:10:00Z',
  practitionerName: 'Dr. Anil Rao',
  departmentName: 'General medicine',
  chiefComplaint: 'Diabetes review',
  diagnoses: ['E11.9 Type 2 diabetes mellitus without complications', 'I10 Essential (primary) hypertension'],
  assessment: null,
  plan: null,
  followUpDate: null,
  followUpAdvice: null,
  medicines: [],
  vitals: null,
};

/** A fever seen and treated a week ago, by a doctor of General Medicine. */
function feverVisit(patch: Partial<PreviousVisitSummary> = {}): PreviousVisitSummary {
  return {
    encounterId: 'encounter-0',
    type: 'OutPatient',
    status: 'Signed',
    startedAt: '2026-10-16T04:30:00Z',
    practitionerName: 'Dr. Anil Rao',
    departmentName: 'General Medicine',
    chiefComplaint: 'Fever for three days',
    diagnoses: ['R50.9 Fever, unspecified'],
    assessment: 'Acute febrile illness, probably viral.',
    plan: 'Paracetamol, fluids and rest.',
    followUpDate: '2026-10-21',
    followUpAdvice: 'Come back if the fever lasts beyond two more days.',
    medicines: [
      {
        drugName: 'Paracetamol 500 mg Tablet',
        strength: '500 mg',
        dose: '1 tablet',
        frequency: 'TDS',
        durationDays: 5,
        instructions: 'After food',
      },
    ],
    vitals: {
      id: 'vitals-0',
      recordedAt: '2026-10-16T04:40:00Z',
      temperatureCelsius: 38.9,
      pulse: 108,
      respiratoryRate: 18,
      systolicBp: 126,
      diastolicBp: 82,
      spO2: 97,
      weightKg: 64,
      bmi: null,
      painScore: null,
      bloodGlucose: null,
      gcs: null,
      flags: [{ parameter: 'temperature', value: 38.9, direction: 'High', severity: 'Abnormal', message: 'Fever' }],
    },
    ...patch,
  };
}

beforeAll(() => {
  // Fluent's MessageBar (the error state) measures itself; jsdom has no ResizeObserver.
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

afterEach(() => {
  vi.mocked(api.get).mockReset();
});

function show(answer: PreviousVisitResponse | Error, onOpen = vi.fn()) {
  vi.mocked(api.get).mockImplementation(() =>
    answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer),
  );
  render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <PreviousVisit patientId="p1" encounterId="e09" onOpen={onOpen} />
      </QueryClientProvider>
    </FluentProvider>,
  );
  return onOpen;
}

describe('PreviousVisit', () => {
  it("shows the server's previous visit of this encounter: when, by whom, and its diagnoses", async () => {
    show({ previous: REVIEW });

    const visit = await screen.findByRole('button', { name: /Diabetes|E11\.9/ });
    expect(api.get).toHaveBeenCalledWith('/api/v1/emr/encounters/e09/previous-visit', expect.anything());
    expect(screen.getByRole('heading', { name: 'Previous visit' })).toBeInTheDocument();
    expect(visit).toHaveTextContent(formatDateTime(REVIEW.startedAt));
    expect(visit).toHaveTextContent('Signed');
    expect(visit).toHaveTextContent('Out-patient (OP) · General medicine · Dr. Anil Rao');
    expect(visit).toHaveTextContent(
      'E11.9 Type 2 diabetes mellitus without complications; I10 Essential (primary) hypertension',
    );
  });

  it('shows the complaint of a visit without diagnoses, and an amended note as amended', async () => {
    show({ previous: { ...REVIEW, status: 'Amended', diagnoses: [] } });

    const visit = await screen.findByRole('button', { name: /Diabetes review/ });
    expect(visit).toHaveTextContent('Amended');
    expect(visit).not.toHaveTextContent('E11.9');
    // The glance shows the complaint already, so it is not repeated below.
    expect(screen.queryByText('Complaint')).not.toBeInTheDocument();
  });

  it('shows what was decided, prescribed and measured at the visit below its glance', async () => {
    show({ previous: feverVisit() });

    const visit = await screen.findByRole('button', { name: /R50\.9/ });
    expect(visit).toHaveTextContent('R50.9 Fever, unspecified');
    expect(screen.getByText('Fever for three days')).toBeInTheDocument();
    expect(screen.getByText('Acute febrile illness, probably viral.')).toBeInTheDocument();
    expect(screen.getByText('Paracetamol, fluids and rest.')).toBeInTheDocument();
    expect(
      screen.getByText(`On ${formatDateOnly('2026-10-21')} · Come back if the fever lasts beyond two more days.`),
    ).toBeInTheDocument();
    expect(screen.getByText('Paracetamol 500 mg Tablet · 1 tablet · TDS · 5 days · After food')).toBeInTheDocument();
    expect(screen.getByText('126/82 mmHg')).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('says when nothing was prescribed, and leaves out a follow-up and vitals that were not recorded', async () => {
    show({ previous: feverVisit({ medicines: [], vitals: null, followUpDate: null, followUpAdvice: null }) });

    expect(await screen.findByText('None prescribed')).toBeInTheDocument();
    expect(screen.queryByText('Follow-up')).not.toBeInTheDocument();
    expect(screen.queryByText('Vitals')).not.toBeInTheDocument();
  });

  it('names each medicine with its strength once, and its course', async () => {
    const paracetamol = feverVisit().medicines[0]!;
    show({
      previous: feverVisit({
        medicines: [
          { ...paracetamol, drugName: 'Dolo', strength: '650 mg', durationDays: 1, instructions: null },
          { ...paracetamol, drugName: 'ORS sachet', strength: null, dose: '1 sachet', frequency: 'SOS' },
        ],
      }),
    });

    expect(await screen.findByText('Dolo 650 mg · 1 tablet · TDS · 1 day')).toBeInTheDocument();
    expect(screen.getByText('ORS sachet · 1 sachet · SOS · 5 days · After food')).toBeInTheDocument();
  });

  it('opens the previous visit from the mouse and the keyboard', async () => {
    const onOpen = show({ previous: REVIEW });

    const visit = await screen.findByRole('button', { name: /Diabetes|E11\.9/ });
    await userEvent.click(visit);
    expect(onOpen).toHaveBeenLastCalledWith('e05');

    onOpen.mockClear();
    visit.focus();
    await userEvent.keyboard('{Enter}');
    expect(onOpen).toHaveBeenCalledWith('e05');
  });

  it('says so when no visit was signed before this one', async () => {
    show({ previous: null });

    expect(await screen.findByText('No signed visit before this one.')).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('explains a failure and loads again on retry', async () => {
    show(new ApiError({ status: 503, title: 'Service unavailable', code: 'server.unavailable' }));

    expect(await screen.findByText('Could not load the previous visit')).toBeInTheDocument();
    vi.mocked(api.get).mockResolvedValue({ previous: REVIEW });
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Diabetes|E11\.9/ })).toBeInTheDocument());
  });
});
