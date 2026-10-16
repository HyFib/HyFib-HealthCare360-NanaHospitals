/**
 * Quick unlock of the lock screen with the device's Windows Hello, fingerprint or face (WebAuthn, SRS UX-11): what the
 * browser needs, turning the server's JSON options into what `navigator.credentials` takes and the device's answer back
 * into JSON, and remembering which of the user's keys this browser holds. The server checks every answer.
 */

/** A ceremony the server started: its options (JSON, binary values in base64url) and the id to answer it with. */
export interface WebAuthnCeremony {
  challengeId: string;
  options: Record<string, unknown>;
}

/** One of the user's devices set up to unlock the screen (`GET /api/v1/auth/webauthn/credentials`). */
export interface WebAuthnCredentialInfo {
  id: string;
  name: string;
  /** The key's id (base64url): how this browser knows it holds the key. */
  credentialId: string;
  /** The server name it was set up under: it works only where the app is opened by that name. */
  serverName: string;
  createdAt: string;
  lastUsedAt: string | null;
  usableHere: boolean;
}

type Json = Record<string, unknown>;

function fromBase64Url(text: string): ArrayBuffer {
  const base64 = text.replace(/-/g, '+').replace(/_/g, '/');
  const binary = atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='));
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes.buffer;
}

export function toBase64Url(data: ArrayBuffer | ArrayBufferView): string {
  const bytes =
    data instanceof ArrayBuffer ? new Uint8Array(data) : new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** The options without the members the server left empty (null), which the browser would refuse. */
function withoutNulls(value: Json): Json {
  return Object.fromEntries(Object.entries(value).filter(([, v]) => v !== null && v !== undefined));
}

function descriptors(list: unknown): PublicKeyCredentialDescriptor[] {
  return ((list as Json[] | undefined) ?? []).map((c) => ({
    ...(c as unknown as PublicKeyCredentialDescriptor),
    id: fromBase64Url(c.id as string),
  }));
}

/** The server's options for setting up this device, for `navigator.credentials.create({ publicKey })`. */
export function creationOptions(json: Json): PublicKeyCredentialCreationOptions {
  const options = withoutNulls(json);
  const user = options.user as Json;
  return {
    ...(options as unknown as PublicKeyCredentialCreationOptions),
    challenge: fromBase64Url(options.challenge as string),
    user: { ...(user as unknown as PublicKeyCredentialUserEntity), id: fromBase64Url(user.id as string) },
    excludeCredentials: descriptors(options.excludeCredentials),
  };
}

/** The server's options for unlocking, for `navigator.credentials.get({ publicKey })`. */
export function requestOptions(json: Json): PublicKeyCredentialRequestOptions {
  const options = withoutNulls(json);
  return {
    ...(options as unknown as PublicKeyCredentialRequestOptions),
    challenge: fromBase64Url(options.challenge as string),
    allowCredentials: descriptors(options.allowCredentials),
  };
}

/** The device's answer to setting it up, as the server reads it. */
export function attestationJson(credential: PublicKeyCredential): Json {
  const response = credential.response as AuthenticatorAttestationResponse;
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: toBase64Url(response.clientDataJSON),
      attestationObject: toBase64Url(response.attestationObject),
      transports: typeof response.getTransports === 'function' ? response.getTransports() : [],
    },
    clientExtensionResults: credential.getClientExtensionResults?.() ?? {},
  };
}

/** The device's answer to unlocking, as the server reads it. */
export function assertionJson(credential: PublicKeyCredential): Json {
  const response = credential.response as AuthenticatorAssertionResponse;
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: toBase64Url(response.clientDataJSON),
      authenticatorData: toBase64Url(response.authenticatorData),
      signature: toBase64Url(response.signature),
      userHandle: response.userHandle ? toBase64Url(response.userHandle) : undefined,
    },
    clientExtensionResults: credential.getClientExtensionResults?.() ?? {},
  };
}

/**
 * Why this page cannot use the device's Windows Hello or fingerprint, or null when it can try: WebAuthn needs a
 * browser that has it, a secure page (HTTPS, or localhost), and a server name in the address, not an IP address.
 */
export function webAuthnUnavailableReason(location: Pick<Location, 'hostname'> = window.location): string | null {
  if (typeof window === 'undefined' || typeof window.PublicKeyCredential === 'undefined' || !navigator.credentials) {
    return 'This browser cannot use Windows Hello or a fingerprint reader.';
  }
  if (!window.isSecureContext) {
    return 'Windows Hello and fingerprint unlock need the secure (https) address of HealthCare360.';
  }
  const host = location.hostname;
  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(host) || host.includes(':') || host.startsWith('[')) {
    return "Windows Hello and fingerprint unlock work when HealthCare360 is opened by the server's name (such as https://HC360-SRV:7360), not by its IP address.";
  }
  return null;
}

/** Whether the device has a built-in authenticator that verifies the person (Windows Hello, Touch ID…). */
export async function platformAuthenticatorAvailable(): Promise<boolean> {
  try {
    return (
      typeof window.PublicKeyCredential?.isUserVerifyingPlatformAuthenticatorAvailable === 'function' &&
      (await window.PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable())
    );
  } catch {
    return false;
  }
}

/** What the device's own unlock is called where the person sits: Windows Hello on Windows. */
export function deviceUnlockLabel(userAgent = typeof navigator === 'undefined' ? '' : navigator.userAgent): string {
  return /Windows/i.test(userAgent) ? 'Windows Hello' : 'fingerprint or face';
}

/** What a failed `navigator.credentials` call means for the person in front of the screen. */
export function webAuthnErrorMessage(error: unknown): string {
  const name = (error as { name?: string } | null)?.name;
  switch (name) {
    case 'NotAllowedError':
      return 'Windows Hello was cancelled or timed out. Try again, or use your PIN or password.';
    case 'InvalidStateError':
      return 'This device is already set up to unlock your screen.';
    case 'SecurityError':
      return "This address cannot use Windows Hello. Open HealthCare360 by the server's name, over https.";
    case 'NotSupportedError':
      return 'This device has no Windows Hello, fingerprint reader or face camera that HealthCare360 can use.';
    default:
      return 'Windows Hello did not answer. Try again, or use your PIN or password.';
  }
}

const KEY_PREFIX = 'hc360:unlock-keys:';

/**
 * The ids of the user's keys this browser holds, as it remembers them. A key lives in the device, and the browser
 * cannot be asked which it holds without prompting, so the app remembers the ones it set up here. They are not
 * secret: a key id unlocks nothing without the device and the person.
 */
export function rememberedUnlockKeys(userId: string): string[] {
  try {
    const raw = localStorage.getItem(KEY_PREFIX + userId);
    const ids: unknown = raw ? JSON.parse(raw) : [];
    return Array.isArray(ids) ? ids.filter((id): id is string => typeof id === 'string') : [];
  } catch {
    return [];
  }
}

function saveUnlockKeys(userId: string, ids: string[]): void {
  try {
    if (ids.length > 0) {
      localStorage.setItem(KEY_PREFIX + userId, JSON.stringify(ids));
    } else {
      localStorage.removeItem(KEY_PREFIX + userId);
    }
  } catch {
    // Storage unavailable (private mode): the screen then unlocks with the PIN or password.
  }
}

/** Remembers that this browser holds the key `credentialId` of the user. */
export function rememberUnlockKey(userId: string, credentialId: string): void {
  saveUnlockKeys(userId, [...new Set([credentialId, ...rememberedUnlockKeys(userId)])]);
}

/** Forgets the user's keys this browser held, all of them or those not in `keep` (removed, or set up elsewhere). */
export function forgetUnlockKeys(userId: string, keep: readonly string[] = []): void {
  saveUnlockKeys(
    userId,
    rememberedUnlockKeys(userId).filter((id) => keep.includes(id)),
  );
}
