import { api } from '@/lib/api';

export interface StoredFile {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  category: string;
  patientId: string | null;
  createdAt: string;
}

/** Largest attachment accepted from the screens (the server allows more; certificates and letters are small). */
export const MAX_UPLOAD_BYTES = 10 * 1024 * 1024;
export const DOCUMENT_ACCEPT = 'application/pdf,image/png,image/jpeg,image/webp';

/** Uploads a document (encrypted at rest by the server) and returns its stored-file record. */
export async function uploadFile(file: File, category: string): Promise<StoredFile> {
  const data = new FormData();
  data.append('file', file);
  data.append('category', category);
  return api.post<StoredFile>('/api/v1/files', data, { timeoutMs: 120_000 });
}

/** Saves a blob through the browser's download (works in HC360 Desktop and on tablets). */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.rel = 'noopener';
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  // Give the download a moment to start before releasing the memory.
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}

/** Downloads a stored document (a certificate, a medical note) under its original name. */
export async function downloadStoredFile(fileId: string): Promise<void> {
  const [meta, blob] = await Promise.all([
    api.get<StoredFile>(`/api/v1/files/${fileId}/meta`),
    api.blob(`/api/v1/files/${fileId}`, { query: { download: true } }),
  ]);
  saveBlob(blob, meta.fileName);
}

/** Reads a small text file chosen by the user (CSV imports). */
export function readTextFile(file: File): Promise<string> {
  return file.text();
}

/** Plain CSV text download (built on the workstation, e.g. an attendance summary). */
export function saveCsv(rows: (string | number | null | undefined)[][], fileName: string): void {
  const escape = (value: string | number | null | undefined) => {
    let text = value === null || value === undefined ? '' : String(value);
    // Spreadsheet formula injection guard: never let a cell start with = + - @ (numbers stay numbers).
    if (typeof value === 'string' && /^[=+\-@\t\r]/.test(text)) {
      text = `'${text}`;
    }
    return /[",\n\r]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  };
  const csv = rows.map((row) => row.map(escape).join(',')).join('\r\n');
  saveBlob(new Blob([`﻿${csv}`], { type: 'text/csv;charset=utf-8' }), fileName);
}
