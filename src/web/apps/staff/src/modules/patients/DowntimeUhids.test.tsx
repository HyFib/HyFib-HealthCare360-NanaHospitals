import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as DowntimeApi from './downtimeApi';
import {
  downtimeApi,
  NOT_ON_PAPER,
  paperOf,
  paperTimeToIso,
  progressText,
  type DowntimeUhidBlockDetail,
  type DowntimeUhidBlockSummary,
} from './downtimeApi';
import { DowntimeBlockDrawer, DowntimeBlocks, IssueDowntimeUhidsDialog } from './DowntimeUhids';

// Downtime UHIDs: issuing a desk's kit, following its back-entry and voiding a spoilt number. The server is replaced.

const list = vi.hoisted(() => vi.fn());
const block = vi.hoisted(() => vi.fn());
vi.mock('./downtimeApi', async (importOriginal) => {
  const actual = await importOriginal<typeof DowntimeApi>();
  return {
    ...actual,
    downtimeApi: { issue: vi.fn(), void: vi.fn() },
    downtimeQueries: {
      list: () => ({ queryKey: ['downtime'], queryFn: () => list() }),
      block: (id: string) => ({ queryKey: ['downtime', id], queryFn: () => block(id) }),
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

const SUMMARY: DowntimeUhidBlockSummary = {
  id: 'b1',
  label: 'Main reception, kit 1',
  count: 3,
  firstUhid: 'NH26000501',
  lastUhid: 'NH26000503',
  issuedAt: '2026-10-20T04:30:00Z',
  issuedByName: 'Meena Iyer',
  entered: 1,
  voided: 1,
  open: 1,
};

const DETAIL: DowntimeUhidBlockDetail = {
  block: SUMMARY,
  facilityName: 'Nana Hospitals, Pune',
  numbers: [
    {
      position: 1,
      uhid: 'NH26000501',
      status: 'Entered',
      patientId: 'p1',
      patientName: 'Ravi Kulkarni',
      paperRegisteredAt: '2026-10-21T05:10:00Z',
      enteredAt: '2026-10-21T09:00:00Z',
      enteredByName: 'Front desk',
      voidedAt: null,
      voidReason: null,
      voidedByName: null,
      version: 3,
    },
    {
      position: 2,
      uhid: 'NH26000502',
      status: 'Voided',
      patientId: null,
      patientName: null,
      paperRegisteredAt: null,
      enteredAt: null,
      enteredByName: null,
      voidedAt: '2026-10-21T09:05:00Z',
      voidReason: 'Label torn',
      voidedByName: 'Meena Iyer',
      version: 4,
    },
    {
      position: 3,
      uhid: 'NH26000503',
      status: 'Open',
      patientId: null,
      patientName: null,
      paperRegisteredAt: null,
      enteredAt: null,
      enteredByName: null,
      voidedAt: null,
      voidReason: null,
      voidedByName: null,
      version: 2,
    },
  ],
};

describe('Downtime UHIDs', () => {
  it('lists the kits with how far each was entered', async () => {
    list.mockResolvedValue([SUMMARY]);
    const open = vi.fn();
    render(wrap(<DowntimeBlocks onOpen={open} />));

    const row = (await screen.findByText('Main reception, kit 1')).closest('tr')!;
    expect(within(row).getByText('NH26000501 – NH26000503')).toBeInTheDocument();
    expect(within(row).getByText('1 of 3 entered, 1 voided')).toBeInTheDocument();
    await userEvent.click(row);
    expect(open).toHaveBeenCalledWith('b1');
  });

  it('issues a block for a kit and opens it', async () => {
    vi.mocked(downtimeApi.issue).mockResolvedValue(DETAIL);
    const issued = vi.fn();
    render(wrap(<IssueDowntimeUhidsDialog open onClose={() => {}} onIssued={issued} />));

    const issue = screen.getByRole('button', { name: 'Issue' });
    expect(issue).toBeDisabled();
    await userEvent.type(screen.getByRole('textbox', { name: /Kit/ }), 'Main reception, kit 1');
    await userEvent.click(issue);

    expect(downtimeApi.issue).toHaveBeenCalledWith({ label: 'Main reception, kit 1', count: 25 });
    expect(issued).toHaveBeenCalledWith(DETAIL);
  });

  it('shows who was entered under each UHID and voids an open one with a reason', async () => {
    block.mockResolvedValue(DETAIL);
    vi.mocked(downtimeApi.void).mockResolvedValue(undefined);
    render(wrap(<DowntimeBlockDrawer blockId="b1" onClose={() => {}} />));

    expect(await screen.findByText(/Ravi Kulkarni, registered/)).toBeInTheDocument();
    expect(screen.getByText(/Label torn/)).toBeInTheDocument();
    // Only the open number can be voided.
    const voids = screen.getAllByRole('button', { name: 'Void' });
    expect(voids).toHaveLength(1);
    await userEvent.click(voids[0]!);

    const dialog = await screen.findByRole('dialog', { name: 'Void NH26000503' });
    await userEvent.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Kit withdrawn');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Void' }));
    expect(downtimeApi.void).toHaveBeenCalledWith('b1', { uhid: 'NH26000503', reason: 'Kit withdrawn', version: 2 });
  });
});

describe('Paper registrations', () => {
  it('reads the time on the form as the browser enters it, and nothing else', () => {
    expect(paperTimeToIso('2026-10-21T10:40')).toBe(new Date('2026-10-21T10:40').toISOString());
    expect(paperTimeToIso('2026-10-21')).toBeNull();
    expect(paperTimeToIso('')).toBeNull();
  });

  it('carries the UHID and the time only when entering a paper form, and says what is missing', () => {
    expect(paperOf(NOT_ON_PAPER)).toEqual({ paper: null, problem: null });
    expect(paperOf({ onPaper: true, uhid: ' ', at: '2026-10-21T10:40' }).problem).toMatch(/UHID/);
    expect(paperOf({ onPaper: true, uhid: 'nh26000503', at: '' }).problem).toMatch(/date and time/);
    expect(paperOf({ onPaper: true, uhid: ' nh26000503 ', at: '2026-10-21T10:40' }).paper).toEqual({
      uhid: 'NH26000503',
      registeredAt: new Date('2026-10-21T10:40').toISOString(),
    });
  });

  it('describes a block by how many were entered', () => {
    expect(progressText({ count: 25, entered: 0, voided: 0 })).toBe('0 of 25 entered');
    expect(progressText({ count: 25, entered: 20, voided: 2 })).toBe('20 of 25 entered, 2 voided');
  });
});
