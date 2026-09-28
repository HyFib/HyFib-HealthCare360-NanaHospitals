/** Saves a downloaded report through the browser's download (works in HC360 Desktop and on tablets). */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.rel = 'noopener';
  anchor.style.display = 'none';
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  // Give the download a moment to start before releasing the memory.
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}
