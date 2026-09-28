/**
 * The address a printed QR code opens (prescriptions and lab, radiology and cardiology reports). The server sends the
 * patient portal's public verification page (`verificationUrl`) once the hospital's public address (`portal.baseUrl`)
 * is configured, and that page opens from any phone. Until then the server-relative `verificationPath` is resolved
 * against this app's origin (the staff app is served by the same server), which works on the hospital network only.
 */
export function verificationLink(url: string | null | undefined, path: string | null | undefined, origin: string = window.location.origin): string | null {
  if (url) {
    return url;
  }
  if (!path) {
    return null;
  }
  try {
    return new URL(path, origin).href;
  } catch {
    return null;
  }
}
