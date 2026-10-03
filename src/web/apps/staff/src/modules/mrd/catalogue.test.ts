import { describe, expect, it } from 'vitest';
import type { CatalogueImportResult } from './api';
import {
  catalogueName,
  checkCatalogueFile,
  formatSize,
  importSummary,
  MAX_CATALOGUE_BYTES,
  sourceLabel,
  usageMark,
  withdrawnNote,
} from './catalogue';

function result(partial: Partial<CatalogueImportResult>): CatalogueImportResult {
  return {
    id: 'i1',
    catalogue: 'Icd10',
    release: 'ICD-10 2019',
    fileName: 'icd102019en.xml',
    sha256: 'abc',
    fileSizeBytes: 1000,
    sourceTitle: null,
    sourceVersion: null,
    codesInFile: 12345,
    added: 0,
    updated: 0,
    unchanged: 12345,
    reinstated: 0,
    withdrawn: 0,
    leftAlone: 0,
    chapters: 22,
    blocks: 1,
    dryRun: false,
    at: '2026-10-03T10:00:00Z',
    importedBy: 'MRD',
    channel: 'Upload',
    durationMs: 0,
    warnings: [],
    ...partial,
  };
}

describe('checkCatalogueFile', () => {
  it('accepts the WHO ClaML file and the CMS order file', () => {
    expect(checkCatalogueFile({ name: 'icd102019en.XML', size: 10 })).toBeNull();
    expect(checkCatalogueFile({ name: 'icd10pcs_order_2026.txt', size: 10 })).toBeNull();
    expect(checkCatalogueFile({ name: 'icd10.zip', size: 10 })).toBeNull();
  });

  it('refuses empty, oversized and other files', () => {
    expect(checkCatalogueFile({ name: 'a.xml', size: 0 })).toBe('The file is empty.');
    expect(checkCatalogueFile({ name: 'a.xml', size: MAX_CATALOGUE_BYTES + 1 })).toMatch(/larger than 100 MB/);
    expect(checkCatalogueFile({ name: 'codes.csv', size: 10 })).toMatch(/Choose the WHO ClaML file/);
  });
});

describe('importSummary', () => {
  it('counts the file, chapters and blocks, and says nothing changed', () => {
    expect(importSummary(result({ blocks: 1 }))).toEqual([
      '12,345 codes in the file, in 22 chapters and 1 block.',
      'Nothing changed: the catalogue already matched the file.',
    ]);
  });

  it('describes a dry run in the conditional', () => {
    const lines = importSummary(
      result({ catalogue: 'Icd10Pcs', dryRun: true, added: 2, withdrawn: 1, unchanged: 12342, leftAlone: 1 }),
    );
    expect(lines).toEqual([
      '12,345 codes in the file.',
      '2 would be added, 1 would be withdrawn; 12,342 already up to date.',
      '1 code added or changed by hand, or saved from the WHO online search, would be left as it is.',
    ]);
  });
});

describe('withdrawnNote', () => {
  it('is null when nothing is withdrawn', () => {
    expect(withdrawnNote(0, false)).toBeNull();
  });

  it('says recorded diagnoses keep their codes', () => {
    expect(withdrawnNote(1, true)).toBe(
      '1 code is not in this file. After the import it will no longer be offered for new diagnoses or coding. Diagnoses already recorded keep it.',
    );
    expect(withdrawnNote(3, false)).toBe(
      '3 codes are not in this file. They will no longer be offered for new diagnoses or coding. Diagnoses already recorded keep them.',
    );
  });
});

describe('labels', () => {
  it('names the catalogues and sources', () => {
    expect(catalogueName('ICD-10-PCS')).toBe('ICD-10-PCS procedures');
    expect(catalogueName('Local')).toBe('Local');
    expect(sourceLabel('Import')).toBe('Imported file');
    expect(sourceLabel('Seed')).toBe('Starter list');
    expect(sourceLabel('Manual')).toBe('Added or edited here');
  });

  it('marks dagger and asterisk codes', () => {
    expect(usageMark('Dagger')).toBe('†');
    expect(usageMark('Asterisk')).toBe('*');
    expect(usageMark(null)).toBe('');
  });

  it('formats file sizes', () => {
    expect(formatSize(4.2 * 1024 * 1024)).toBe('4.2 MB');
    expect(formatSize(100)).toBe('1 KB');
  });
});
