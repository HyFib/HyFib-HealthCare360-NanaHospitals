// A fake HC360 server for whole-screen tests: canned JSON by method and path. No test framework in here, so the same
// answers feed the apps in jsdom (vitest) and in a real browser (Playwright, ./browser.ts).

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

/** A problem response, as the server sends for an error (`application/problem+json`). */
export function problem(status: number, title: string): Response {
  return new Response(JSON.stringify({ title, status }), {
    status,
    headers: { 'content-type': 'application/problem+json' },
  });
}

/**
 * A `fetch` that answers from `answers`, keyed by method and path ("GET /api/v1/patients"); the query string is
 * ignored. A key may end in "/*" to answer every path below it. Anything else is a 404, and is listed in `unanswered`.
 */
export function createFakeFetch(answers: FakeAnswers) {
  const requests: FakeRequest[] = [];
  const unanswered: string[] = [];
  const answerFor = (method: string, path: string): FakeAnswer => {
    const exact = answers[`${method} ${path}`];
    if (exact !== undefined) {
      return exact;
    }
    const prefix = Object.keys(answers)
      .filter((key) => key.endsWith('/*') && `${method} ${path}`.startsWith(key.slice(0, -1)))
      .sort((a, b) => b.length - a.length)[0];
    return prefix === undefined ? undefined : answers[prefix];
  };
  const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const url = new URL(raw, 'https://hc360.test');
    const method = (init?.method ?? 'GET').toUpperCase();
    const body = typeof init?.body === 'string' && init.body !== '' ? (JSON.parse(init.body) as unknown) : null;
    const request = { method, path: url.pathname, url, body };
    requests.push(request);
    let answer = answerFor(method, url.pathname);
    if (typeof answer === 'function') {
      answer = (answer as (request: FakeRequest) => unknown)(request);
    }
    if (answer === undefined) {
      unanswered.push(`${method} ${url.pathname}${url.search}`);
      return problem(404, 'Not found');
    }
    // A Response is answered as a copy: a page may ask for the same thing again (a refetch), and a body reads once.
    return answer instanceof Response
      ? answer.clone()
      : answer === null
        ? new Response(null, { status: 204 })
        : json(answer);
  };
  return { fetch, requests, unanswered };
}
