import { api } from '@/lib/api';
import type { StoredFileDto } from './types';

/** What the encrypted file store accepts (it sniffs the content, so the extension alone is not enough). */
export const ACCEPTED_FILE_TYPES = 'application/pdf,image/png,image/jpeg,image/webp';

/**
 * What a cardiology machine exports: a PDF or images, DICOM, or SCP-ECG (the ECG machines' own format). Browsers often
 * give a DICOM file no type and never know SCP-ECG, so their extensions are listed too.
 */
export const DEVICE_FILE_TYPES = `${ACCEPTED_FILE_TYPES},application/dicom,.dcm,.scp`;
export const MAX_FILE_BYTES = 25 * 1024 * 1024;

/** A sentence explaining why the file cannot be uploaded, or null when it looks acceptable. */
export function fileProblem(file: File): string | null {
  return problemWith(file, ACCEPTED_FILE_TYPES, 'a PDF, PNG, JPEG or WebP');
}

/** As {@link fileProblem}, for a cardiology machine's output, which may also be DICOM or SCP-ECG. */
export function deviceFileProblem(file: File): string | null {
  return problemWith(file, DEVICE_FILE_TYPES, 'a PDF, image, DICOM or SCP-ECG');
}

function problemWith(file: File, accept: string, kinds: string): string | null {
  if (file.size === 0) {
    return `${file.name} is empty.`;
  }
  if (file.size > MAX_FILE_BYTES) {
    return `${file.name} is larger than 25 MB.`;
  }
  const accepted = accept.split(',');
  const dot = file.name.lastIndexOf('.');
  const extension = dot >= 0 ? file.name.slice(dot).toLowerCase() : '';
  if (file.type && !accepted.includes(file.type) && !accepted.includes(extension)) {
    return `${file.name} is not ${kinds} file.`;
  }
  return null;
}

/**
 * Uploads a document to the encrypted file store. Passing the patient links the file to the record, which the
 * diagnostics endpoints check before accepting it (a file of another patient is refused).
 */
export function uploadFile(
  file: File,
  options: { category: string; patientId?: string | null },
): Promise<StoredFileDto> {
  const data = new FormData();
  data.append('file', file);
  data.append('category', options.category);
  if (options.patientId) {
    data.append('patientId', options.patientId);
  }
  return api.post<StoredFileDto>('/api/v1/files', data, { timeoutMs: 120_000 });
}

/** Decrypted file as an object URL for the in-app viewer. Call `URL.revokeObjectURL` when the viewer closes. */
export async function openFile(fileId: string): Promise<{ url: string; contentType: string; blob: Blob }> {
  const blob = await api.blob(`/api/v1/files/${fileId}`);
  return { url: URL.createObjectURL(blob), contentType: blob.type, blob };
}
