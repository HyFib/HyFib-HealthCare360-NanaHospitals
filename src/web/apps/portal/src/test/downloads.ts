import { vi } from 'vitest';

export interface SavedFile {
  /** The name the browser was asked to save the file under. */
  name: string;
  blob: Blob;
}

let next = 0;

/**
 * Catches the files a page hands to the browser's downloads (jsdom has no object URLs and does not follow links).
 * Call it in `beforeEach`; `vi.restoreAllMocks()` removes the link spy. The object URL functions stay in place,
 * because a saved file's address is revoked a minute later, possibly after the test.
 */
export function catchDownloads(): SavedFile[] {
  const saved: SavedFile[] = [];
  const blobs = new Map<string, Blob>();
  Object.defineProperty(URL, 'createObjectURL', {
    configurable: true,
    writable: true,
    value: (blob: Blob) => {
      next += 1;
      const url = `blob:https://portal.test/${next}`;
      blobs.set(url, blob);
      return url;
    },
  });
  Object.defineProperty(URL, 'revokeObjectURL', {
    configurable: true,
    writable: true,
    value: (url: string) => void blobs.delete(url),
  });
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    const blob = blobs.get(this.href);
    if (blob && this.download) {
      saved.push({ name: this.download, blob });
    }
  });
  return saved;
}
