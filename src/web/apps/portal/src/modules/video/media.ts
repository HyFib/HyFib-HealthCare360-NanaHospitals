/**
 * Camera, microphone and browser checks for video consultations (pure functions, unit-tested). Patients join from
 * all kinds of phones, so every failure maps to one clear, fixable instruction.
 */

export type MediaProblem = 'denied' | 'notFound' | 'inUse' | 'insecure' | 'unsupported' | 'unknown';

/** What went wrong when asking for the camera/microphone (DOMException names differ a little between browsers). */
export function classifyMediaError(error: unknown): MediaProblem {
  const name = (error as { name?: unknown } | null)?.name;
  switch (name) {
    case 'NotAllowedError':
    case 'PermissionDeniedError':
    case 'SecurityError':
      return 'denied';
    case 'NotFoundError':
    case 'DevicesNotFoundError':
    case 'OverconstrainedError':
    case 'ConstraintNotSatisfiedError':
      return 'notFound';
    case 'NotReadableError':
    case 'TrackStartError':
    case 'AbortError':
      return 'inUse';
    default:
      return 'unknown';
  }
}

/** Problems worth trying again without the camera (the microphone may still work). */
export function canFallBackToAudio(problem: MediaProblem): boolean {
  return problem === 'notFound' || problem === 'inUse' || problem === 'unknown';
}

export type DevicePlatform = 'android' | 'ios' | 'desktop';

export function detectPlatform(userAgent: string, maxTouchPoints = 0): DevicePlatform {
  if (/android/i.test(userAgent)) {
    return 'android';
  }
  // iPadOS reports itself as a Mac with a touch screen.
  if (/iphone|ipad|ipod/i.test(userAgent) || (/macintosh/i.test(userAgent) && maxTouchPoints > 1)) {
    return 'ios';
  }
  return 'desktop';
}

/**
 * Links opened inside Facebook, Instagram, LinkedIn and similar apps load in their built-in browsers, which often
 * cannot use the camera. (WhatsApp and SMS open the phone's real browser.)
 */
export function isInAppBrowser(userAgent: string): boolean {
  return /FBAN|FBAV|FB_IAB|Instagram|LinkedInApp|Snapchat|Line\/|MicroMessenger|; wv\)|GSA\//i.test(userAgent);
}

export interface BrowserCapabilities {
  isSecureContext: boolean;
  hasGetUserMedia: boolean;
  hasPeerConnection: boolean;
}

/** Whether this page can make a video call at all, before asking for any permission. */
export function mediaSupport(capabilities: BrowserCapabilities): 'ok' | 'insecure' | 'unsupported' {
  if (!capabilities.isSecureContext) {
    return 'insecure';
  }
  return capabilities.hasGetUserMedia && capabilities.hasPeerConnection ? 'ok' : 'unsupported';
}

export function currentCapabilities(): BrowserCapabilities {
  return {
    isSecureContext: typeof window !== 'undefined' && window.isSecureContext,
    hasGetUserMedia: typeof navigator !== 'undefined' && typeof navigator.mediaDevices?.getUserMedia === 'function',
    hasPeerConnection: typeof window !== 'undefined' && typeof window.RTCPeerConnection === 'function',
  };
}

export type JoinProblem = 'tooEarly' | 'closed' | 'cancelled' | 'missed' | 'linkInvalid' | 'linkExpired' | 'notLinked' | 'signIn' | 'tooMany' | 'other';

/**
 * The hub answers a refused join with a sentence (HubException); this maps the known ones to the patient's language.
 * Sources: VirtualCare/Hubs/TelehealthHub.cs and Domain/TelehealthSession.cs.
 */
export function classifyJoinError(message: string): JoinProblem {
  const text = message.toLowerCase();
  if (text.includes('minutes before the appointment time')) {
    return 'tooEarly';
  }
  if (text.includes('was cancelled')) {
    return 'cancelled';
  }
  if (text.includes('was missed')) {
    return 'missed';
  }
  if (text.includes('has ended') || text.includes('is closed')) {
    return 'closed';
  }
  if (text.includes('link has expired')) {
    return 'linkExpired';
  }
  if (text.includes('link is not valid')) {
    return 'linkInvalid';
  }
  if (text.includes('not linked to your mobile')) {
    return 'notLinked';
  }
  if (text.includes('sign in')) {
    return 'signIn';
  }
  if (text.includes('too many attempts')) {
    return 'tooMany';
  }
  return 'other';
}
