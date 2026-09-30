import { printDocument, printHtml } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { POSTER_STYLES, posterHtml, type PosterData } from './poster';

/** Prints the A4 QR poster (or kiosk card) for a feedback form. The QR code is drawn locally. */
export async function printQrPoster(data: PosterData): Promise<void> {
  const qr = await barcodeSvg(data.url, 'qrcode', { scale: 6 });
  await printHtml(
    printDocument(posterHtml(data, qr), {
      title: `Feedback QR - ${data.formTitle}`,
      pageSize: 'A4',
      styles: POSTER_STYLES,
    }),
  );
}
