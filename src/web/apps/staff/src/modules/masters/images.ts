import { api } from '@/lib/api';

/**
 * Images on the masters: a facility's logo (on every letterhead), and a doctor's photo (online booking) and signature
 * (prescriptions, reports, discharge summaries). The chosen file is shrunk here until it fits what documents embed, so
 * the server never has to refuse it; logos and signatures stay PNG to keep a transparent background.
 */

export type ImageKind = 'logo' | 'signature' | 'photo';

/** The largest image a document embeds (the server's DocumentAssets.MaxImageBytes); the masters refuse bigger ones. */
export const MAX_IMAGE_BYTES = 300 * 1024;

export const IMAGE_ACCEPT = 'image/png,image/jpeg,image/webp';

/** A scanner's or phone's original can be large; only the shrunk copy is uploaded. */
export const MAX_SOURCE_BYTES = 20 * 1024 * 1024;

interface ImageSpec {
  /** The file store category, so the file is recognisable later. */
  category: string;
  /** The longest side to start from, in pixels: sharp when printed at the size documents use. */
  maxSide: number;
  type: 'image/png' | 'image/jpeg';
  fileName: string;
  noun: string;
}

export const IMAGE_SPECS: Record<ImageKind, ImageSpec> = {
  logo: { category: 'logo', maxSide: 800, type: 'image/png', fileName: 'logo.png', noun: 'logo' },
  signature: { category: 'signature', maxSide: 600, type: 'image/png', fileName: 'signature.png', noun: 'signature' },
  photo: { category: 'staff-photo', maxSide: 600, type: 'image/jpeg', fileName: 'photo.jpg', noun: 'photo' },
};

/** A sentence explaining why a chosen file can't be used, or null when it looks fine. */
export function imageSourceProblem(file: Pick<File, 'name' | 'size' | 'type'>): string | null {
  if (file.size === 0) {
    return `${file.name} is empty.`;
  }
  if (!IMAGE_ACCEPT.split(',').includes(file.type)) {
    return `${file.name} is not an image HC360 can use. Choose a PNG, JPEG or WebP file.`;
  }
  if (file.size > MAX_SOURCE_BYTES) {
    return `${file.name} is larger than 20 MB. Choose a smaller image.`;
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

/** The longest sides to try, largest first, until the image fits within {@link MAX_IMAGE_BYTES}. */
export function sidesToTry(maxSide: number): number[] {
  return [...new Set([maxSide, 600, 450, 300, 200])].filter((side) => side <= maxSide);
}

function toBlob(canvas: HTMLCanvasElement, type: string): Promise<Blob | null> {
  return new Promise((resolve) => canvas.toBlob(resolve, type, 0.85));
}

/** The chosen file as the image to upload: shrunk, re-encoded (dropping any embedded details) and at most 300 KB. */
export async function prepareImage(file: Blob, kind: ImageKind): Promise<File> {
  const spec = IMAGE_SPECS[kind];
  const bitmap = await createImageBitmap(file);
  try {
    for (const side of sidesToTry(spec.maxSide)) {
      const { width, height } = fitWithin(bitmap.width, bitmap.height, side);
      const canvas = document.createElement('canvas');
      canvas.width = width;
      canvas.height = height;
      const context = canvas.getContext('2d');
      if (!context) {
        throw new Error('This computer cannot prepare images.');
      }
      if (spec.type === 'image/jpeg') {
        // JPEG has no transparency: a transparent background would otherwise turn black.
        context.fillStyle = '#ffffff';
        context.fillRect(0, 0, width, height);
      }
      context.drawImage(bitmap, 0, 0, width, height);
      const blob = await toBlob(canvas, spec.type);
      if (blob && blob.size <= MAX_IMAGE_BYTES) {
        return new File([blob], spec.fileName, { type: spec.type });
      }
    }
    throw new Error(`This ${spec.noun} can't be made small enough. Choose a simpler image, or crop it first.`);
  } finally {
    bitmap.close();
  }
}

/** Stores the prepared image and returns its file id. */
export async function uploadImage(file: File, kind: ImageKind): Promise<string> {
  const data = new FormData();
  data.append('file', file);
  data.append('category', IMAGE_SPECS[kind].category);
  const stored = await api.post<{ id: string }>('/api/v1/files', data, { timeoutMs: 60_000 });
  return stored.id;
}

/** A stored image as a data URL for an image element: the file store needs the signed-in user's token. */
export async function imageDataUrl(fileId: string): Promise<string | null> {
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
