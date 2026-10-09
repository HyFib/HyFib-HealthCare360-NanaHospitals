import { describe, expect, it } from 'vitest';
import { backupContents, backupKeyText } from './api';

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
