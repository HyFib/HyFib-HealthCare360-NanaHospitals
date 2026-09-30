import { api } from '@/lib/api';

/**
 * Patient photos (SRS FO-01, photo capture). A photo is taken with the workstation's camera or picked from a file, shrunk
 * here to a small JPEG (which also drops the camera's location and other embedded details), and stored in the encrypted
 * file store in the "photo" category. The patient record then refers to it by id.
 */

/** The category the server requires of a patient's photo. */
export const PHOTO_CATEGORY = 'photo';

/** What the file store accepts and the server allows as a photo. */
export const PHOTO_ACCEPT = 'image/jpeg,image/png,image/webp';

/** A phone's original can be several MB; only the shrunk copy is uploaded. */
export const PHOTO_MAX_SOURCE_BYTES = 25 * 1024 * 1024;

/** The longest side of the stored photo, in pixels: sharp in a banner and on a printed card, and around 100 KB. */
export const PHOTO_MAX_SIDE = 800;

export const PHOTO_QUALITY = 0.85;

/** A sentence explaining why a chosen file cannot be used as a photo, or null when it looks fine. */
export function photoSourceProblem(file: Pick<File, 'name' | 'size' | 'type'>): string | null {
  if (file.size === 0) {
    return `${file.name} is empty.`;
  }
  if (!PHOTO_ACCEPT.split(',').includes(file.type)) {
    return `${file.name} is not a photo. Choose a JPEG, PNG or WebP image.`;
  }
  if (file.size > PHOTO_MAX_SOURCE_BYTES) {
    return `${file.name} is larger than 25 MB. Choose a smaller photo.`;
  }
  return null;
}

/** The size of an image once its longest side is at most `maxSide`, keeping its shape. Never enlarges. */
export function fitWithin(width: number, height: number, maxSide: number): { width: number; height: number } {
  const longest = Math.max(width, height);
  if (longest <= maxSide || longest <= 0) {
    return { width, height };
  }
  const scale = maxSide / longest;
  return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
}

/**
 * The photo to send in a save, given the patient's photo before editing and now. A save that leaves the photo alone sends
 * nothing about it (the server then keeps it); a new photo is sent; taking the photo away is asked for explicitly.
 */
export function photoChange(
  original: string | null,
  current: string | null,
): { photoFileId: string | null; removePhoto: boolean } {
  return { photoFileId: current !== original ? current : null, removePhoto: original !== null && current === null };
}

/** A sentence for the front desk when the camera cannot be started, from the browser's error. */
export function cameraProblem(error: unknown): string {
  const name =
    typeof error === 'object' && error !== null && 'name' in error ? String((error as { name: unknown }).name) : '';
  switch (name) {
    case 'NotAllowedError':
    case 'SecurityError':
      return 'The camera is blocked. Allow camera access for HealthCare360 in the browser or in Windows privacy settings, or upload a photo instead.';
    case 'NotFoundError':
    case 'OverconstrainedError':
      return 'No camera was found on this computer. Upload a photo instead.';
    case 'NotReadableError':
    case 'AbortError':
      return 'The camera is being used by another program. Close it and try again, or upload a photo instead.';
    default:
      return 'The camera could not be started. Upload a photo instead.';
  }
}

/**
 * A chosen file or a camera frame as the JPEG that is uploaded. A camera frame (an ImageBitmap) is owned from here on and
 * is closed once it has been drawn.
 */
export async function preparePhoto(source: Blob | ImageBitmap): Promise<File> {
  const bitmap = source instanceof Blob ? await createImageBitmap(source) : source;
  try {
    const { width, height } = fitWithin(bitmap.width, bitmap.height, PHOTO_MAX_SIDE);
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext('2d');
    if (!context) {
      throw new Error('This computer cannot prepare photos.');
    }
    // JPEG has no transparency: a PNG with a transparent background would otherwise turn black.
    context.fillStyle = '#ffffff';
    context.fillRect(0, 0, width, height);
    context.drawImage(bitmap, 0, 0, width, height);
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', PHOTO_QUALITY));
    if (!blob) {
      throw new Error('The photo could not be prepared.');
    }
    return new File([blob], 'patient-photo.jpg', { type: 'image/jpeg' });
  } finally {
    bitmap.close();
  }
}

/** Stores the prepared photo and returns its file id. */
export async function uploadPhoto(file: File): Promise<string> {
  const data = new FormData();
  data.append('file', file);
  data.append('category', PHOTO_CATEGORY);
  const stored = await api.post<{ id: string }>('/api/v1/files', data, { timeoutMs: 60_000 });
  return stored.id;
}

/** A stored photo as a data URL for an image element (the file store needs the signed-in user's token, so a plain link cannot be used). */
export async function photoDataUrl(fileId: string): Promise<string | null> {
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
    // No permission to read files, or the file is gone: the banner shows initials instead.
    return null;
  }
}
