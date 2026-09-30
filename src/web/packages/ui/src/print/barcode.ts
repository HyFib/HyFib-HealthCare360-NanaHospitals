export type BarcodeKind = 'code128' | 'qrcode';

/**
 * Barcode/QR as an SVG string for print templates (UHID cards, wristbands, sample labels, token slips, QR
 * verification on reports). Rendering is local — nothing leaves the workstation. The encoder (~1 MB, every
 * symbology) is loaded on first use only, so screens that never print do not download it.
 */
export async function barcodeSvg(
  value: string,
  kind: BarcodeKind = 'code128',
  options: { height?: number; scale?: number } = {},
): Promise<string> {
  const { toSVG } = await import('bwip-js/browser');
  const common = {
    bcid: kind,
    text: value,
    scale: options.scale ?? 2,
    includetext: false,
    paddingwidth: 2,
    paddingheight: 2,
  };
  // bwip-js rejects an explicit `height: undefined`, and QR codes are square, so only linear codes get a height.
  return toSVG(kind === 'code128' ? { ...common, height: options.height ?? 10 } : common);
}
