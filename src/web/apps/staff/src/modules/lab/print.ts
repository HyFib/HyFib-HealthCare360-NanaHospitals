import { printDocument, printHtml } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import type { SampleLabel } from './api';
import { LABEL_STYLES, sampleLabelHtml } from './labelHtml';

/**
 * Prints one 50 × 25 mm label per tube (Code 128). In HC360 Desktop the labels go straight to the label printer
 * without a dialog, so the phlebotomist can stick them on while the patient is still seated.
 */
export async function printSampleLabels(labels: readonly SampleLabel[]): Promise<void> {
  if (labels.length === 0) {
    return;
  }
  const barcodes = await Promise.all(
    labels.map((label) => barcodeSvg(label.barcode, 'code128', { height: 8, scale: 2 })),
  );
  const body = labels.map((label, index) => sampleLabelHtml(label, barcodes[index] ?? '')).join('');
  const title = labels.length === 1 ? `Label ${labels[0]!.barcode}` : `${labels.length} sample labels`;
  await printHtml(printDocument(body, { title, pageSize: 'Label50x25', styles: LABEL_STYLES }), {
    pageSize: 'Label50x25',
    silent: true,
  });
}
