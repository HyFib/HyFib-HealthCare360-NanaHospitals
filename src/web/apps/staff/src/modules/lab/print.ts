import { printDocument } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { printLabelJob } from '@/lib/labelPrinting';
import type { SampleLabel } from './api';
import { LABEL_STYLES, sampleLabelHtml, tubeLabelData } from './labelHtml';

/**
 * Prints one label per tube on this computer's label printer, without a dialog, so the phlebotomist can stick them on
 * while the patient is still seated: printer commands for a ZPL or TSPL printer, else the 50 × 25 mm label page.
 */
export async function printSampleLabels(labels: readonly SampleLabel[]): Promise<void> {
  if (labels.length === 0) {
    return;
  }
  await printLabelJob({
    kind: 'lab-tubes',
    labels: labels.map(tubeLabelData),
    page: async () => {
      const barcodes = await Promise.all(
        labels.map((label) => barcodeSvg(label.barcode, 'code128', { height: 8, scale: 2 })),
      );
      const body = labels.map((label, index) => sampleLabelHtml(label, barcodes[index] ?? '')).join('');
      const title = labels.length === 1 ? `Label ${labels[0]!.barcode}` : `${labels.length} sample labels`;
      return printDocument(body, { title, pageSize: 'Label50x25', styles: LABEL_STYLES });
    },
  });
}
