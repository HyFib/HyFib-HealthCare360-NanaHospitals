import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as InpatientApi from './api';
import type { WristbandDto } from './api';

const printLabelJob = vi.hoisted(() => vi.fn(async () => ({ via: 'commands', printer: 'ZDesigner HC100' })));
const wristband = vi.hoisted(() => vi.fn());

vi.mock('@/lib/labelPrinting', () => ({ printLabelJob }));
vi.mock('@hc360/ui', () => ({ barcodeSvg: vi.fn(async () => '<svg id="uhid"></svg>') }));
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof InpatientApi>()),
  admissionApi: { wristband },
}));

const { bandAgeSex, printWristband, wristbandData, wristbandHtml } = await import('./print');

const band: WristbandDto = {
  admissionId: 'a1',
  uhid: 'NH00012345',
  patientName: 'Asha <Rao>',
  gender: 'Female',
  age: '31Y',
  admissionNumber: 'IP2600123',
  wardName: 'Ward 3',
  bedLabel: 'Bed 12',
  allergies: 'Recorded',
};

describe('wristband', () => {
  beforeEach(() => {
    printLabelJob.mockClear();
    wristband.mockReset();
  });

  it('shows age and sex as the band prints them', () => {
    expect(bandAgeSex(band)).toBe('31Y/F');
    expect(bandAgeSex({ ...band, age: null })).toBe('F');
  });

  it('gives a band printer the same facts as the band page, with the allergy mark from the chart', () => {
    expect(wristbandData(band)).toEqual({
      uhid: 'NH00012345',
      patientName: 'Asha <Rao>',
      ageSex: '31Y/F',
      admissionNumber: 'IP2600123',
      ward: 'Ward 3',
      bed: 'Bed 12',
      allergies: 'Recorded',
    });
  });

  it('lays the band page out along an adult band, escaping text, with the black ALLERGY mark when allergies are recorded', async () => {
    const html = await wristbandHtml(band);

    expect(html).toContain('size: 25.4mm 279.4mm');
    expect(html).toContain('Asha &lt;Rao&gt;');
    expect(html).toContain('UHID NH00012345 · 31Y/F');
    expect(html).toContain('IP2600123 · Ward 3 / Bed 12');
    expect(html).toContain('<div class="allergy">ALLERGY</div>');
    expect(html).toContain('<svg id="uhid"></svg>');
    expect(html).not.toContain('NOT RECORDED');
  });

  it('marks allergies never recorded with an outlined mark, and prints no mark for no known allergies', async () => {
    const unknown = await wristbandHtml({ ...band, allergies: 'NotRecorded' });
    expect(unknown).toContain('<div class="allergy-unknown">ALLERGIES<br>NOT RECORDED</div>');
    expect(unknown).not.toContain('<div class="allergy">');

    const none = await wristbandHtml({ ...band, allergies: 'NoKnownAllergies' });
    expect(none).not.toContain('<div class="allergy');
    expect(wristbandData({ ...band, allergies: 'NotRecorded' }).allergies).toBe('NotRecorded');
  });

  it('reads the band fresh from the server and prints it on the wristband printer', async () => {
    wristband.mockResolvedValue(band);

    await printWristband({ id: 'a1' });

    expect(wristband).toHaveBeenCalledWith('a1');
    expect(printLabelJob).toHaveBeenCalledWith(
      expect.objectContaining({ kind: 'wristbands', labels: [wristbandData(band)] }),
    );
  });
});
