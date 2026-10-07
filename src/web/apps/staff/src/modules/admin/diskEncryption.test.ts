import { describe, expect, it } from 'vitest';
import type { DiskEncryptionReport, VolumeEncryption } from './api';
import { encryptionSummary } from './diskEncryption';

function report(volumes: VolumeEncryption[], available = true): DiskEncryptionReport {
  return { checkedAt: '2026-10-07T00:45:00Z', available, volumes };
}

const on = (drive: string): VolumeEncryption => ({ drive, protection: 'On', conversion: 'Encrypted' });

describe('encryptionSummary', () => {
  it('names the disks when BitLocker protects them all', () => {
    expect(encryptionSummary(report([on('C:'), on('D:')]))).toEqual({ text: 'On for C:, D:', tone: 'ok' });
  });

  it('shows each disk when one is not protected, with the worst tone', () => {
    expect(
      encryptionSummary(report([on('C:'), { drive: 'D:', protection: 'Off', conversion: 'NotEncrypted' }])),
    ).toEqual({ text: 'C: on, D: off', tone: 'bad' });
    expect(
      encryptionSummary(
        report([
          { drive: 'C:', protection: 'Off', conversion: 'Encrypted' },
          { drive: 'D:', protection: 'Off', conversion: 'Encrypting' },
          { drive: 'E:', protection: 'Unknown', conversion: 'Unknown' },
        ]),
      ),
    ).toEqual({ text: 'C: suspended, D: still encrypting, E: unknown (locked?)', tone: 'warn' });
  });

  it('says when BitLocker is missing or found no disks', () => {
    expect(encryptionSummary(report([], false))).toEqual({ text: 'BitLocker is not installed', tone: 'bad' });
    expect(encryptionSummary(report([]))).toEqual({ text: 'No fixed disks found', tone: 'warn' });
  });
});
