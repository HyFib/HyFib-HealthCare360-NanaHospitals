import type { Gstr1Return, Gstr3bReturn } from '@/modules/billing/gstReturnsApi';
import type { FakeAnswers } from '../fakeApi';

// GST returns: a month with a B2B bill, a B2C row, exempt services, the HSN summary and the documents issued.

const zero = { taxable: 0, igst: 0, cgst: 0, sgst: 0, cess: 0 };

const GSTR1: Gstr1Return = {
  gstin: '33AABCN1234F1Z3',
  year: 2026,
  month: 10,
  period: '102026',
  b2b: [
    {
      number: 'NHIB26000001',
      date: '2026-10-05',
      buyerGstin: '29AAACI1681G1ZM',
      buyerName: 'Infosys Limited',
      placeOfSupply: '29',
      value: 13800,
      items: [{ rate: 18, amounts: { ...zero, taxable: 10000, igst: 1800 } }],
      kind: 'Invoice',
    },
  ],
  b2cl: [],
  b2cs: [{ interState: false, placeOfSupply: '33', rate: 18, amounts: { ...zero, taxable: 900, cgst: 81, sgst: 81 } }],
  cdnr: [],
  cdnur: [],
  nil: [{ supplyType: 'INTRAB2C', nilRated: 0, exempt: 500, nonGst: 0 }],
  hsnB2b: [{ hsn: '999722', description: 'Cosmetic procedure', uqc: 'NA', quantity: 0, rate: 18, amounts: { ...zero, taxable: 10000, igst: 1800 } }],
  hsnB2c: [{ hsn: '999312', description: 'Consultation', uqc: 'NA', quantity: 0, rate: 0, amounts: { ...zero, taxable: 500 } }],
  documentsIssued: [{ nature: 1, series: 'OP bills', from: 'NHOB26000001', to: 'NHOB26000002', total: 2, cancelled: 1, net: 1 }],
  problems: [],
};

const GSTR3B: Gstr3bReturn = {
  gstin: '33AABCN1234F1Z3',
  year: 2026,
  month: 10,
  outwardTaxable: { ...zero, taxable: 10900, igst: 1800, cgst: 81, sgst: 81 },
  zeroRated: zero,
  nilAndExempt: 500,
  inwardReverseCharge: zero,
  nonGst: 0,
  interStateToUnregistered: [],
  itcAllOther: { ...zero, taxable: 50000, cgst: 3000, sgst: 3000 },
  itcReversed: zero,
  itcNet: { ...zero, taxable: 50000, cgst: 3000, sgst: 3000 },
  purchaseInvoices: 1,
  purchasesWithoutGstin: 0,
  exemptShareOfTurnover: 0.0439,
  notes: ['Credit is shown as the hospital’s purchase records hold it; claim it only as far as it appears in GSTR-2B (CGST rule 36(4)).'],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/billing/gst-returns/gstr1': GSTR1,
  'GET /api/v1/billing/gst-returns/gstr3b': GSTR3B,
};
