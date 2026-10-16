import { ApiError, CORRELATION_HEADER, featureOf, isFeatureDisabled, isSessionLocked, toApiError } from './errors';

export type ClientType = 'web' | 'desktop' | 'portal';

export type QueryValue = string | number | boolean | null | undefined | readonly (string | number)[];

export interface RequestOptions {
  query?: Record<string, QueryValue>;
  signal?: AbortSignal;
  /** Reuse a key when the *user* repeats the same operation (e.g. "Try again" after a timeout). */
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
   * The language the user reads the app in (for example `hi`), sent as Accept-Language so that the server writes its
   * messages and documents for patients in it when it knows no preference of theirs. Leave it out to send the
   * browser's own.
   */
  getLanguage?: () => string | undefined;
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
 * Fetch wrapper tuned for the hospital WiFi (SRS DED-08/09):
 * - every unsafe request carries an Idempotency-Key, so automatic retries after a dropped response never
 *   duplicate a bill, payment or order;
 * - transient failures (network, 502/503/504) are retried with exponential backoff + jitter;
 * - a 401 triggers one token refresh and replay; a 423 (the screen is locked) does not, it waits for the PIN;
 * - every request carries an `X-Correlation-Id`, the same on each retry, which the server logs and echoes, so the
 *   reference a user quotes finds the request in the server's logs (SRS OBS-04);
 * - Problem Details become typed {@link ApiError}s with field errors for forms.
 */
export function createApiClient(config: ApiClientConfig): ApiClient {
  async function send(method: string, path: string, body: unknown, options: RequestOptions = {}): Promise<Response> {
    const unsafe = method !== 'GET' && method !== 'HEAD';
    const isAuthCall = path.startsWith('/api/v1/auth/');
    const idempotencyKey = unsafe && !isAuthCall ? (options.idempotencyKey ?? crypto.randomUUID()) : undefined;
    const url = buildUrl(config.baseUrl ?? '', path, options.query);
    const correlationId = crypto.randomUUID();
    let refreshed = false;

    for (let attempt = 1; ; attempt++) {
      const headers = new Headers(options.headers);
      headers.set('Accept', 'application/json');
      headers.set('X-HC360-Client', config.clientType);
      const language = config.getLanguage?.();
      if (language) {
        headers.set('Accept-Language', language);
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
      if (body instanceof FormData) {
        payload = body;
      } else if (body !== undefined) {
        headers.set('Content-Type', 'application/json');
        payload = JSON.stringify(body);
      }

      const timeout = AbortSignal.timeout(options.timeoutMs ?? 30_000);
      const signal = options.signal ? AbortSignal.any([options.signal, timeout]) : timeout;

      let response: Response;
      try {
        response = await fetch(url, { method, headers, body: payload, signal, credentials: 'same-origin' });
      } catch (error) {
        if (options.signal?.aborted) {
          throw error;
        }
        config.onReachability?.(false);
        if (attempt < MAX_ATTEMPTS && (!unsafe || idempotencyKey)) {
          await delay(backoff(attempt));
          continue;
        }
        throw new ApiError({
          status: 0,
          title: 'Cannot reach the HealthCare360 server',
          detail: 'Check the WiFi connection and try again. It is safe to retry — nothing will be duplicated.',
          code: 'network',
        });
      }

      config.onReachability?.(true);

      if (response.status === 401 && !refreshed && !isAuthCall) {
        refreshed = true;
        if (await config.refreshAccessToken()) {
          continue;
        }
        config.onSessionExpired();
        throw await toApiError(response);
      }

      if (
        RETRYABLE_STATUS.has(response.status) &&
        attempt < MAX_ATTEMPTS &&
        (!unsafe || idempotencyKey) &&
        !(await isServerDecision(response))
      ) {
        await delay(backoff(attempt, response.headers.get('Retry-After')));
        continue;
      }

      if (!response.ok) {
        const error = await toApiError(response);
        if (isFeatureDisabled(error)) {
          config.onFeatureDisabled?.(featureOf(error));
        }
        if (isSessionLocked(error)) {
          config.onSessionLocked?.();
        }
        throw error;
      }

      return response;
    }
  }

  async function request<T>(method: string, path: string, body?: unknown, options?: RequestOptions): Promise<T> {
    const response = await send(method, path, body, options);
    if (response.status === 204) {
      return undefined as T;
    }
    const type = response.headers.get('content-type') ?? '';
    return (type.includes('json') ? await response.json() : await response.text()) as T;
  }

  return {
    get: (path, options) => request('GET', path, undefined, options),
    post: (path, body, options) => request('POST', path, body, options),
    put: (path, body, options) => request('PUT', path, body, options),
    patch: (path, body, options) => request('PATCH', path, body, options),
    delete: (path, options) => request('DELETE', path, undefined, options),
    blob: async (path, options) => (await send('GET', path, undefined, options)).blob(),
    download: async (path, options) => {
      const response = await send('GET', path, undefined, options);
      return {
        blob: await response.blob(),
        fileName: fileNameFromDisposition(response.headers.get('content-disposition')),
        headers: response.headers,
      };
    },
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
  try {
    const problem = (await response.clone().json()) as { code?: unknown };
    return typeof problem.code === 'string' && problem.code.length > 0;
  } catch {
    return false;
  }
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
