import {
  ICD10_RELEASE,
  ICD10PCS_RELEASE,
  ICD11_RELEASE,
  type CatalogueImportResult,
  type CodeSource,
  type IcdUsage,
} from './api';

/** The server refuses larger files; checking first saves uploading 100 MB to be told so. */
export const MAX_CATALOGUE_BYTES = 100 * 1024 * 1024;

const ACCEPTED = ['.xml', '.zip', '.txt'];

/** What the file picker offers. */
export const CATALOGUE_FILE_TYPES = ACCEPTED.join(',');

const count = new Intl.NumberFormat('en-IN');

/** A number as staff read it in India (12,345 or 1,23,456). */
export function formatCount(value: number): string {
  return count.format(value);
}

/** The catalogue's name on screen. */
export function catalogueName(release: string): string {
  switch (release) {
    case ICD10_RELEASE:
      return 'ICD-10 (WHO 2019)';
    case ICD10PCS_RELEASE:
      return 'ICD-10-PCS procedures';
    case ICD11_RELEASE:
      return 'ICD-11 MMS, saved from the WHO online search';
    default:
      return release;
  }
}

/** Why a chosen file cannot be imported, before it is uploaded; null when it may be. */
export function checkCatalogueFile(file: { name: string; size: number }): string | null {
  if (file.size === 0) {
    return 'The file is empty.';
  }
  if (file.size > MAX_CATALOGUE_BYTES) {
    return 'The file is larger than 100 MB. Choose the WHO ClaML file or the CMS order file.';
  }
  const name = file.name.toLowerCase();
  if (!ACCEPTED.some((extension) => name.endsWith(extension))) {
    return 'Choose the WHO ClaML file (.xml, or the .zip WHO provides) or the CMS ICD-10-PCS order file (.txt or .zip).';
  }
  return null;
}

/** A file size as people say it: 4.2 MB, 830 KB. */
export function formatSize(bytes: number): string {
  if (bytes >= 1024 * 1024) {
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

function plural(value: number, one: string, many: string): string {
  return `${formatCount(value)} ${value === 1 ? one : many}`;
}

/**
 * What an import did, or would do, in two or three plain sentences: the codes in the file, then what changes, then
 * what is left alone.
 */
export function importSummary(result: CatalogueImportResult): string[] {
  const would = result.dryRun;
  const lines = [
    `${plural(result.codesInFile, 'code', 'codes')} in the file${
      result.catalogue === 'Icd10'
        ? `, in ${plural(result.chapters, 'chapter', 'chapters')} and ${plural(result.blocks, 'block', 'blocks')}`
        : ''
    }.`,
  ];
  const changes = [
    result.added > 0 && `${formatCount(result.added)} ${would ? 'would be added' : 'added'}`,
    result.updated > 0 && `${formatCount(result.updated)} ${would ? 'would be updated' : 'updated'}`,
    result.reinstated > 0 && `${formatCount(result.reinstated)} ${would ? 'would come back into use' : 'back in use'}`,
    result.withdrawn > 0 && `${formatCount(result.withdrawn)} ${would ? 'would be withdrawn' : 'withdrawn'}`,
  ].filter((part): part is string => Boolean(part));
  lines.push(
    changes.length === 0
      ? would
        ? 'Nothing would change: the catalogue already matches the file.'
        : 'Nothing changed: the catalogue already matched the file.'
      : `${changes.join(', ')}; ${formatCount(result.unchanged)} already up to date.`,
  );
  if (result.leftAlone > 0) {
    lines.push(
      `${plural(result.leftAlone, 'code', 'codes')} added or changed by hand, or saved from the WHO online search, ${
        would ? 'would be' : 'were'
      } left as ${result.leftAlone === 1 ? 'it is' : 'they are'}.`,
    );
  }
  return lines;
}

/** What withdrawing codes means for the people who use them. */
export function withdrawnNote(withdrawn: number, dryRun: boolean): string | null {
  if (withdrawn === 0) {
    return null;
  }
  const one = withdrawn === 1;
  const they = one ? 'it' : 'they';
  return `${plural(withdrawn, 'code is', 'codes are')} not in this file. ${
    dryRun ? `After the import ${they} will` : one ? 'It will' : 'They will'
  } no longer be offered for new diagnoses or coding. Diagnoses already recorded keep ${one ? 'it' : 'them'}.`;
}

/** The mark printed after a dagger or asterisk code. */
export function usageMark(usage: IcdUsage | null | undefined): string {
  return usage === 'Dagger' ? '†' : usage === 'Asterisk' ? '*' : '';
}

/** What the mark means, for its tooltip. */
export function usageHint(usage: IcdUsage | null | undefined): string | null {
  switch (usage) {
    case 'Dagger':
      return 'Dagger code: the underlying disease. Code it as the main condition.';
    case 'Asterisk':
      return 'Asterisk code: how a disease shows in an organ. Use it with the dagger code of the underlying disease, never as the primary diagnosis.';
    default:
      return null;
  }
}

/** Where a code came from, in the catalogue's list. */
export function sourceLabel(source: CodeSource): string {
  switch (source) {
    case 'Seed':
      return 'Starter list';
    case 'Import':
      return 'Imported file';
    case 'Online':
      return 'WHO online';
    default:
      return 'Added or edited here';
  }
}
