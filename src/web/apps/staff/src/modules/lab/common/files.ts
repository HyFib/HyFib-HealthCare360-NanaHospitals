import { api } from '@/lib/api';
import type { StoredFileDto } from './types';

/** What the encrypted file store accepts (it sniffs the content, so the extension alone is not enough). */
export const ACCEPTED_FILE_TYPES = 'application/pdf,image/png,image/jpeg,image/webp';
export const MAX_FILE_BYTES = 25 * 1024 * 1024;

/** A sentence explaining why the file cannot be uploaded, or null when it looks acceptable. */
export function fileProblem(file: File): string | null {
  if (file.size === 0) {
    return `${file.name} is empty.`;
  }
  if (file.size > MAX_FILE_BYTES) {
    return `${file.name} is larger than 25 MB.`;
  }
  if (file.type && !ACCEPTED_FILE_TYPES.split(',').includes(file.type)) {
    return `${file.name} is not a PDF, PNG, JPEG or WebP file.`;
  }
  return null;
}

/**
 * Uploads a document to the encrypted file store. Passing the patient links the file to the record, which the
 * diagnostics endpoints check before accepting it (a file of another patient is refused).
 */
export function uploadFile(file: File, options: { category: string; patientId?: string | null }): Promise<StoredFileDto> {
  const data = new FormData();
  data.append('file', file);
  data.append('category', options.category);
  if (options.patientId) {
    data.append('patientId', options.patientId);
  }
  return api.post<StoredFileDto>('/api/v1/files', data, { timeoutMs: 120_000 });
}

/** Decrypted file as an object URL for the in-app viewer. Call `URL.revokeObjectURL` when the viewer closes. */
export async function openFile(fileId: string): Promise<{ url: string; contentType: string }> {
  const blob = await api.blob(`/api/v1/files/${fileId}`);
  return { url: URL.createObjectURL(blob), contentType: blob.type };
}

/** File as a data URL for print templates (logo, signature). Best effort: printing continues without the image. */
export async function fileAsDataUrl(fileId: string | null | undefined): Promise<string | null> {
  if (!fileId) {
    return null;
  }
  try {
    const blob = await api.blob(`/api/v1/files/${fileId}`);
    if (!blob.type.startsWith('image/')) {
      return null;
    }
    return await new Promise<string | null>((resolve) => {
      const reader = new FileReader();
      reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : null);
      reader.onerror = () => resolve(null);
      reader.readAsDataURL(blob);
    });
  } catch {
    return null;
  }
}
