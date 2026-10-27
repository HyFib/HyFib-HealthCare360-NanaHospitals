import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as DowntimeApi from './downtimeApi';
import {
  downtimeApi,
  durationText,
  isoToLocal,
  localToIso,
  type DowntimeEvent,
  type DowntimeSummary,
} from './downtimeApi';
import { DowntimeFigures, DowntimeStepDialog, DowntimeTable, RecordDowntimeDialog } from './DowntimeLog';

// The downtime log: the figures, the list, recording a downtime, ending it and signing off its back-entry. The server
// is replaced.

const list = vi.hoisted(() => vi.fn());
const summary = vi.hoisted(() => vi.fn());
vi.mock('./downtimeApi', async (importOriginal) => {
  const actual = await importOriginal<typeof DowntimeApi>();
  return {
    ...actual,
    downtimeApi: { record: vi.fn(), end: vi.fn(), signOff: vi.fn() },
    downtimeQueries: {
      list: () => ({ queryKey: ['downtime', 'list'], queryFn: () => list() }),
      summary: () => ({ queryKey: ['downtime', 'summary'], queryFn: () => summary() }),
    },
  };
});

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

beforeEach(() => {
  vi.clearAllMocks();
});

function wrap(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>
  );
}

const downtime = (patch: Partial<DowntimeEvent> = {}): DowntimeEvent => ({
  id: 'd1',
  number: 'NHDT260004',
  kind: 'Unplanned',
  cause: 'NetworkOutage',
  affected: 'All of HC360',
  description: 'Core switch failed',
  startedAt: '2026-10-20T04:00:00Z',
  endedAt: '2026-10-20T05:30:00Z',
  minutes: 90,
  status: 'BackEntryPending',
  recordedByName: 'Meena Iyer',
  actionsTaken: 'Switch replaced',
  endedByName: 'Meena Iyer',
  backEntryCompletedAt: null,
  paperRecords: null,
  backEntryNote: null,
  backEntrySignedByName: null,
  version: 5,
  ...patch,
});

const SUMMARY: DowntimeSummary = {
  from: '2025-10-28',
  to: '2026-10-27',
  unplanned: 2,
  planned: 1,
  drills: 1,
  unplannedMinutes: 135,
  plannedMinutes: 30,
  longestUnplannedMinutes: 90,
  unplannedAvailabilityPercent: 99.974,
  meanMinutesToRestore: 68,
  meanHoursToBackEntry: 5.5,
  ongoing: 0,
  backEntryPending: 1,
  lastDrillAt: null,
};

describe('Downtime log', () => {
  it('shows the figures NABH asks to be monitored', async () => {
    summary.mockResolvedValue(SUMMARY);
    render(wrap(<DowntimeFigures />));

    expect(await screen.findByText('99.974%')).toBeInTheDocument();
    expect(screen.getByText('2 h 15 min in all; longest 1 h 30 min')).toBeInTheDocument();
    expect(screen.getByText('1 h 8 min')).toBeInTheDocument();
    expect(screen.getByText('1 waiting for back-entry')).toBeInTheDocument();
    expect(screen.getByText('No drill recorded yet')).toBeInTheDocument();
  });

  it('lists downtime and opens one that is not closed', async () => {
    list.mockResolvedValue([
      downtime(),
      downtime({ id: 'd2', number: 'NHDT260003', status: 'Closed', paperRecords: 12 }),
    ]);
    const open = vi.fn();
    render(wrap(<DowntimeTable onOpen={open} />));

    const pending = (await screen.findByText(/NHDT260004/)).closest('tr')!;
    expect(within(pending).getByText('Back-entry pending')).toBeInTheDocument();
    expect(screen.getByText('12 paper records entered')).toBeInTheDocument();
    await userEvent.click(pending);
    expect(open).toHaveBeenCalledWith(expect.objectContaining({ id: 'd1' }));
    await userEvent.click(screen.getByText(/NHDT260003/).closest('tr')!);
    expect(open).toHaveBeenCalledTimes(1);
  });

  it('records a downtime that is still going on', async () => {
    vi.mocked(downtimeApi.record).mockResolvedValue(downtime());
    const close = vi.fn();
    render(wrap(<RecordDowntimeDialog open onClose={close} />));

    await userEvent.selectOptions(screen.getByRole('combobox', { name: /Kind/ }), 'Drill');
    await userEvent.click(screen.getByRole('button', { name: 'Record' }));

    expect(downtimeApi.record).toHaveBeenCalledWith(
      expect.objectContaining({ kind: 'Drill', cause: 'ServerFailure', affected: 'All of HC360', endedAt: null }),
    );
    expect(close).toHaveBeenCalled();
  });

  it('signs off the back-entry of an ended downtime with its version', async () => {
    vi.mocked(downtimeApi.signOff).mockResolvedValue(downtime({ status: 'Closed' }));
    render(wrap(<DowntimeStepDialog downtime={downtime()} onClose={() => {}} />));

    const records = screen.getByRole('spinbutton', { name: /Paper records entered/ });
    await userEvent.clear(records);
    await userEvent.type(records, '37');
    await userEvent.click(screen.getByRole('button', { name: 'Sign off' }));
    expect(downtimeApi.signOff).toHaveBeenCalledWith('d1', { paperRecords: 37, note: null, version: 5 });
  });

  it('ends an ongoing downtime', async () => {
    vi.mocked(downtimeApi.end).mockResolvedValue(downtime());
    render(wrap(<DowntimeStepDialog downtime={downtime({ status: 'Ongoing', endedAt: null })} onClose={() => {}} />));

    await userEvent.click(screen.getByRole('button', { name: 'End downtime' }));
    expect(downtimeApi.end).toHaveBeenCalledWith('d1', expect.objectContaining({ actionsTaken: null, version: 5 }));
  });

  it('writes durations and reads the time typed in the browser', () => {
    expect(durationText(45)).toBe('45 min');
    expect(durationText(120)).toBe('2 h');
    expect(durationText(135)).toBe('2 h 15 min');
    const at = new Date(2026, 9, 20, 9, 30);
    expect(isoToLocal(at)).toBe('2026-10-20T09:30');
    expect(localToIso('2026-10-20T09:30')).toBe(at.toISOString());
    expect(localToIso('2026-10-20')).toBeNull();
  });
});
