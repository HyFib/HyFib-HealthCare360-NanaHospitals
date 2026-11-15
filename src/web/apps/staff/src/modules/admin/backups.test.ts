import { describe, expect, it } from 'vitest';
import {
  backupContents,
  backupKeyText,
  copyDisksText,
  custodyAdvice,
  damagedCopyText,
  offsiteText,
  restoreTestText,
} from './api';

describe('backupContents', () => {
  it('describes a full backup with its documents', () => {
    expect(backupContents({ status: 'Succeeded', formatVersion: 2, documentCount: 1 })).toBe(
      'Database, key and 1 document',
    );
    expect(backupContents({ status: 'Succeeded', formatVersion: 2, documentCount: 12500 })).toBe(
      'Database, key and 12,500 documents',
    );
  });

  it('describes the backup taken before an update, which has no documents', () => {
    expect(backupContents({ status: 'Succeeded', formatVersion: 2, documentCount: null })).toBe(
      'Database and key (before an update)',
    );
  });

  it('says an older backup holds the database only', () => {
    expect(backupContents({ status: 'Succeeded', formatVersion: 1, documentCount: null })).toBe(
      'Database only (older backup)',
    );
  });

  it('shows nothing for a backup that did not finish', () => {
    expect(backupContents({ status: 'Failed', formatVersion: 2, documentCount: null })).toBe('—');
    expect(backupContents({ status: 'Running', formatVersion: 1, documentCount: null })).toBe('—');
  });
});

describe('backupKeyText', () => {
  it('names the original key for backups made before backups named their key, and those made with it', () => {
    expect(backupKeyText(null, '3F2A9C1B0D4E5F60')).toBe('Opens with the original recovery key');
    expect(backupKeyText('3F2A9C1B0D4E5F60', '3F2A9C1B0D4E5F60')).toBe('Opens with the original recovery key');
  });

  it('gives the id of any other key', () => {
    expect(backupKeyText('7B1E0A44C2D9F3A8', '3F2A9C1B0D4E5F60')).toBe('Opens with recovery key 7B1E0A44C2D9F3A8');
    expect(backupKeyText('7B1E0A44C2D9F3A8', undefined)).toBe('Opens with recovery key 7B1E0A44C2D9F3A8');
  });
});

describe('offsiteText', () => {
  const run = (patch: Partial<Parameters<typeof offsiteText>[0]> = {}) =>
    offsiteText({
      status: 'Succeeded',
      kind: 'Scheduled',
      offsiteUploadedAt: null,
      offsiteAttempts: 0,
      offsiteError: null,
      ...patch,
    });

  it('says whether a full backup is off site, going up, or failing to', () => {
    expect(run({ offsiteUploadedAt: '2026-11-14T02:01:00Z', offsiteAttempts: 1 })).toBe('Yes');
    expect(run({ offsiteAttempts: 1 })).toBe('Uploading');
    expect(run({ offsiteAttempts: 3, offsiteError: 'The store could not be reached.' })).toBe(
      'Not yet: the last try failed',
    );
    expect(run()).toBe('No');
  });

  it('shows nothing for a backup that is not uploaded: one that failed, or the one before an update', () => {
    expect(run({ status: 'Failed' })).toBe('—');
    expect(run({ kind: 'PreMigration' })).toBe('—');
  });
});

describe('custodyAdvice', () => {
  const now = new Date('2026-11-14T06:00:00Z');
  const holder = (name: string, daysAgo: number) => ({
    userId: name,
    name,
    lastProvenAt: new Date(now.getTime() - daysAgo * 86_400_000).toISOString(),
  });

  it('asks for two people who hold the key, as System health does', () => {
    expect(custodyAdvice({ custodians: null }, now)).toMatch(/^Nobody has typed this key in/);
    expect(custodyAdvice({ custodians: [holder('itadmin', 1)] }, now)).toMatch(/^Only itadmin has shown/);
    expect(custodyAdvice({ custodians: [holder('director', 2), holder('itadmin', 40)] }, now)).toBeNull();
  });

  it('asks for the quarterly check once nobody has typed it in for 90 days', () => {
    expect(custodyAdvice({ custodians: [holder('director', 91), holder('itadmin', 120)] }, now)).toMatch(/for 90 days/);
    expect(custodyAdvice({ custodians: [holder('director', 89), holder('itadmin', 120)] }, now)).toBeNull();
  });
});

describe('restoreTestText', () => {
  it('says whether the backup passed its last test restore', () => {
    expect(restoreTestText({ restoreTestedAt: '2026-11-14T22:12:00Z', restoreTestPassed: true })).toBe(
      'Test restore passed',
    );
    expect(restoreTestText({ restoreTestedAt: '2026-11-14T22:12:00Z', restoreTestPassed: false })).toBe(
      'Test restore failed',
    );
  });

  it('says nothing of a backup never test-restored, as older ones were not', () => {
    expect(restoreTestText({ restoreTestedAt: null, restoreTestPassed: null })).toBeNull();
  });
});

describe('copyDisksText', () => {
  const copy = (disk: string | null, problem: string | null = null) => ({
    disk,
    copiedAt: '2026-11-13T20:10:00Z',
    checkedAt: null,
    problem,
  });

  it('names the disk each copy is on, as its label does', () => {
    expect(copyDisksText({ secondaryCopies: [copy('7F3A2C1D')] })).toBe('Disk 7F3A2C1D');
    expect(copyDisksText({ secondaryCopies: [copy('7F3A2C1D'), copy('B21E9C40')] })).toBe('Disks 7F3A2C1D, B21E9C40');
  });

  it('names no disk when none is known', () => {
    expect(copyDisksText({ secondaryCopies: [copy(null)] })).toBeNull();
    expect(copyDisksText({ secondaryCopies: [] })).toBeNull();
    expect(copyDisksText({ secondaryCopies: null })).toBeNull();
  });

  it('says which copy no longer reads back as made, and why', () => {
    expect(
      damagedCopyText({ secondaryCopies: [copy('7F3A2C1D'), copy('B21E9C40', 'It no longer matches the SHA-256.')] }),
    ).toBe('The copy on disk B21E9C40 is damaged: It no longer matches the SHA-256.');
    expect(damagedCopyText({ secondaryCopies: [copy('7F3A2C1D')] })).toBeNull();
  });
});
