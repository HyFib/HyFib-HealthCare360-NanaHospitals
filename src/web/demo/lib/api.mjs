// A small client for the HC360 API, signed in as one member of staff, for the demo tools (story.mjs). It keeps one
// session per user name and signs in again when the access token has run out (the story moves the server's clock on,
// so that can happen between two calls); it waits and retries when the server's rate limits answer 429.

export const BASE = process.env.HC360_SERVER ?? 'https://localhost:7360';
const PASSWORD = process.env.HC360_DEMO_PASSWORD;

export class ApiError extends Error {
  constructor(method, path, status, problem) {
    super(`${method} ${path} answered ${status}: ${problem?.detail ?? problem?.title ?? JSON.stringify(problem)}`);
    this.status = status;
    this.code = problem?.code;
    this.problem = problem;
  }
}

const sessions = new Map();

/** The server's clock, from its Date header: the demo server may run on a shifted clock. */
export let serverNow = () => new Date();

async function raw(method, path, { token, body, headers = {} } = {}) {
  for (let attempt = 1; ; attempt++) {
    const response = await fetch(`${BASE}${path}`, {
      method,
      headers: {
        Accept: 'application/json',
        'X-HC360-Client': 'web',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        ...(method !== 'GET' && !path.startsWith('/api/v1/auth/') ? { 'Idempotency-Key': crypto.randomUUID() } : {}),
        ...headers,
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const date = response.headers.get('date');
    if (date) {
      const offset = new Date(date).getTime() - Date.now();
      serverNow = () => new Date(Date.now() + offset);
    }
    if (response.status === 429 && attempt < 8) {
      const wait = Number(response.headers.get('retry-after') ?? '10');
      await new Promise((resolve) => setTimeout(resolve, (wait + 1) * 1000));
      continue;
    }
    return response;
  }
}

async function read(method, path, response) {
  const text = await response.text();
  const json =
    text && (response.headers.get('content-type') ?? '').includes('json') ? JSON.parse(text) : text || undefined;
  if (!response.ok) {
    throw new ApiError(method, path, response.status, json);
  }
  return json;
}

export class Api {
  constructor(userName, token, me) {
    this.userName = userName;
    this.token = token;
    this.me = me;
  }

  /** Signs in as `userName` with the demo password, or reuses the session it signed in with before. */
  static async as(userName, password = PASSWORD) {
    return sessions.get(userName) ?? Api.signIn(userName, password);
  }

  static async signIn(userName, password = PASSWORD) {
    if (!password) {
      throw new Error("Set HC360_DEMO_PASSWORD to the demo accounts' password.");
    }
    const result = await read(
      'POST',
      '/api/v1/auth/login',
      await raw('POST', '/api/v1/auth/login', { body: { userName, password } }),
    );
    if (!result.accessToken || result.mfaRequired || result.restriction) {
      throw new Error(
        `${userName} could not sign in fully (MFA: ${result.mfaRequired}, restriction: ${result.restriction}).`,
      );
    }
    const api = new Api(userName, result.accessToken, result.user);
    sessions.set(userName, api);
    return api;
  }

  async send(method, path, body) {
    let response = await raw(method, path, { token: this.token, body });
    if (response.status === 401 && !path.startsWith('/api/v1/auth/')) {
      // The token ran out (the clock moved on): sign in again, as the app would refresh, and ask once more.
      const fresh = await Api.signIn(this.userName);
      this.token = fresh.token;
      this.me = fresh.me;
      response = await raw(method, path, { token: this.token, body });
    }
    return read(method, path, response);
  }

  get(path) {
    return this.send('GET', path);
  }

  post(path, body = {}) {
    return this.send('POST', path, body);
  }

  put(path, body) {
    return this.send('PUT', path, body);
  }

  /** The response itself, for a status the caller decides on (204 for "no open shift", 422 for a refusal). */
  async response(method, path, body) {
    return raw(method, path, { token: this.token, body });
  }
}

/** The server's time now, asked of the server (its health check's Date header). */
export async function askServerTime() {
  await raw('GET', '/health/live');
  return serverNow();
}

/** Signs in with a temporary password, sets the demo password, as a new member of staff would at first sign-in. */
export async function firstSignIn(userName, temporary, password = PASSWORD) {
  const first = await read(
    'POST',
    '/api/v1/auth/login',
    await raw('POST', '/api/v1/auth/login', { body: { userName, password: temporary } }),
  );
  if (!first.accessToken) {
    throw new Error(`${userName}: the temporary password did not sign in.`);
  }
  await read(
    'POST',
    '/api/v1/auth/change-password',
    await raw('POST', '/api/v1/auth/change-password', {
      token: first.accessToken,
      body: { currentPassword: temporary, newPassword: password },
    }),
  );
}

const INDIA_OFFSET_MINUTES = 330;

/** The hospital's calendar date (IST) of `instant`, as yyyy-mm-dd. */
export function istDate(instant = serverNow()) {
  return new Date(instant.getTime() + INDIA_OFFSET_MINUTES * 60_000).toISOString().slice(0, 10);
}

/** An instant `days` days and `hhmm` IST from the hospital's today. */
export function istAt(days, hhmm) {
  const [h, m] = hhmm.split(':').map(Number);
  const date = new Date(`${istDate()}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() + days);
  return new Date(date.getTime() + (h * 60 + m - INDIA_OFFSET_MINUTES) * 60_000).toISOString();
}

export function addDays(isoDate, days) {
  const date = new Date(`${isoDate}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}
