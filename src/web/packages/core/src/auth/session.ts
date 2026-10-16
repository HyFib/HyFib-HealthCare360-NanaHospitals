import { create } from 'zustand';
import { ApiError, CORRELATION_HEADER, LANGUAGE_HEADER, toApiError } from '../api/errors';
import type { ClientType } from '../api/client';

export interface FacilityOption {
  id: string;
  code: string;
  name: string;
}

/** Mirrors the server's AuthUser (GET /api/v1/auth/me). */
export interface AuthUser {
  id: string;
  userName: string;
  displayName: string;
  type: 'staff' | 'patient' | 'service';
  facilityId: string | null;
  facilityName: string | null;
  practitionerId: string | null;
  patientId: string | null;
  roles: string[];
  permissions: string[];
  facilities: FacilityOption[];
  mfaEnabled: boolean;
  hasPin: boolean;
  mustChangePassword: boolean;
  /**
   * What the session may do: change its password or set up two-step verification first, or nothing but unlock while
   * its screen is locked (the server keeps the lock; the user keeps their permissions for the screens behind it).
   */
  restriction: 'password' | 'mfa-setup' | 'locked' | null;
  /** The password must be changed because a sign-in found it published from data breaches (SRS SEC-01). */
  passwordBreached?: boolean;
}

export interface AuthResult {
  accessToken?: string | null;
  expiresAt?: string | null;
  user?: AuthUser | null;
  mfaRequired?: boolean;
  challengeId?: string | null;
  restriction?: string | null;
}

export type SessionStatus = 'unknown' | 'authenticated' | 'anonymous';

interface SessionState {
  status: SessionStatus;
  accessToken: string | null;
  expiresAt: number | null;
  user: AuthUser | null;
  /** Screen locked after inactivity (session still valid; requires PIN/password to continue). */
  locked: boolean;
  setAuth(result: AuthResult): void;
  setUser(user: AuthUser): void;
  setLocked(locked: boolean): void;
  clear(): void;
}

const DRAFT_PREFIX = 'hc360:draft:';

/** In-memory session. The refresh token lives only in an HttpOnly cookie, never in JavaScript. */
export const useSession = create<SessionState>((set) => ({
  status: 'unknown',
  accessToken: null,
  expiresAt: null,
  user: null,
  locked: false,
  // A result never unlocks the screen by itself: only unlocking does (setLocked). The server locks a session left
  // unused even when the app did not, and says so with the "locked" restriction.
  setAuth: (result) =>
    set((state) => ({
      status: result.accessToken ? 'authenticated' : state.status,
      accessToken: result.accessToken ?? state.accessToken,
      expiresAt: result.expiresAt ? Date.parse(result.expiresAt) : state.expiresAt,
      user: result.user ?? state.user,
      locked: state.locked || result.user?.restriction === 'locked',
    })),
  setUser: (user) => set({ user }),
  setLocked: (locked) => set({ locked }),
  clear: () => {
    clearDrafts();
    set({ status: 'anonymous', accessToken: null, expiresAt: null, user: null, locked: false });
  },
}));

/** True when the user holds the permission (the Super Administrator's "*" grants everything). */
export function hasPermission(
  user: AuthUser | null | undefined,
  permission: string | readonly string[] | undefined,
): boolean {
  if (!permission || permission.length === 0) {
    return true;
  }
  if (!user) {
    return false;
  }
  if (user.permissions.includes('*')) {
    return true;
  }
  return typeof permission === 'string'
    ? user.permissions.includes(permission)
    : permission.some((p) => user.permissions.includes(p));
}

let clientType: ClientType = 'web';
let refreshing: Promise<string | null> | null = null;
let refreshTimer: ReturnType<typeof setTimeout> | undefined;
let lastActivity = Date.now();

export function configureAuth(options: { clientType: ClientType }): void {
  clientType = options.clientType;
}

/**
 * Notes that someone is using the app now. Key presses, clicks, touches and scrolling count by themselves; an app calls
 * this for use the browser does not see, such as a video consultation in progress.
 */
export function markActivity(): void {
  lastActivity = Date.now();
}

// The same signs of use as the idle lock (useIdle), so the server and the screen agree on when it was last used.
if (typeof window !== 'undefined') {
  for (const name of ['mousemove', 'mousedown', 'keydown', 'touchstart', 'wheel'] as const) {
    window.addEventListener(name, markActivity, { passive: true, capture: true });
  }
}

/** Whole seconds since someone last used the app. */
export function idleSeconds(): number {
  return Math.max(0, Math.floor((Date.now() - lastActivity) / 1000));
}

async function authFetch(path: string, body?: unknown, headers?: Record<string, string>): Promise<AuthResult> {
  const response = await fetch(`/api/v1/auth/${path}`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json',
      'X-HC360-Client': clientType,
      [CORRELATION_HEADER]: crypto.randomUUID(),
      ...headers,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    throw await toApiError(response);
  }
  return response.status === 204 ? {} : ((await response.json()) as AuthResult);
}

/** What the portal adds to an OTP request: the language the patient chose and the proof of work. */
export interface OtpOptions {
  language?: string;
  headers?: Record<string, string>;
}

function otpHeaders({ language, headers }: OtpOptions): Record<string, string> | undefined {
  const all = { ...(language ? { [LANGUAGE_HEADER]: language } : {}), ...headers };
  return Object.keys(all).length > 0 ? all : undefined;
}

function accept(result: AuthResult): AuthResult {
  if (result.accessToken) {
    useSession.getState().setAuth(result);
    scheduleRefresh();
  }
  return result;
}

export const auth = {
  async login(
    userName: string,
    password: string,
    device?: { deviceId?: string; deviceSecret?: string; facilityId?: string },
  ) {
    return accept(await authFetch('login', { userName, password, ...device }));
  },

  async verifyMfa(challengeId: string, code: string) {
    return accept(await authFetch('mfa', { challengeId, code }));
  },

  /**
   * `language`: the one the patient chose in the portal, so that a refusal (too many codes…) comes in it.
   * `headers`: the portal's proof of work (`X-HC360-Proof`), which the server asks for here.
   */
  async requestOtp(phone: string, options: OtpOptions = {}) {
    return authFetch('otp/request', { phone }, otpHeaders(options)) as Promise<
      AuthResult & { challengeId: string; expiresAt: string; maskedPhone: string }
    >;
  },

  async verifyOtp(challengeId: string, code: string, options: OtpOptions = {}) {
    return accept(await authFetch('otp/verify', { challengeId, code }, otpHeaders(options)));
  },

  /** Adopts tokens returned by other auth endpoints (change password, MFA enable, facility switch). */
  apply(result: AuthResult): AuthResult {
    return accept(result);
  },

  /**
   * Single-flight refresh; network failures keep the session (WiFi blips must not sign people out). It tells the server
   * how long nobody has used the app, since refreshing alone, in the background, is no sign of anyone at the screen:
   * the server locks the screen and ends the session by that.
   */
  refresh(): Promise<string | null> {
    refreshing ??= (async () => {
      try {
        const result = await authFetch('refresh', undefined, { 'X-HC360-Idle': String(idleSeconds()) });
        accept(result);
        return result.accessToken ?? null;
      } catch (error) {
        if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
          useSession.getState().clear();
          return null;
        }
        return useSession.getState().accessToken;
      } finally {
        refreshing = null;
      }
    })();
    return refreshing;
  },

  /** Restores the session from the refresh cookie on app start. */
  async restore(): Promise<boolean> {
    const token = await auth.refresh();
    if (!token) {
      useSession.getState().clear();
    }
    return token !== null;
  },

  async logout() {
    try {
      await authFetch('logout');
    } catch {
      // Sign out locally even if the server is unreachable.
    }
    clearTimeout(refreshTimer);
    useSession.getState().clear();
  },
};

/** Refresh a minute before the access token expires so requests never stall on a 401 round-trip. */
function scheduleRefresh(): void {
  clearTimeout(refreshTimer);
  const expiresAt = useSession.getState().expiresAt;
  if (!expiresAt) {
    return;
  }
  const dueIn = Math.max(expiresAt - Date.now() - 60_000, 5_000);
  refreshTimer = setTimeout(() => void auth.refresh(), dueIn);
}

function clearDrafts(): void {
  try {
    for (let i = localStorage.length - 1; i >= 0; i--) {
      const key = localStorage.key(i);
      if (key?.startsWith(DRAFT_PREFIX)) {
        localStorage.removeItem(key);
      }
    }
  } catch {
    // Storage unavailable (private mode) — nothing to clear.
  }
}

export function draftStorageKey(userId: string, key: string): string {
  return `${DRAFT_PREFIX}${userId}:${key}`;
}
