import { formatInr } from '@hc360/core';
import type { DeviceDetails } from './api';

/**
 * What a bill line says of a device beneath its name, as the server's printed bill does (NPPA): "Brand XIENCE Sierra ·
 * made or imported by Abbott Vascular · Batch 7041231 · Drug-eluting, 3.0 × 28 mm · MRP ₹38,933.14".
 */
export function deviceDetailsText(device: DeviceDetails): string {
  return [
    device.brand ? `Brand ${device.brand}` : null,
    device.manufacturer ? `made or imported by ${device.manufacturer}` : null,
    device.batch ? `Batch ${device.batch}` : null,
    device.serial ? `S/N ${device.serial}` : null,
    device.specification,
    device.mrp !== null ? `MRP ${formatInr(device.mrp)}` : null,
    device.ceilingPrice !== null ? `NPPA ceiling price ${formatInr(device.ceilingPrice)}` : null,
  ]
    .filter((part): part is string => Boolean(part))
    .join(' · ');
}
