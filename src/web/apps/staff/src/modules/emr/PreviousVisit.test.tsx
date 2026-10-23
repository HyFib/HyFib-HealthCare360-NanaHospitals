import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError, formatDateTime } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { PreviousVisitResponse, RecentEncounter } from './api';
import { PreviousVisit } from './PreviousVisit';

vi.mock('@/lib/api', () => ({ api: { get: vi.fn() } }));

const REVIEW: RecentEncounter = {
  id: 'e05',
  type: 'OutPatient',
  status: 'Signed',
  startedAt: '2026-08-31T05:10:00Z',
  practitionerName: 'Dr. Anil Rao',
  departmentName: 'General medicine',
  chiefComplaint: 'Diabetes review',
  diagnoses: ['E11.9 Type 2 diabetes mellitus without complications', 'I10 Essential (primary) hypertension'],
};

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
