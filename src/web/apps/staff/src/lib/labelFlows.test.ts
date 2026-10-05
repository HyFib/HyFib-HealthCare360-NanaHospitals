import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { BloodUnitDto } from '@/modules/blood-bank/api';
import type { LoadDto } from '@/modules/cssd/api';

// The label flows of the blood bank, CSSD and stores send the server the same facts as their own label pages, and go
// through the one label print path.
const printLabelJob = vi.hoisted(() => vi.fn(async () => ({ via: 'commands', printer: 'TSC TE244' })));
vi.mock('@/lib/labelPrinting', () => ({ printLabelJob }));

const { printUnitLabels, unitLabelData } = await import('@/modules/blood-bank/print');
const { packLabelData, printPackLabels } = await import('@/modules/cssd/print');
const { assetTagData, printAssetTag } = await import('@/modules/assets/print');

const unit = {
  id: 'u1',
  unitNumber: 'NH-BU-2600123',
  donationId: 'd1',
  componentType: 'PRBC',
  bloodGroup: 'ABNegative',
  volumeMl: 350,
  collectedAt: '2026-09-20T05:00:00Z',
  expiresAt: '2026-11-05T08:30:00Z',
  status: 'Available',
  tti: {
    hiv: 'NonReactive',
    hbsAg: 'NonReactive',
    hcv: 'NonReactive',
    syphilis: 'NonReactive',
    malaria: 'NonReactive',
  },
  storageLocation: null,
  reservedForRequestId: null,
  issuedAt: null,
  discardReason: null,
  isExpired: false,
} as unknown as BloodUnitDto;

const load = {
  id: 'l1',
  cycleNumber: 'CY260001',
  method: 'Steam',
  packs: [
    { id: 'p1', packNumber: 'CS2600000123', setName: 'General surgery set', sterileUntil: '2026-10-30' },
    { id: 'p2', packNumber: 'CS2600000124', setName: null, sterileUntil: null },
  ],
} as unknown as LoadDto;

describe('label flows', () => {
  beforeEach(() => printLabelJob.mockClear());

  it('labels a blood unit with its group, component and screening, and marks units in quarantine', async () => {
    expect(unitLabelData(unit)).toEqual({
      unitNumber: 'NH-BU-2600123',
      bloodGroup: 'AB−',
      component: 'PRBC',
      volumeMl: 350,
      cleared: true,
      expiresAt: '2026-11-05T08:30:00Z',
      storage: 'Store at 2–6 °C',
    });
    expect(unitLabelData({ ...unit, status: 'Quarantine' }).cleared).toBe(false);
    expect(unitLabelData({ ...unit, bloodGroup: 'Unknown' }).bloodGroup).toBe('UNGROUPED');

    await printUnitLabels([unit]);
    expect(printLabelJob).toHaveBeenCalledWith(
      expect.objectContaining({ kind: 'blood-units', labels: [unitLabelData(unit)] }),
    );
  });

  it('labels the packs of a load, or the ones asked for, and leaves unreleased packs to say so', async () => {
    expect(packLabelData(load)).toEqual([
      {
        packNumber: 'CS2600000123',
        setName: 'General surgery set',
        method: 'Steam (autoclave)',
        sterileUntil: '2026-10-30',
      },
      { packNumber: 'CS2600000124', setName: 'Instrument set', method: 'Steam (autoclave)', sterileUntil: null },
    ]);
    expect(packLabelData(load, ['p2']).map((p) => p.packNumber)).toEqual(['CS2600000124']);

    await printPackLabels(load, ['p1']);
    expect(printLabelJob).toHaveBeenCalledWith(
      expect.objectContaining({ kind: 'cssd-packs', labels: packLabelData(load, ['p1']) }),
    );
  });

  it('tags an asset with the hospital, its name and its tag', async () => {
    const asset = { assetTag: 'AST-2600123', name: 'Multipara monitor' };
    expect(assetTagData(asset, 'Nana Hospitals')).toEqual({
      assetTag: 'AST-2600123',
      assetName: 'Multipara monitor',
      hospital: 'Nana Hospitals',
    });

    await printAssetTag(asset, 'Nana Hospitals');
    expect(printLabelJob).toHaveBeenCalledWith(
      expect.objectContaining({ kind: 'asset-tags', labels: [assetTagData(asset, 'Nana Hospitals')] }),
    );
  });

  it('prints nothing for an empty selection', async () => {
    await printUnitLabels([]);
    await printPackLabels(load, []);
    expect(printLabelJob).not.toHaveBeenCalled();
  });
});
