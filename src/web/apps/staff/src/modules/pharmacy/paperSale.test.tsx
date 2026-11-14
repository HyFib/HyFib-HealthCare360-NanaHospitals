import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState, type ReactNode } from 'react';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as PharmacyApiModule from './api';
import { pharmacyApi, type QueueEntryDetail } from './api';
import { DispensedOnPaperDialog } from './DispensedOnPaperDialog';
import {
  dispensedOnPaperOf,
  NOT_A_PAPER_SALE,
  paperSaleOf,
  paperSaleProblemsOf,
  type PaperSaleEntry,
} from './paperSale';
import { PaperSaleFields } from './PaperSaleFields';

// A pharmacy sale written on paper during a downtime, entered afterwards: what the counter must give (the sale number and
// the time on the paper), what the server refuses, and closing a prescription that was handed over on paper.

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof PharmacyApiModule>();
  return { ...actual, pharmacyApi: { ...actual.pharmacyApi, dispensedOnPaper: vi.fn() } };
});

const NOW = new Date('2026-11-14T10:00:00');

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
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(NOW);
  vi.clearAllMocks();
});

afterEach(() => {
  vi.useRealTimers();
});

function wrap(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return (
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>
  );
}

const onPaper = (patch: Partial<PaperSaleEntry> = {}): PaperSaleEntry => ({
  onPaper: true,
  number: 'nhph260000007',
  at: '2026-11-13T21:30',
  ...patch,
});

describe('paperSaleOf', () => {
  it('sends nothing for a sale made now', () => {
    expect(paperSaleOf(NOT_A_PAPER_SALE)).toEqual({ paper: null, problem: null });
  });

  it('sends the number as printed, in capitals, with the time on the paper as an instant', () => {
    expect(paperSaleOf(onPaper({ number: ' nhph260000007 ' })).paper).toEqual({
      number: 'NHPH260000007',
      at: new Date('2026-11-13T21:30').toISOString(),
    });
  });

  it('asks for the number and the time on the paper before the sale is saved', () => {
    expect(paperSaleOf(onPaper({ number: '' })).problem?.number).toBe('Enter the sale number written on the paper.');
    expect(paperSaleOf(onPaper({ number: 'NH PH 7' })).problem?.number).toBe('Enter the number as it is printed.');
    expect(paperSaleOf(onPaper({ at: '' })).problem?.at).toBe('Enter the date and time written on the paper.');
    expect(paperSaleOf(onPaper({ at: '2026-11-14' })).problem?.at).toBe(
      'Enter the date and time written on the paper.',
    );
  });

  it('refuses a time in the future', () => {
    expect(paperSaleOf(onPaper({ at: '2026-11-14T13:00' })).problem?.at).toBe(
      'The time on the paper cannot be in the future.',
    );
    expect(paperSaleOf(onPaper({ at: '2026-11-14T10:03' })).paper).not.toBeNull();
  });
});

describe('paperSaleProblemsOf', () => {
  const refused = (fieldErrors: Record<string, string[]>) =>
    new ApiError({ status: 400, title: 'Check the form', errors: fieldErrors });

  it('reads the server refusals of the number and the time by field', () => {
    expect(paperSaleProblemsOf(refused({ 'paper.number': ['NHPH1 is not a downtime pharmacy sale number.'] }))).toEqual(
      {
        number: 'NHPH1 is not a downtime pharmacy sale number.',
        at: undefined,
      },
    );
    expect(paperSaleProblemsOf(refused({ 'paper.At': ['The time on the paper cannot be in the future.'] }))?.at).toBe(
      'The time on the paper cannot be in the future.',
    );
  });

  it('leaves any other error to the usual message', () => {
    expect(paperSaleProblemsOf(refused({ lines: ['Add at least one item.'] }))).toBeNull();
    expect(paperSaleProblemsOf(new Error('offline'))).toBeNull();
    expect(paperSaleProblemsOf(null)).toBeNull();
  });
});

describe('PaperSaleFields', () => {
  function Harness({ problems }: { problems?: { number?: string; at?: string } }) {
    const [value, setValue] = useState<PaperSaleEntry>(NOT_A_PAPER_SALE);
    return (
      <>
        <PaperSaleFields value={value} onChange={setValue} problems={problems} />
        <output data-testid="sent">{JSON.stringify(paperSaleOf(value))}</output>
      </>
    );
  }

  it('shows the number and the time only for a paper sale, and carries what is typed', async () => {
    const user = userEvent.setup();
    render(wrap(<Harness />));
    expect(screen.queryByLabelText(/sale number on the paper/i)).toBeNull();

    await user.click(screen.getByRole('checkbox', { name: /written on paper during a downtime/i }));
    await user.type(screen.getByLabelText(/sale number on the paper/i), 'nhph260000007');
    await user.type(screen.getByLabelText(/sold on the paper at/i), '2026-11-13T21:30');

    const sent = JSON.parse(screen.getByTestId('sent').textContent!) as { paper: { number: string; at: string } };
    expect(sent.paper).toEqual({ number: 'NHPH260000007', at: new Date('2026-11-13T21:30').toISOString() });
  });

  it("shows the server's message under the number", async () => {
    const user = userEvent.setup();
    render(wrap(<Harness problems={{ number: 'NHPH1 was voided and cannot be used.' }} />));
    await user.click(screen.getByRole('checkbox', { name: /written on paper during a downtime/i }));
    expect(screen.getByText('NHPH1 was voided and cannot be used.')).toBeTruthy();
  });
});

describe('dispensedOnPaperOf', () => {
  it('needs the time on the paper, and a sale number or a note of where the paper is', () => {
    expect(dispensedOnPaperOf({ at: '', invoiceNumber: 'nhph1', note: '' }).problem).toMatch(/date and time/);
    expect(dispensedOnPaperOf({ at: '2026-11-14T13:00', invoiceNumber: 'nhph1', note: '' }).problem).toMatch(/future/);
    expect(dispensedOnPaperOf({ at: '2026-11-13T21:30', invoiceNumber: '', note: ' ' }).problem).toMatch(/sale number/);
    expect(dispensedOnPaperOf({ at: '2026-11-13T21:30', invoiceNumber: ' nhph1 ', note: '' }).body).toEqual({
      at: new Date('2026-11-13T21:30').toISOString(),
      invoiceNumber: 'NHPH1',
      note: null,
    });
    expect(dispensedOnPaperOf({ at: '2026-11-13T21:30', invoiceNumber: '', note: 'Sale book 3' }).body?.note).toBe(
      'Sale book 3',
    );
  });
});

describe('DispensedOnPaperDialog', () => {
  const entry = { id: 'q1', prescriptionNumber: 'NHRX26000042' } as QueueEntryDetail;

  it('closes the prescription as dispensed with the time on the paper and the sale it was entered under', async () => {
    vi.mocked(pharmacyApi.dispensedOnPaper).mockResolvedValue(undefined);
    const onClose = vi.fn();
    const user = userEvent.setup({ advanceTimers: () => undefined });
    render(wrap(<DispensedOnPaperDialog entry={entry} onClose={onClose} />));
    const close = screen.getByRole('button', { name: /close as dispensed/i });
    expect(close.hasAttribute('disabled')).toBe(true);

    await user.type(screen.getByLabelText(/dispensed on the paper at/i), '2026-11-13T21:30');
    await user.type(screen.getByLabelText(/sale number on the paper/i), 'nhph260000007');
    await user.click(close);

    expect(pharmacyApi.dispensedOnPaper).toHaveBeenCalledWith('q1', {
      at: new Date('2026-11-13T21:30').toISOString(),
      invoiceNumber: 'NHPH260000007',
      note: null,
    });
  });
});
