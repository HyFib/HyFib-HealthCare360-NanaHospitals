import type { EInvoiceRegister } from '@/modules/billing/einvoiceApi';
import type { FakeAnswers } from '../fakeApi';

// GST e-invoices: a bill waiting for its IRN and a pharmacy B2B sale with one recorded.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const today = new Date().toISOString().slice(0, 10);

const REGISTER: EInvoiceRegister = {
  applicable: true,
  applicableFrom: '2026-04-01',
  from: today,
  to: today,
  rows: [
    {
      source: 'bill',
      id: id('e1'),
      number: 'NHOB260000123',
      date: today,
      buyerGstin: '32AAACH7409R1ZA',
      buyerName: 'Malabar Traders Ltd',
      total: 1180,
      state: 'Pending',
      reportBy: today,
      isOverdue: false,
      irn: null,
      ackNo: null,
      ackDate: null,
    },
    {
      source: 'pharmacy',
      id: id('e2'),
      number: 'NHPH260000456',
      date: today,
      buyerGstin: '33AAACH7409R1Z8',
      buyerName: 'Sree Clinic',
      total: 560,
      state: 'Registered',
      reportBy: today,
      isOverdue: false,
      irn: 'a'.repeat(64),
      ackNo: '112610012345678',
      ackDate: new Date().toISOString(),
    },
  ],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/billing/einvoices': REGISTER,
};
