import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ScannedBatch } from './api';

// Pharmacy tags go through the one label print path, with the same facts as the tag page.
const printLabelJob = vi.hoisted(() => vi.fn(async () => ({ via: 'commands', printer: 'TSC TE244' })));
vi.mock('@/lib/labelPrinting', () => ({ printLabelJob }));

const {
  batchTagValue,
  buildTagsHtml,
  isBatchTag,
  isExactScan,
  MAX_TAGS,
  pharmacyTagData,
  printPharmacyTags,
  scannedBatchProblem,
  tagCount,
} = await import('./tags');

const tag = {
  itemCode: 'pcm500',
  medicine: 'Paracetamol 500 mg tab',
  batchNumber: ' AB1234 ',
  expiryDate: '2027-09-30',
  mrp: 2.5,
  unit: 'tab',
  copies: 2,
};

beforeEach(() => printLabelJob.mockClear());

describe('the tag barcode', () => {
  it('joins the upper-cased item code and the batch with #', () => {
    expect(batchTagValue('pcm500', ' AB1234 ')).toBe('PCM500#AB1234');
    expect(batchTagValue('PH/AMOX-250', 'B 22/07')).toBe('PH/AMOX-250#B 22/07');
  });

  it('falls back to the item code when the batch is missing or cannot be coded', () => {
    expect(batchTagValue('PCM500')).toBe('PCM500');
    expect(batchTagValue('PCM500', '  ')).toBe('PCM500');
    expect(batchTagValue('PCM500', 'AB"12')).toBe('PCM500');
    expect(batchTagValue('PCM500', 'AB!12')).toBe('PCM500');
    expect(batchTagValue('PCM500', 'Ä1')).toBe('PCM500');
    expect(batchTagValue('PCM500', 'X'.repeat(42))).toBe('PCM500');
    expect(batchTagValue('PCM500', 'X'.repeat(41))).toBe(`PCM500#${'X'.repeat(41)}`);
  });

  it('is told apart from an ordinary search as the server does', () => {
    expect(isBatchTag('PCM500#AB1234')).toBe(true);
    expect(isBatchTag(' pcm500 # ab1234 ')).toBe(true);
    expect(isBatchTag('PCM500#AB#2')).toBe(true);
    expect(isBatchTag('PCM500')).toBe(false);
    expect(isBatchTag('#AB1234')).toBe(false);
    expect(isBatchTag('PCM500#')).toBe(false);
    expect(isBatchTag('PCM 500#AB1234')).toBe(false);
    expect(isBatchTag(`PCM500#${'X'.repeat(41)}`)).toBe(false);
  });
});

describe('printing tags', () => {
  it('sends the server the medicine, batch, expiry and MRP per unit', () => {
    expect(pharmacyTagData(tag)).toEqual({
      barcode: 'PCM500#AB1234',
      medicine: 'Paracetamol 500 mg tab',
      batch: 'AB1234',
      expiry: '2027-09-30',
      mrp: 2.5,
      unit: 'tab',
    });
    expect(pharmacyTagData({ itemCode: 'GAUZE', medicine: 'Gauze roll', copies: 1 })).toEqual({
      barcode: 'GAUZE',
      medicine: 'Gauze roll',
      batch: null,
      expiry: null,
      mrp: null,
      unit: null,
    });
  });

  it('prints each tag as many times as asked, in one job', async () => {
    await printPharmacyTags([tag, { ...tag, itemCode: 'AMX250', batchNumber: 'Q9', copies: 1 }, { ...tag, copies: 0 }]);

    expect(printLabelJob).toHaveBeenCalledTimes(1);
    const [job] = printLabelJob.mock.calls[0] as unknown as [{ kind: string; labels: { barcode: string }[] }];
    expect(job.kind).toBe('pharmacy-tags');
    expect(job.labels.map((l) => l.barcode)).toEqual(['PCM500#AB1234', 'PCM500#AB1234', 'AMX250#Q9']);
  });

  it('counts whole tags only and refuses more than one print takes', async () => {
    expect(tagCount([tag, { ...tag, copies: 2.7 }, { ...tag, copies: -3 }])).toBe(4);

    await expect(printPharmacyTags([{ ...tag, copies: MAX_TAGS + 1 }])).rejects.toThrow(
      `Print at most ${MAX_TAGS} tags at a time.`,
    );
    await printPharmacyTags([{ ...tag, copies: 0 }]);
    expect(printLabelJob).not.toHaveBeenCalled();
  });

  it('puts the same facts on the tag page, escaping text', async () => {
    const html = await buildTagsHtml([{ ...pharmacyTagData(tag), medicine: 'Iron <syrup>' }]);

    expect(html).toContain('Iron &lt;syrup&gt;');
    expect(html).toContain('B AB1234');
    expect(html).toContain('MRP Rs 2.50/tab');
    expect(html).toContain('PCM500#AB1234');
    expect(html).toContain('<svg');
  });
});

describe('a scanned code', () => {
  const row = { code: 'PCM500', barcode: '8901234567890', scanned: null };

  it('is the item whose code or barcode it is, or the item of a tag', () => {
    expect(isExactScan(row, 'pcm500')).toBe(true);
    expect(isExactScan(row, ' 8901234567890 ')).toBe(true);
    const tagged = { ...row, barcode: null, scanned: { batchNumber: 'AB1234' } as ScannedBatch };
    expect(isExactScan(tagged, 'PCM500#AB1234')).toBe(true);
  });

  it('is not a medicine that only matched on its name', () => {
    expect(isExactScan(row, '500')).toBe(false);
    expect(isExactScan({ ...row, barcode: null }, '8901234567890')).toBe(false);
  });
});

describe('a scanned tag', () => {
  const scanned: ScannedBatch = {
    batchNumber: 'AB1234',
    batchId: 'b1',
    expiryDate: '2027-09-30',
    mrp: 2.5,
    available: 40,
    problem: null,
  };

  it('needs no word when its batch was picked', () => {
    expect(scannedBatchProblem(scanned)).toBeNull();
    expect(scannedBatchProblem(null)).toBeNull();
    expect(scannedBatchProblem(undefined)).toBeNull();
  });

  it('says why its batch cannot be handed out', () => {
    expect(scannedBatchProblem({ ...scanned, batchId: null, problem: 'Batch AB1234 has expired.' })).toBe(
      'Batch AB1234 has expired.',
    );
    expect(scannedBatchProblem({ ...scanned, batchId: null, problem: null })).toBe(
      'Batch AB1234 cannot be handed out here.',
    );
  });
});
