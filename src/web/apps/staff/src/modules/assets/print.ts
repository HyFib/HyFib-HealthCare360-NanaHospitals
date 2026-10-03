import { escapeHtml, printDocument, type AssetTagLabel } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { printLabelJob } from '@/lib/labelPrinting';

/** An asset tag as the server writes it for a ZPL or TSPL label printer: the same facts as the tag page. */
export function assetTagData(asset: { assetTag: string; name: string }, hospitalName: string): AssetTagLabel {
  return { assetTag: asset.assetTag, assetName: asset.name, hospital: hospitalName || null };
}

/**
 * The asset tag (AST-01) on this computer's label printer, without a dialog: the hospital, the asset and its tag as
 * Code 128 for the biomedical team's scanner.
 */
export async function printAssetTag(asset: { assetTag: string; name: string }, hospitalName: string): Promise<void> {
  await printLabelJob({
    kind: 'asset-tags',
    labels: [assetTagData(asset, hospitalName)],
    page: async () => {
      const code = await barcodeSvg(asset.assetTag, 'code128', { height: 9 });
      const body = `<div style="width:46mm;font-family:'Segoe UI',sans-serif;text-align:center">
        <div style="font-size:7px;font-weight:700">${escapeHtml(hospitalName)}</div>
        <div style="font-size:8px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${escapeHtml(asset.name)}</div>
        <div>${code}</div><div style="font-family:Consolas,monospace;font-size:9px;letter-spacing:1px">${escapeHtml(asset.assetTag)}</div></div>`;
      return printDocument(body, {
        title: `Asset tag ${asset.assetTag}`,
        pageSize: 'Label50x25',
        styles: 'svg { width: 100%; height: 9mm; }',
      });
    },
  });
}
