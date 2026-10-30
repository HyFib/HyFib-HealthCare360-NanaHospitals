import type { RecordRequestDto } from './api';

/**
 * Copies of the record issued as documents (BSA 2023, s.63): each file's SHA-256 is what its certificate states, and a
 * file brought back is checked against the copies issued by that value alone, worked out here on this computer, so the
 * file itself is never sent anywhere.
 */

/**
 * A copy for the patient (at the desk or from the portal) or for a legal authority: due within 72 hours, and kept by
 * whoever asked, so it has no return date. The server's `RecordRequest.IsCopy`.
 */
export function isCopyRequest(r: Pick<RecordRequestDto, 'isPatientRequest' | 'purpose'>): boolean {
  return r.isPatientRequest || r.purpose === 'PatientCopy' || r.purpose === 'Legal';
}

/**
 * What issuing a request sends: the chart lent out comes back on the day chosen; a copy, and a request issued as
 * documents, are kept by whoever asked, so they go with no return date.
 */
export function issueChoice(
  request: Pick<RecordRequestDto, 'isPatientRequest' | 'purpose'>,
  documents: { kind: string; id: string }[],
  dueBack: string,
): { dueBackOn: string | null; documents: { kind: string; id: string }[] } {
  const lent = !isCopyRequest(request) && documents.length === 0;
  return { dueBackOn: lent ? dueBack || null : null, documents };
}

/** The SHA-256 of a file's bytes, in upper-case hex (as the server keeps it and Windows' Get-FileHash prints it). */
export async function sha256Hex(file: Blob): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer());
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0'))
    .join('')
    .toUpperCase();
}

/**
 * A SHA-256 as people copy it from a certificate or a tool (either case, with spaces or colons between the groups), in
 * the form it is kept; null when it is not one. The server's `RecordCopyFile.NormalizeSha256`.
 */
export function normalizeSha256(value: string): string | null {
  const hex = value.replace(/[\s:]/g, '').toUpperCase();
  return /^[0-9A-F]{64}$/.test(hex) ? hex : null;
}

/** "1.2 MB", "84 KB", "512 bytes". */
export function fileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} bytes`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
