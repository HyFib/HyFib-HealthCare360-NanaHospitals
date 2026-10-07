import type { DiskEncryptionReport, VolumeEncryption } from './api';

/** How a disk stands, in words, and how worrying that is. */
function volumeState(volume: VolumeEncryption): { text: string; tone: 'ok' | 'warn' | 'bad' } {
  if (volume.protection === 'On') {
    return { text: 'on', tone: 'ok' };
  }
  if (volume.protection === 'Unknown') {
    return { text: 'unknown (locked?)', tone: 'warn' };
  }
  switch (volume.conversion) {
    case 'Encrypted':
      return { text: 'suspended', tone: 'warn' };
    case 'Encrypting':
    case 'EncryptionPaused':
      return { text: 'still encrypting', tone: 'warn' };
    default:
      return { text: 'off', tone: 'bad' };
  }
}

/**
 * The System health row for BitLocker on the server's fixed disks (SRS SEC-05): "On for C: and D:", or each disk's
 * state when one is not protected, with the worst tone among them.
 */
export function encryptionSummary(report: DiskEncryptionReport): { text: string; tone: 'ok' | 'warn' | 'bad' } {
  if (!report.available) {
    return { text: 'BitLocker is not installed', tone: 'bad' };
  }
  if (report.volumes.length === 0) {
    return { text: 'No fixed disks found', tone: 'warn' };
  }
  const states = report.volumes.map((volume) => ({ drive: volume.drive, ...volumeState(volume) }));
  if (states.every((state) => state.tone === 'ok')) {
    return { text: `On for ${states.map((state) => state.drive).join(', ')}`, tone: 'ok' };
  }
  return {
    text: states.map((state) => `${state.drive} ${state.text}`).join(', '),
    tone: states.some((state) => state.tone === 'bad') ? 'bad' : 'warn',
  };
}
