import type { AuthUser, PagedResult } from '@hc360/core';

// A fake HC360 server for whole-screen tests: canned JSON by method and path. No test framework in here, so the same
// answers can also feed a page in a real browser.

export const FACILITY_ID = '0192a000-0000-7000-8000-0000000000f1';

/** A super administrator ("*"): every module and every screen. */
export const STAFF_USER: AuthUser = {
  id: '0192a000-0000-7000-8000-0000000000a1',
  userName: 'meera.iyer',
  displayName: 'Dr. Meera Iyer',
  type: 'staff',
  facilityId: FACILITY_ID,
  facilityName: 'Nana Hospitals, Pune',
  practitionerId: '0192a000-0000-7000-8000-0000000000d1',
  patientId: null,
  roles: ['SuperAdministrator'],
  permissions: ['*'],
  facilities: [
    { id: FACILITY_ID, code: 'NHP', name: 'Nana Hospitals, Pune' },
    { id: '0192a000-0000-7000-8000-0000000000f2', code: 'NHM', name: 'Nana Hospitals, Mumbai' },
  ],
  mfaEnabled: true,
  hasPin: true,
  mustChangePassword: false,
  restriction: null,
};

export interface FakeRequest {
  method: string;
  path: string;
  url: URL;
  body: unknown;
}

/** A JSON body (null for 204 No Content), a Response, or a function of the request that returns either. */
export type FakeAnswer = unknown;

export type FakeAnswers = Record<string, FakeAnswer>;

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

export function paged<T>(items: T[]): PagedResult<T> {
  return { items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 };
}

/** What the shell asks for on every screen. */
export const SHELL_ANSWERS: FakeAnswers = {
  'GET /api/v1/settings/public': {},
  'GET /api/v1/notifications/unread-count': { count: 2 },
  'GET /api/v1/notifications': paged([]),
};

/**
 * A `fetch` that answers from `answers`, keyed by method and path ("GET /api/v1/patients"); the query string is
 * ignored. A key may end in "/*" to answer every path below it. Anything else is a 404, and is listed in `unanswered`.
 */
export function createFakeFetch(answers: FakeAnswers) {
  const all = { ...SHELL_ANSWERS, ...answers };
  const requests: FakeRequest[] = [];
  const unanswered: string[] = [];
  const answerFor = (method: string, path: string): FakeAnswer => {
    const exact = all[`${method} ${path}`];
    if (exact !== undefined) {
      return exact;
    }
    const prefix = Object.keys(all)
      .filter((key) => key.endsWith('/*') && `${method} ${path}`.startsWith(key.slice(0, -1)))
      .sort((a, b) => b.length - a.length)[0];
    return prefix === undefined ? undefined : all[prefix];
  };
  const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const url = new URL(raw, 'https://hc360.test');
    const method = (init?.method ?? 'GET').toUpperCase();
    const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : null;
    const request = { method, path: url.pathname, url, body };
    requests.push(request);
    let answer = answerFor(method, url.pathname);
    if (typeof answer === 'function') {
      answer = (answer as (request: FakeRequest) => unknown)(request);
    }
    if (answer === undefined) {
      unanswered.push(`${method} ${url.pathname}${url.search}`);
      return new Response(JSON.stringify({ title: 'Not found', status: 404 }), {
        status: 404,
        headers: { 'content-type': 'application/problem+json' },
      });
    }
    return answer instanceof Response ? answer : answer === null ? new Response(null, { status: 204 }) : json(answer);
  };
  return { fetch, requests, unanswered };
}
