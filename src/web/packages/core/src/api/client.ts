import { useSession } from '../auth/session';
import {
  ApiError,
  CORRELATION_HEADER,
  featureOf,
  isFeatureDisabled,
  isSessionLocked,
  LANGUAGE_HEADER,
  toApiError,
} from './errors';
import { forgetKey, rememberedKey, rememberKey, requestFingerprint } from './rememberedKeys';

export type ClientType = 'web' | 'desktop' | 'portal';

export type QueryValue = string | number | boolean | null | undefined | readonly (string | number)[];

export interface RequestOptions {
  query?: Record<string, QueryValue>;
  signal?: AbortSignal;
  /**
   * The Idempotency-Key to send, for a screen that keeps its own while the user repeats one operation (a payment
   * dialog's "Try again"). Without it the client chooses: a new key for each write, and the same key again for the same
   * write sent again while the first one's outcome is not known (see `rememberedKeys`).
   */
  idempotencyKey?: string;
  timeoutMs?: number;
  headers?: Record<string, string>;
}

export interface ApiClientConfig {
  baseUrl?: string;
  clientType: ClientType;
  getAccessToken: () => string | null;
  /** Obtains a fresh access token (single-flight) or null when the session is over. */
  refreshAccessToken: () => Promise<string | null>;
  onSessionExpired: () => void;
  /** Reports reachability so the UI can show the WiFi/server banner. */
  onReachability?: (reachable: boolean) => void;
  /**
   * Called when the server answers with a failure of its own that names no reason: a 500, or a 502 to 504 without a
   * problem code of the server's (`isServerDecision`). Every request is answered so while the server's database is down,
   * so the app asks the server whether it is ready (`noteServerFailure`). The request still fails.
   */
  onServerFailure?: () => void;
  /**
   * Called when the server refuses a request because an administrator switched its feature off (`feature.disabled`),
   * with the feature it names, so the app can explain instead of showing an error. The request still fails.
   */
  onFeatureDisabled?: (feature: string | undefined) => void;
  /**
   * Called when the server refuses a request because the session's screen is locked (`auth.session_locked`): the
   * server locks a session left unused even when the app did not. The request still fails.
   */
  onSessionLocked?: () => void;
  /**
   * The language the user chose in the app (for example `hi`), sent as `X-HC360-Language` on every request: the server
   * writes its messages and documents for patients in it, ahead of the preference on the patient's record. Leave it
   * out (the staff app) to send none.
   */
  getLanguage?: () => string | undefined;
  /**
   * For a read the server refused because the screen is locked: resolves true once it is unlocked, when the read is
   * sent again, or false when the session is over (`whenUnlocked`). Without it, the read fails at once.
   */
  waitForUnlock?: (signal?: AbortSignal) => Promise<boolean>;
  /**
   * Who is signed in, so that the key kept for a write whose outcome is not known is sent again only with the same
   * user's same write. Defaults to the signed-in user of `useSession`.
   */
  getUserId?: () => string | null | undefined;
}

/** A downloaded file, the name the server gave it (Content-Disposition) and the response headers (notices). */
export interface DownloadedFile {
  blob: Blob;
  fileName: string | null;
  headers: Headers;
}

export interface ApiClient {
  get<T>(path: string, options?: RequestOptions): Promise<T>;
  post<T>(path: string, body?: unknown, options?: RequestOptions): Promise<T>;
  put<T>(path: string, body?: unknown, options?: RequestOptions): Promise<T>;
  patch<T>(path: string, body?: unknown, options?: RequestOptions): Promise<T>;
  delete<T>(path: string, options?: RequestOptions): Promise<T>;
  blob(path: string, options?: RequestOptions): Promise<Blob>;
  /** A file together with the name the server gave it (Content-Disposition), for "save as" downloads. */
  download(path: string, options?: RequestOptions): Promise<DownloadedFile>;
}

const RETRYABLE_STATUS = new Set([502, 503, 504]);
const MAX_ATTEMPTS = 3;

/**
 * How often a write is sent again while the server says its first attempt with the key is still running (409
 * `idempotency.in_progress`), each time after the wait the server asks for (Retry-After, 2 s): about 10 s in all.
 */
const MAX_IN_PROGRESS_WAITS = 5;

/** The problem code the server answers a retry with while the first attempt with its key still runs (IdempotencyMiddleware). */
const IN_PROGRESS = 'idempotency.in_progress';

const UNREACHABLE = 'Cannot reach the HealthCare360 server';

/** What a request that could not reach the server says, by what sending it again does. */
const UNREACHABLE_DETAIL = {
  read: 'Check the WiFi connection and try again.',
  // The same write sent again carries the same key, so the server answers with the first attempt's outcome.
  sameKey:
    'Check the WiFi connection, then try again. Saving again with the same details will not save it twice; if you change anything, first check whether it was saved.',
  // A file upload, a sign-in step, or a page without WebCrypto: sent again, it is a new request to the server.
  newKey: 'Check the WiFi connection. It may have been saved: check before you try again.',
} as const;

/** What a write still running after the waits says when sending it again would not carry the same key. */
const STILL_RUNNING_NEW_KEY =
  'An earlier attempt of this request has not finished yet, so it may still be saved: check before you try again.';

/**
 * Fetch wrapper tuned for the hospital WiFi (SRS DED-08/09):
 * - every unsafe request carries an Idempotency-Key, so automatic retries after a dropped response never
 *   duplicate a bill, payment or order;
 * - a JSON write whose outcome is not known (a network failure or a timeout after the retries, or the server still
 *   working on it) keeps its key: the same write sent again by the same user, with the same details, carries it again,
 *   so the server answers with the first attempt's outcome instead of saving twice. A definitive answer, and signing
 *   out, forget it; a refusal given before the server's store was asked (a locked screen, too many requests) does not.
 *   The caller's own key wins, and a form (a file upload) gets a new key each time;
 * - while the server says the first attempt is still running (409 `idempotency.in_progress`), the write waits as asked
 *   (Retry-After) and is sent again, a few times;
 * - transient failures (network, 502/503/504) are retried with exponential backoff + jitter, and so is an answer lost
 *   while it arrived: the server kept it under the key;
 * - a server failure that names no reason is reported (`onServerFailure`): an answer counts as the server being
 *   reachable, but while its database is down every answer is such a failure;
 * - a 401 triggers one token refresh and replay; a 423 (the screen is locked) does not: a read waits until the screen
 *   is unlocked and is then sent again, so the screens behind the lock keep what they show (SRS UX-11), and a write
 *   fails, to be made again once unlocked;
 * - every request carries an `X-Correlation-Id`, the same on each retry, which the server logs and echoes, so the
 *   reference a user quotes finds the request in the server's logs (SRS OBS-04);
 * - Problem Details become typed {@link ApiError}s with field errors for forms.
 */
export function createApiClient(config: ApiClientConfig): ApiClient {
  const userId = config.getUserId ?? (() => useSession.getState().user?.id);

  async function send<T>(
    method: string,
    path: string,
    body: unknown,
    options: RequestOptions,
    read: (response: Response) => Promise<T>,
  ): Promise<T> {
    const unsafe = method !== 'GET' && method !== 'HEAD';
    const isAuthCall = path.startsWith('/api/v1/auth/');
    const url = buildUrl(config.baseUrl ?? '', path, options.query);
    const form = body instanceof FormData ? body : undefined;
    const json = form || body === undefined ? undefined : JSON.stringify(body);
    const correlationId = crypto.randomUUID();

    // The write's key, and the fingerprint it is remembered under while its outcome is not known.
    let idempotencyKey: string | undefined;
    let fingerprint: string | null = null;
    if (unsafe && !isAuthCall) {
      if (options.idempotencyKey) {
        idempotencyKey = options.idempotencyKey;
      } else if (form) {
        idempotencyKey = crypto.randomUUID();
      } else {
        fingerprint = await requestFingerprint(userId() ?? '', method, url, json ?? '');
        idempotencyKey = (fingerprint ? rememberedKey(fingerprint) : undefined) ?? crypto.randomUUID();
      }
    }
    // Sent again, does the write carry the same key? Then sending it again is safe.
    const sameKeyAgain = fingerprint !== null || options.idempotencyKey !== undefined;

    /** The write's outcome is not known: its key is kept for when the same write is sent again. */
    const outcomeUnknown = () => {
      if (fingerprint && idempotencyKey) {
        rememberKey(fingerprint, idempotencyKey);
      }
    };
    /** The write got a definitive answer: the next such write is a new one. */
    const settled = () => {
      if (fingerprint) {
        forgetKey(fingerprint);
      }
    };
    const unreachable = () => {
      outcomeUnknown();
      return new ApiError({
        status: 0,
        title: UNREACHABLE,
        detail: !unsafe
          ? UNREACHABLE_DETAIL.read
          : sameKeyAgain
            ? UNREACHABLE_DETAIL.sameKey
            : UNREACHABLE_DETAIL.newKey,
        code: 'network',
      });
    };

    let refreshed = false;
    let waitedForUnlock = false;
    let inProgressWaits = 0;
    // The transport attempt: only a network failure, a gateway failure or a lost answer uses one up.
    let attempt = 1;

    for (;;) {
      const headers = new Headers(options.headers);
      headers.set('Accept', 'application/json');
      headers.set('X-HC360-Client', config.clientType);
      const language = config.getLanguage?.();
      if (language) {
        headers.set(LANGUAGE_HEADER, language);
      }
      if (!headers.has(CORRELATION_HEADER)) {
        headers.set(CORRELATION_HEADER, correlationId);
      }
      const token = config.getAccessToken();
      if (token) {
        headers.set('Authorization', `Bearer ${token}`);
      }
      if (idempotencyKey) {
        headers.set('Idempotency-Key', idempotencyKey);
      }

      let payload: BodyInit | undefined;
      if (form) {
        payload = form;
      } else if (json !== undefined) {
        headers.set('Content-Type', 'application/json');
        payload = json;
      }

      const timeout = AbortSignal.timeout(options.timeoutMs ?? 30_000);
      const signal = options.signal ? AbortSignal.any([options.signal, timeout]) : timeout;
      const retryable = attempt < MAX_ATTEMPTS && (!unsafe || idempotencyKey !== undefined);

      let response: Response;
      try {
        response = await fetch(url, { method, headers, body: payload, signal, credentials: 'same-origin' });
      } catch (error) {
        if (options.signal?.aborted) {
          throw error;
        }
        config.onReachability?.(false);
        if (retryable) {
          await delay(backoff(attempt++));
          continue;
        }
        throw unreachable();
      }

      config.onReachability?.(true);

      if (response.status === 401 && !refreshed && !isAuthCall) {
        refreshed = true;
        if (await config.refreshAccessToken()) {
          continue;
        }
        config.onSessionExpired();
        settled();
        throw await toApiError(response);
      }

      // A failure that names no reason: the server's database may be down, or (502 to 504) a gateway that gave no answer
      // of the server's own may have passed the request on.
      const unexplainedFailure = response.status >= 500 && !(await isServerDecision(response));
      if (unexplainedFailure) {
        config.onServerFailure?.();
      }
      const gatewayFailure = unexplainedFailure && RETRYABLE_STATUS.has(response.status);
      if (gatewayFailure && retryable) {
        await delay(backoff(attempt++, response.headers.get('Retry-After')));
        continue;
      }

      if (
        response.status === 409 &&
        idempotencyKey !== undefined &&
        inProgressWaits < MAX_IN_PROGRESS_WAITS &&
        (await problemCode(response)) === IN_PROGRESS
      ) {
        // The first attempt with this key is still running: wait as the server asks, then ask again for its outcome.
        inProgressWaits++;
        await delay(backoff(inProgressWaits, response.headers.get('Retry-After')));
        continue;
      }

      if (!response.ok) {
        let error = await toApiError(response);
        if (gatewayFailure || error.code === IN_PROGRESS) {
          // Still running after the waits, or lost beyond the server: the write may yet be saved.
          outcomeUnknown();
          if (error.code === IN_PROGRESS && !sameKeyAgain) {
            error = new ApiError({
              status: error.status,
              title: error.title,
              detail: STILL_RUNNING_NEW_KEY,
              code: error.code,
              referenceId: error.referenceId,
            });
          }
        } else if (!refusedBeforeTheStore(error)) {
          // Any other answer is definitive. One about an earlier attempt that did not finish (409
          // `idempotency.outcome_unknown`) too: the user checks, and what they send next is a new write.
          settled();
        }
        if (isFeatureDisabled(error)) {
          config.onFeatureDisabled?.(featureOf(error));
        }
        if (isSessionLocked(error)) {
          config.onSessionLocked?.();
          if (!unsafe && !waitedForUnlock && config.waitForUnlock && (await config.waitForUnlock(options.signal))) {
            waitedForUnlock = true;
            continue;
          }
        }
        throw error;
      }

      let result: T;
      try {
        result = await read(response);
      } catch (error) {
        if (options.signal?.aborted || error instanceof SyntaxError) {
          // The server answered: the write is done, whatever became of reading its answer.
          settled();
          throw error;
        }
        // The answer was lost while it arrived (the connection dropped): the server kept it under the key, so it is
        // asked for again.
        if (retryable) {
          await delay(backoff(attempt++));
          continue;
        }
        throw unreachable();
      }
      settled();
      return result;
    }
  }

  function request<T>(method: string, path: string, body?: unknown, options: RequestOptions = {}): Promise<T> {
    return send(method, path, body, options, async (response) => {
      if (response.status === 204) {
        return undefined as T;
      }
      const type = response.headers.get('content-type') ?? '';
      return (type.includes('json') ? await response.json() : await response.text()) as T;
    });
  }

  return {
    get: (path, options) => request('GET', path, undefined, options),
    post: (path, body, options) => request('POST', path, body, options),
    put: (path, body, options) => request('PUT', path, body, options),
    patch: (path, body, options) => request('PATCH', path, body, options),
    delete: (path, options) => request('DELETE', path, undefined, options),
    blob: (path, options = {}) => send('GET', path, undefined, options, (response) => response.blob()),
    download: (path, options = {}) =>
      send('GET', path, undefined, options, async (response) => ({
        blob: await response.blob(),
        fileName: fileNameFromDisposition(response.headers.get('content-disposition')),
        headers: response.headers,
      })),
  };
}

/** The file name in a Content-Disposition header: the UTF-8 `filename*` form first, then plain `filename`. */
export function fileNameFromDisposition(header: string | null): string | null {
  if (!header) {
    return null;
  }
  const encoded = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(header);
  if (encoded?.[1]) {
    try {
      return decodeURIComponent(encoded[1].trim());
    } catch {
      // Malformed percent-encoding: fall back to the plain name.
    }
  }
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(header);
  return plain?.[1]?.trim() || null;
}

export function buildUrl(base: string, path: string, query?: Record<string, QueryValue>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query ?? {})) {
    if (value === undefined || value === null || value === '') {
      continue;
    }
    if (Array.isArray(value)) {
      value.forEach((v) => params.append(key, String(v)));
    } else {
      params.set(key, String(value));
    }
  }
  const qs = params.toString();
  return `${base}${path}${qs ? `?${qs}` : ''}`;
}

/** The problem code of an answer in Problem Details, read from a copy so that the answer itself can still be read. */
async function problemCode(response: Response): Promise<string | undefined> {
  if (!(response.headers.get('content-type') ?? '').includes('json')) {
    return undefined;
  }
  try {
    const problem = (await response.clone().json()) as { code?: unknown };
    return typeof problem.code === 'string' && problem.code.length > 0 ? problem.code : undefined;
  } catch {
    return undefined;
  }
}

/**
 * An answer given before the server's idempotency store was asked: the screen is locked (423), too many requests (429),
 * or the feature is switched off. It says nothing about an earlier attempt with the key, whose outcome stays as unknown
 * as it was, so the key kept for it is kept: sent again once unlocked, the write still carries it.
 */
function refusedBeforeTheStore(error: ApiError): boolean {
  return error.status === 429 || isSessionLocked(error) || isFeatureDisabled(error);
}

/**
 * A 5xx from HC360 itself that names a problem code (for example `ai.provider_failed`, `ai.disabled`, or
 * `feature.disabled` for a feature an administrator switched off) is the server's answer, not a dropped connection:
 * retrying would only repeat it (and spend quota). Proxy and gateway failures carry no such problem body and are still
 * retried.
 */
async function isServerDecision(response: Response): Promise<boolean> {
  if (!(response.headers.get('content-type') ?? '').includes('application/problem+json')) {
    return false;
  }
  return (await problemCode(response)) !== undefined;
}

function backoff(attempt: number, retryAfter?: string | null): number {
  const retrySeconds = retryAfter ? Number(retryAfter) : Number.NaN;
  if (Number.isFinite(retrySeconds)) {
    return Math.min(retrySeconds * 1000, 10_000);
  }
  return 400 * 2 ** (attempt - 1) + Math.random() * 300;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
