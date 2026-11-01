import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { RecordHistoryDto } from './api';
import { HistoryButton } from './RecordHistory';

// The server is outside the test: what is tested is what a record's History shows of the trail it answers with.
const get = vi.hoisted(() => vi.fn());
vi.mock('@/lib/api', () => ({ api: { get } }));

const history: RecordHistoryDto = {
  entityType: 'Bill',
  entityId: 'b1',
  title: 'bill NHOPB2600012',
  entries: {
    items: [
      {
        id: 3,
        at: '2026-10-31T06:30:00Z',
        by: 'Anita Rao',
        action: 'Bill.update',
        part: null,
        what: 'Changed',
        changes: [
          { field: 'Discount amount', from: '0', to: '250' },
          { field: 'Aadhaar number', from: '***', to: '***' },
        ],
      },
      {
        id: 2,
        at: '2026-10-31T06:10:00Z',
        by: null,
        action: 'billing.bill.written_off',
        part: null,
        what: 'CN26000004: ₹800.00 on bill NHOPB2600012 (asked for by Anita Rao, approved by Ravi Kumar)',
        changes: [],
      },
      {
        id: 4,
        at: '2026-10-31T05:40:00Z',
        by: 'Anita Rao',
        action: 'approval.requested',
        part: 'Approval',
        what: 'Write-off asked for: ₹800.00 on bill NHOPB2600012 (patient cannot pay)',
        changes: [],
      },
      {
        id: 5,
        at: '2026-10-31T05:20:00Z',
        by: 'Ravi Kumar',
        action: 'Receipt.create',
        part: 'Receipt RC2600031',
        what: 'Created',
        changes: [{ field: 'Amount', from: null, to: '3700' }],
      },
      {
        id: 1,
        at: '2026-10-31T05:00:00Z',
        by: 'Ravi Kumar',
        action: 'Bill.create',
        part: null,
        what: 'Created',
        changes: [{ field: 'Patient amount', from: null, to: '4500' }],
      },
    ],
    page: 1,
    pageSize: 25,
    totalCount: 5,
  },
};

function renderButton() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <HistoryButton type="Bill" id="b1" />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('a record history', () => {
  // Fluent's drawer and message bars measure themselves; jsdom has no ResizeObserver.
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

  beforeEach(() => get.mockReset());

  it('is read only when it is opened, and lists who changed what, newest first, each field from what to what', async () => {
    get.mockResolvedValue(history);
    renderButton();
    expect(get).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: 'History' }));
    const drawer = await screen.findByRole('dialog', { name: 'History of bill NHOPB2600012' });
    expect(get).toHaveBeenCalledWith(
      '/api/v1/history/Bill/b1',
      expect.objectContaining({ query: { page: 1, pageSize: 25 } }),
    );

    const list = within(drawer).getByRole('list', { name: 'Changes, newest first' });
    // An entry about a part of the bill (an approval asked for on it, a receipt) says which part.
    expect([...list.children].map((item) => item.firstElementChild?.textContent)).toEqual([
      'Changed',
      'CN26000004: ₹800.00 on bill NHOPB2600012 (asked for by Anita Rao, approved by Ravi Kumar)',
      'Approval — Write-off asked for: ₹800.00 on bill NHOPB2600012 (patient cannot pay)',
      'Receipt RC2600031 — Created',
      'Created',
    ]);

    // The change lists each field from what to what; a masked value stays masked; nothing before is shown as "—".
    const changed = within(drawer).getByRole('list', { name: 'Changed, by field' });
    expect(
      within(changed)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(['Discount amount: 0 → 250', 'Aadhaar number: *** → ***']);
    expect(within(drawer).getByRole('list', { name: 'Created, by field' }).textContent).toBe(
      'Patient amount: — → 4500',
    );
    expect(within(drawer).getByRole('list', { name: 'Receipt RC2600031 — Created, by field' }).textContent).toBe(
      'Amount: — → 3700',
    );

    // Who, on each entry: the person, or the system for an entry with none.
    expect([...list.children].map((item) => item.children[1]?.textContent?.split(' · ')[1])).toEqual([
      'Anita Rao',
      'The system',
      'Anita Rao',
      'Ravi Kumar',
      'Ravi Kumar',
    ]);
  });

  it('says so when nothing has been recorded about the record', async () => {
    get.mockResolvedValue({ ...history, entries: { ...history.entries, items: [], totalCount: 0 } });
    renderButton();
    await userEvent.click(screen.getByRole('button', { name: 'History' }));
    expect(await screen.findByText('Nothing has been recorded about this record yet.')).toBeVisible();
  });
});
