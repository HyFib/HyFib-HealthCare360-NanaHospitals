import { describe, expect, it } from 'vitest';
import type { SampleLabel } from './api';
import { labelTests, labelTime, sampleLabelHtml, tubeLabelData } from './labelHtml';

const label: SampleLabel = {
  sampleId: 's1',
  barcode: '000123456',
  accessionNumber: 'NHLB260000123',
  uhid: 'NH26000001',
  patientName: 'Asha <Rao>',
  ageSex: '31Y / F',
  container: 'Edta',
  specimenType: 'WholeBlood',
  section: 'Hematology',
  priority: 'Routine',
  tests: ['Complete blood count', 'ESR'],
  collectedAt: '2026-09-26T05:02:00Z',
};

describe('tube label', () => {
  it('shows patient, UHID, barcode digits, tube and bench, escaping text', () => {
    const html = sampleLabelHtml(label, '<svg id="bc"></svg>');
    expect(html).toContain('Asha &lt;Rao&gt;');
    expect(html).toContain('UHID NH26000001');
    expect(html).toContain('000123456');
    expect(html).toContain('EDTA · HAEM');
    expect(html).toContain('<svg id="bc"></svg>');
    expect(html).toContain('NHLB260000123');
    expect(html).not.toContain('STAT');
  });

  it('marks STAT tubes instead of the accession number', () => {
    const html = sampleLabelHtml({ ...label, priority: 'Stat' }, '');
    expect(html).toContain('class="stat">STAT');
    expect(html).not.toContain('NHLB260000123');
  });

  it('prints the collection time in hospital time, 24-hour', () => {
    const text = labelTime('2026-09-26T05:02:00Z');
    expect(text).toContain('26');
    expect(text).toContain('10:32');
    expect(labelTime(null)).toBe('');
  });

  it('fits the test list and counts the rest', () => {
    expect(labelTests(['CBC', 'ESR'])).toBe('CBC, ESR');
    expect(labelTests(['Complete blood count', 'Erythrocyte sedimentation rate', 'Peripheral smear'], 30)).toBe(
      'Complete blood count +2',
    );
    expect(labelTests(['A very long single test name that never fits'], 20)).toBe('A very long single …');
    expect(labelTests([])).toBe('');
  });

  it('gives a label printer the same facts as the page', () => {
    expect(tubeLabelData({ ...label, priority: 'Stat' })).toEqual({
      barcode: '000123456',
      patientName: 'Asha <Rao>',
      uhid: 'NH26000001',
      ageSex: '31Y / F',
      accessionNumber: 'NHLB260000123',
      stat: true,
      tube: 'EDTA',
      bench: 'HAEM',
      tests: ['Complete blood count', 'ESR'],
      collectedAt: '2026-09-26T05:02:00Z',
    });
    expect(tubeLabelData(label).stat).toBe(false);
  });
});
