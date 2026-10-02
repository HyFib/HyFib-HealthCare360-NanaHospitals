import { ApiError } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { printServerDocument } from '@/lib/documents';
import type { BillDetail } from './api';
import { useBillingPrint } from './hooks';
import type * as PrintModule from './print';
import { sendToPrinter } from './print';

// The server document and the printer are outside the test; which of them a bill goes to is what is tested.
vi.mock('@/lib/documents', () => ({ printServerDocument: vi.fn(async () => undefined) }));
vi.mock('./print', async (importOriginal) => ({
  ...(await importOriginal<typeof PrintModule>()),
  sendToPrinter: vi.fn(async () => undefined),
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', () => ({ useNotify: () => notify }));

const bill = {
  id: 'b1',
  billNumber: 'NHOB260000001',
  type: 'OutPatient',
  isFinalBill: false,
  status: 'Paid',
  isTaxInvoice: false,
  patientName: 'Ravi Kumar',
  uhid: 'NH26000123',
  createdAt: '2026-09-25T06:30:00Z',
  finalizedAt: '2026-09-25T06:35:00Z',
  grossAmount: 500,
  packageCoveredAmount: 0,
  tariffDiscountAmount: 0,
  lineDiscountAmount: 0,
  billDiscountAmount: 0,
  discountReason: null,
  taxableAmount: 500,
  cgstAmount: 0,
  sgstAmount: 0,
  roundOffAmount: 0,
  netAmount: 500,
  payerAmount: 0,
  patientAmount: 500,
  creditAmount: 0,
  paidAmount: 500,
  refundedAmount: 0,
  refundableAmount: 0,
  dueAmount: 0,
  lines: [],
  receipts: [],
} as unknown as BillDetail;

function wrapper({ children }: { children: ReactNode }) {
  return createElement(QueryClientProvider, { client: new QueryClient() }, children);
}

describe('useBillingPrint', () => {
  beforeEach(() => {
    vi.mocked(printServerDocument).mockReset().mockResolvedValue(undefined);
    vi.mocked(sendToPrinter).mockClear();
    notify.error.mockClear();
  });

  it('prints the A4 bill from the server, the same document as its PDF', async () => {
    const { result } = renderHook(() => useBillingPrint(), { wrapper });

    await result.current.printBill(bill, 'A4');

    expect(printServerDocument).toHaveBeenCalledWith('/api/v1/billing/bills/b1');
    expect(sendToPrinter).not.toHaveBeenCalled();
  });

  it('prints the 80 mm bill on the counter printer from the browser', async () => {
    const { result } = renderHook(() => useBillingPrint(), { wrapper });

    await result.current.printBill(bill, 'Receipt80');

    expect(printServerDocument).not.toHaveBeenCalled();
    expect(sendToPrinter).toHaveBeenCalledWith(expect.stringContaining('NHOB260000001'), 'Receipt80');
  });

  it('says so when the A4 bill cannot be printed', async () => {
    const failure = new ApiError({ status: 404, title: 'Not found' });
    vi.mocked(printServerDocument).mockRejectedValue(failure);
    const { result } = renderHook(() => useBillingPrint(), { wrapper });

    await result.current.printBill(bill, 'A4');

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(failure, 'Printing failed'));
  });
});
