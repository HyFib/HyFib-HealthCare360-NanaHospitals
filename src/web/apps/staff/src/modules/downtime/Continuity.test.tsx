import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useConnection } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { useOfflineCopy } from '@/lib/offline/offlineCopy';
import { OfflineCopyBanner } from '@/shell/OfflineCopy';
import type * as ContinuityApi from './continuityApi';
import type * as PackPrint from './packPrint';
import {
  blockProgress,
  continuityApi,
  lateRequestOf,
  windowWords,
  type DowntimeNumberBlockSummary,
  type DowntimePack,
} from './continuityApi';
import { NumberBlocks, PackPanel, RequestLateDialog } from './Continuity';
import { numberSheetHtml, packHtml } from './packPrint';

// Downtime continuity: the pack printed and shown, the counters' number blocks, asking for a late back-entry, and the
// read-only offline copy shown with its age when the server cannot be reached. The server is replaced.

const blocks = vi.hoisted(() => vi.fn());
vi.mock('./continuityApi', async (importOriginal) => {
  const actual = await importOriginal<typeof ContinuityApi>();
  return {
    ...actual,
    continuityApi: { ...actual.continuityApi, printPack: vi.fn(), requestLate: vi.fn() },
    continuityQueries: {
      ...actual.continuityQueries,
      blocks: () => ({ queryKey: ['blocks'], queryFn: () => blocks() }),
    },
  };
});
vi.mock('./packPrint', async (importOriginal) => ({
  ...(await importOriginal<typeof PackPrint>()),
  printPack: vi.fn().mockResolvedValue(undefined),
}));

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

beforeEach(() => vi.clearAllMocks());
afterEach(() => {
  useConnection.setState({ browserOnline: true, serverReachable: true });
  useOfflineCopy.setState({ takenAt: null, pack: null, worklists: [] });
});

function wrap(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>
  );
}

const PACK: DowntimePack = {
  id: 'p1',
  kind: 'OnDemand',
  generatedAt: '2026-11-08T08:00:00Z',
  windowEndsAt: '2026-11-09T08:00:00Z',
  facilityName: 'Nana Hospitals',
  generatedByName: 'Meena Iyer',
  patients: 1,
  sections: [
    {
      key: 'census',
      title: 'Census by ward',
      note: 'Every patient in a bed',
      pagePerTable: false,
      tables: [
        {
          heading: 'Ward A (WA)',
          note: '1 patient',
          alert: 'Allergies not recorded for some patients: ask before giving any medicine.',
          columns: ['Bed', 'Patient (UHID)', 'Allergies', 'Isolation'],
          rows: [['A-1', 'Asha <Rao> (NH26000001)', 'ALLERGIES NOT RECORDED', 'Airborne']],
        },
      ],
    },
    {
      key: 'mar',
      title: 'Medication sheets',
      note: null,
      pagePerTable: true,
      tables: [
        {
          heading: 'Ward A, bed A-1',
          note: 'Allergies: none',
          alert: null,
          columns: ['Medicine', 'Due (next 24 h)'],
          rows: [['Ceftriaxone 1 g', '08/11 14:00']],
        },
        { heading: 'Ward A, bed A-2', note: null, alert: null, columns: ['Medicine', 'Due (next 24 h)'], rows: [] },
      ],
    },
  ],
};

const BLOCK: DowntimeNumberBlockSummary = {
  id: 'b1',
  kind: 'Receipt',
  kindName: 'receipt',
  counter: 'OPD billing counter 1',
  count: 50,
  firstNumber: 'NHRC26000101',
  lastNumber: 'NHRC26000150',
  issuedAt: '2026-11-07T08:00:00Z',
  issuedByName: 'Meena Iyer',
  entered: 2,
  voided: 1,
  open: 47,
  returnedAt: null,
  returnedByName: null,
  returnNote: null,
  version: 3,
};

describe('the pre-downtime pack', () => {
  it('is printed on demand and shown section by section', async () => {
    vi.mocked(continuityApi.printPack).mockResolvedValue(PACK);
    const { baseElement } = render(wrap(<PackPanel />));
    await userEvent.click(screen.getByRole('button', { name: 'Print pack now' }));
    expect(await screen.findByRole('region', { name: 'Census by ward' })).toBeInTheDocument();
    expect(screen.getByRole('table', { name: /Ward A \(WA\)/ })).toBeInTheDocument();
    expect(
      screen.getByText('Allergies not recorded for some patients: ask before giving any medicine.'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Print again' })).toBeInTheDocument();
    await expectNoAxeViolations(baseElement);
  });

  it('prints escaped, with each medication sheet on its own page and the time it was made on the cover', () => {
    const html = packHtml(PACK);
    expect(html).toContain('Asha &lt;Rao&gt;');
    expect(html).not.toContain('Asha <Rao>');
    expect(html).toContain('Made ');
    expect(html.match(/class="table page"/g)).toHaveLength(1);
    expect(html).toContain('None.');
  });
});

describe('number blocks', () => {
  it('lists each counter block with its range and progress', async () => {
    blocks.mockResolvedValue([BLOCK]);
    const { baseElement } = render(wrap(<NumberBlocks onOpen={vi.fn()} />));
    const table = await screen
      .findByRole('grid', { name: 'Downtime number blocks' })
      .catch(() => screen.findByRole('table', { name: 'Downtime number blocks' }));
    expect(within(table).getByText('OPD billing counter 1')).toBeInTheDocument();
    expect(within(table).getByText('NHRC26000101 – NHRC26000150')).toBeInTheDocument();
    expect(within(table).getByText('2 of 50 entered, 1 voided')).toBeInTheDocument();
    await expectNoAxeViolations(baseElement);
  });

  it('prints a sheet with one line per number', () => {
    const html = numberSheetHtml({
      block: BLOCK,
      facilityName: 'Nana Hospitals',
      numbers: [
        {
          position: 1,
          number: 'NHRC26000101',
          status: 'Open',
          entityType: null,
          entityId: null,
          patientId: null,
          patientName: null,
          paperAt: null,
          enteredAt: null,
          enteredByName: null,
          lateBackEntry: false,
          voidedAt: null,
          voidReason: null,
          voidedByName: null,
          version: 1,
        },
      ],
    });
    expect(html).toContain('NHRC26000101');
    expect(html).toContain('OPD billing counter 1');
  });

  it('words progress and windows as the server does', () => {
    expect(blockProgress({ ...BLOCK, returnedAt: '2026-11-08T00:00:00Z' })).toBe('2 of 50 entered, 1 voided, returned');
    expect(windowWords(24)).toBe('24 hours');
    expect(windowWords(72)).toBe('3 days');
    expect(windowWords(1)).toBe('1 hour');
  });
});

describe('late back-entry', () => {
  it('checks the form before asking', () => {
    const base = {
      record: 'NursingChart' as const,
      from: '2026-11-01T08:00',
      to: '2026-11-02T08:00',
      records: '12',
      reason: 'Six-day outage',
    };
    expect(lateRequestOf(base).request).toMatchObject({
      record: 'NursingChart',
      records: 12,
      reason: 'Six-day outage',
    });
    expect(lateRequestOf({ ...base, to: '2026-10-31T08:00' }).problem).toBe('The latest time is before the earliest.');
    expect(lateRequestOf({ ...base, records: '0' }).problem).toMatch(/how many records/);
    expect(lateRequestOf({ ...base, reason: ' ' }).problem).toBe('Say why the records are entered late.');
    expect(lateRequestOf({ ...base, from: '' }).problem).toMatch(/earliest and latest/);
  });

  it('asks for approval with the batch from the tally', async () => {
    vi.mocked(continuityApi.requestLate).mockResolvedValue({ id: 'a1' });
    const onClose = vi.fn();
    const { baseElement } = render(
      wrap(
        <RequestLateDialog
          open
          onClose={onClose}
          windows={[
            {
              record: 'NursingChart',
              name: 'Nursing notes and intake/output',
              settingKey: 'k',
              hours: 24,
              defaultHours: 24,
              maxHours: 336,
            },
          ]}
        />,
      ),
    );
    await expectNoAxeViolations(baseElement);
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/Earliest time/), { target: { value: '2026-11-01T08:00' } });
    fireEvent.change(within(dialog).getByLabelText(/Latest time/), { target: { value: '2026-11-02T08:00' } });
    await userEvent.type(within(dialog).getByLabelText(/How many records/), '12');
    await userEvent.type(within(dialog).getByLabelText(/Why late/), 'Six-day outage');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));
    expect(continuityApi.requestLate).toHaveBeenCalledWith(
      expect.objectContaining({ record: 'NursingChart', records: 12 }),
    );
  });
});

describe('the read-only offline copy', () => {
  it('shows nothing while the server can be reached', () => {
    useOfflineCopy.setState({ takenAt: Date.now(), pack: PACK, worklists: [] });
    render(wrap(<OfflineCopyBanner />));
    expect(screen.queryByText(/Read-only copy/)).not.toBeInTheDocument();
  });

  it('says how old the copy is when the server cannot be reached, and opens it with nothing to save', async () => {
    const takenAt = Date.parse('2026-11-08T08:00:00Z');
    useOfflineCopy.setState({
      takenAt,
      pack: PACK,
      worklists: [
        { slot: 'worklist:lab', title: 'Laboratory worklist', rows: [{ patientName: 'Asha Rao', test: 'CBC' }] },
      ],
    });
    act(() => useConnection.setState({ serverReachable: false }));
    const { baseElement } = render(wrap(<OfflineCopyBanner now={() => takenAt + 3 * 3_600_000} />));
    expect(screen.getByText(/ward sheets and medicine lists are 3 hours old/)).toBeInTheDocument();
    expect(screen.getByText(/worklists were fetched .*, 3 hours ago/)).toBeInTheDocument();
    await expectNoAxeViolations(baseElement);
    await userEvent.click(screen.getByRole('button', { name: 'Open the offline copy' }));
    const dialog = await screen.findByRole('dialog', { name: 'Offline copy (read only)' });
    expect(within(dialog).getByRole('region', { name: 'Census by ward' })).toBeInTheDocument();
    expect(within(dialog).getByRole('region', { name: 'Laboratory worklist' })).toBeInTheDocument();
    // Read only: the copy offers nothing to save, only to close.
    expect(
      within(dialog)
        .getAllByRole('button')
        .map((b) => b.textContent),
    ).toEqual(['Close']);
    await expectNoAxeViolations(baseElement);
  });

  it('labels the ward sheets with the pack time, not the fetch time, and warns when the pack is over an hour old', () => {
    // The pack was made at 02:05, the nightly one; the copy was fetched at 14:30, when the screen went dark.
    const generatedAt = '2026-11-14T02:05:00+05:30';
    const fetchedAt = Date.parse('2026-11-14T14:30:00+05:30');
    useOfflineCopy.setState({ takenAt: fetchedAt, pack: { ...PACK, kind: 'Nightly', generatedAt }, worklists: [] });
    act(() => useConnection.setState({ serverReachable: false }));
    render(wrap(<OfflineCopyBanner now={() => fetchedAt + 20 * 60_000} />));
    expect(screen.getByText(/ward sheets and medicines as of 14 Nov 2026, 2:05 am/)).toBeInTheDocument();
    expect(screen.getByText(/medicine lists are 12 hours old/)).toBeInTheDocument();
    expect(screen.getByText(/worklists were fetched 14 Nov 2026, 2:30 pm, 20 minutes ago/)).toBeInTheDocument();
    expect(screen.getByText(/check medicines against the paper MAR/)).toBeInTheDocument();
  });

  it('does not warn about medicines while the pack is less than an hour old', () => {
    const fetchedAt = Date.parse('2026-11-14T14:30:00+05:30');
    useOfflineCopy.setState({
      takenAt: fetchedAt,
      pack: { ...PACK, kind: 'Refreshed', generatedAt: '2026-11-14T14:10:00+05:30' },
      worklists: [],
    });
    act(() => useConnection.setState({ serverReachable: false }));
    render(wrap(<OfflineCopyBanner now={() => fetchedAt + 5 * 60_000} />));
    expect(screen.getByText(/ward sheets and medicines as of 14 Nov 2026, 2:10 pm/)).toBeInTheDocument();
    expect(screen.queryByText(/paper MAR/)).not.toBeInTheDocument();
  });

  it('points to the paper pack when there is no copy', () => {
    act(() => useConnection.setState({ serverReachable: false }));
    render(wrap(<OfflineCopyBanner />));
    expect(screen.getByText('No offline copy on this computer')).toBeInTheDocument();
  });
});
