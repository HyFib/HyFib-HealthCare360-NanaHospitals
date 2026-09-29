import { describe, expect, it } from 'vitest';
import { backupContents } from './api';

describe('backupContents', () => {
  it('describes a full backup with its documents', () => {
    expect(backupContents({ status: 'Succeeded', formatVersion: 2, documentCount: 1 })).toBe('Database, key and 1 document');
    expect(backupContents({ status: 'Succeeded', formatVersion: 2, documentCount: 12500 })).toBe('Database, key and 12,500 documents');
  });

  it('describes the backup taken before an update, which has no documents', () => {
    expect(backupContents({ status: 'Succeeded', formatVersion: 2, documentCount: null })).toBe('Database and key (before an update)');
  });

  it('says an older backup holds the database only', () => {
    expect(backupContents({ status: 'Succeeded', formatVersion: 1, documentCount: null })).toBe('Database only (older backup)');
  });

  it('shows nothing for a backup that did not finish', () => {
    expect(backupContents({ status: 'Failed', formatVersion: 2, documentCount: null })).toBe('—');
    expect(backupContents({ status: 'Running', formatVersion: 1, documentCount: null })).toBe('—');
  });
});
