import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError, formatDateOnly, formatDateTime } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { PreviousVisitSummary } from './api';
import { PreviousVisit } from './PreviousVisit';

// The server, which picks the previous visit, is outside the test; what the panel shows and offers is what is tested.
vi.mock('@/lib/api', () => ({ api: { get: vi.fn() } }));

// Fluent's MessageBar (the error state) measures itself; jsdom has no ResizeObserver.
beforeAll(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

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

function renderPanel(onOpen = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <PreviousVisit encounterId="encounter-1" onOpen={onOpen} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return onOpen;
}

describe('PreviousVisit', () => {
  beforeEach(() => {
    vi.mocked(api.get).mockReset();
  });

  it('shows what was found, decided, prescribed and measured at the visit before, and opens it', async () => {
    vi.mocked(api.get).mockResolvedValue({ previous: feverVisit() });
    const onOpen = renderPanel();

    expect(
      await screen.findByRole('heading', { name: `Previous visit · ${formatDateTime('2026-10-16T04:30:00Z')}` }),
    ).toBeInTheDocument();
    expect(api.get).toHaveBeenCalledWith('/api/v1/emr/encounters/encounter-1/previous', expect.anything());
    expect(screen.getByText('Out-patient (OP) · General Medicine · Dr. Anil Rao')).toBeInTheDocument();
    expect(screen.getByText('Fever for three days')).toBeInTheDocument();
    expect(screen.getByText('R50.9 Fever, unspecified')).toBeInTheDocument();
    expect(screen.getByText('Acute febrile illness, probably viral.')).toBeInTheDocument();
    expect(screen.getByText('Paracetamol, fluids and rest.')).toBeInTheDocument();
    expect(
      screen.getByText(`On ${formatDateOnly('2026-10-21')} · Come back if the fever lasts beyond two more days.`),
    ).toBeInTheDocument();
    expect(screen.getByText('Paracetamol 500 mg Tablet · 1 tablet · TDS · 5 days · After food')).toBeInTheDocument();
    expect(screen.getByText('126/82 mmHg')).toBeInTheDocument();
    expect(screen.queryByText('Not signed')).not.toBeInTheDocument();
    await expectNoAxeViolations();

    await userEvent.click(screen.getByRole('button', { name: 'Open' }));
    expect(onOpen).toHaveBeenCalledWith('encounter-0');
  });

  it('says when the note of the visit before is not signed yet', async () => {
    vi.mocked(api.get).mockResolvedValue({
      previous: feverVisit({
        status: 'InProgress',
        medicines: [],
        vitals: null,
        followUpDate: null,
        followUpAdvice: null,
      }),
    });
    renderPanel();

    expect(await screen.findByText('Not signed')).toBeInTheDocument();
    expect(screen.getByText('The note of that visit is not signed yet and may still change.')).toBeInTheDocument();
    expect(screen.getByText('None prescribed')).toBeInTheDocument();
    expect(screen.queryByText('Follow-up')).not.toBeInTheDocument();
    expect(screen.queryByText('Vitals')).not.toBeInTheDocument();
  });

  it('names each medicine with its strength once, and its course', async () => {
    const paracetamol = feverVisit().medicines[0]!;
    vi.mocked(api.get).mockResolvedValue({
      previous: feverVisit({
        medicines: [
          { ...paracetamol, drugName: 'Dolo', strength: '650 mg', durationDays: 1, instructions: null },
          { ...paracetamol, drugName: 'ORS sachet', strength: null, dose: '1 sachet', frequency: 'SOS' },
        ],
      }),
    });
    renderPanel();

    expect(await screen.findByText('Dolo 650 mg · 1 tablet · TDS · 1 day')).toBeInTheDocument();
    expect(screen.getByText('ORS sachet · 1 sachet · SOS · 5 days · After food')).toBeInTheDocument();
  });

  it("says so on a patient's first encounter", async () => {
    vi.mocked(api.get).mockResolvedValue({ previous: null });
    renderPanel();

    expect(await screen.findByText("This is the patient's first encounter on record.")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open' })).not.toBeInTheDocument();
  });

  it('offers to try again when the previous visit cannot be loaded', async () => {
    vi.mocked(api.get)
      .mockRejectedValueOnce(new ApiError({ status: 503, title: 'Server unavailable', detail: 'Try again shortly.' }))
      .mockResolvedValue({ previous: null });
    renderPanel();

    expect(await screen.findByText('Could not load the previous visit')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText("This is the patient's first encounter on record.")).toBeInTheDocument();
  });
});
