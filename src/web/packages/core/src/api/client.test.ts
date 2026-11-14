import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useSession } from '../auth/session';
import { buildUrl, createApiClient, fileNameFromDisposition } from './client';
import { ApiError, featureOf, isFeatureDisabled, isSessionLocked } from './errors';
import { forgetRememberedKeys } from './rememberedKeys';

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

describe('api client', () => {
  afterEach(() => vi.unstubAllGlobals());

  const baseConfig = {
    clientType: 'web' as const,
    getAccessToken: () => 'token-1',
    refreshAccessToken: async () => 'token-2',
    onSessionExpired: vi.fn(),
  };

  it('builds query strings and skips empty values', () => {
    expect(buildUrl('', '/api/v1/patients', { search: 'ravi', page: 2, empty: '', none: null, ids: ['a', 'b'] })).toBe(
      '/api/v1/patients?search=ravi&page=2&ids=a&ids=b',
    );
  });

  it('sends the language chosen in the app, and no language when it has none', async () => {
    const languages: (string | null)[] = [];
    const fetchMock = vi.fn().mockImplementation(async (_url: string, init: RequestInit) => {
      languages.push(new Headers(init.headers).get('X-HC360-Language'));
      return jsonResponse(200, {});
    });
    vi.stubGlobal('fetch', fetchMock);
    let language: string | undefined = 'hi';

    const client = createApiClient({ ...baseConfig, clientType: 'portal', getLanguage: () => language });
    await client.get('/api/v1/portal/bills');
    language = undefined;
    await client.get('/api/v1/portal/bills');
    await createApiClient(baseConfig).get('/api/v1/patients');

    expect(languages).toEqual(['hi', null, null]);
  });

  it('retries a POST after a network failure with the same idempotency key', async () => {
    const keys: string[] = [];
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(async (_url: string, init: RequestInit) => {
        keys.push(new Headers(init.headers).get('Idempotency-Key') ?? '');
        throw new TypeError('Failed to fetch');
      })
      .mockImplementationOnce(async (_url: string, init: RequestInit) => {
        keys.push(new Headers(init.headers).get('Idempotency-Key') ?? '');
        return jsonResponse(201, { id: 'p1' });
      });
    vi.stubGlobal('fetch', fetchMock);

    const client = createApiClient(baseConfig);
    const result = await client.post<{ id: string }>('/api/v1/patients', { firstName: 'Ravi' });

    expect(result.id).toBe('p1');
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(keys[0]).toBeTruthy();
    expect(keys[0]).toBe(keys[1]);
  });

  it('does not retry a server-decided failure that names a problem code', async () => {
    const problem = new Response(
      JSON.stringify({ title: 'The AI provider failed', status: 502, code: 'ai.provider_failed' }),
      {
        status: 502,
        headers: { 'content-type': 'application/problem+json' },
      },
    );
    const fetchMock = vi.fn().mockResolvedValueOnce(problem);
    vi.stubGlobal('fetch', fetchMock);

    const client = createApiClient(baseConfig);
    await expect(client.post('/api/v1/ai/assistant/command', { text: 'open billing' })).rejects.toBeInstanceOf(
      ApiError,
    );
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does not retry a feature an administrator switched off, and says which one', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          title: 'Video consultations are turned off',
          status: 503,
          detail: 'Book an in-person visit instead.',
          code: 'feature.disabled',
          data: { feature: 'telehealth' },
        }),
        { status: 503, headers: { 'content-type': 'application/problem+json' } },
      ),
    );
    vi.stubGlobal('fetch', fetchMock);

    const client = createApiClient(baseConfig);
    const error = await client.get('/api/v1/telehealth/sessions').catch((e: unknown) => e);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(isFeatureDisabled(error)).toBe(true);
    expect(isFeatureDisabled(error, 'telehealth')).toBe(true);
    expect(isFeatureDisabled(error, 'patientPortal')).toBe(false);
    expect(featureOf(error)).toBe('telehealth');
    expect((error as ApiError).title).toBe('Video consultations are turned off');
  });

  it('tells the app which feature was refused', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ status: 503, code: 'feature.disabled', data: { feature: 'patientPortal' } }), {
          status: 503,
          headers: { 'content-type': 'application/problem+json' },
        }),
      ),
    );
    const onFeatureDisabled = vi.fn();

    const client = createApiClient({ ...baseConfig, onFeatureDisabled });
    await expect(client.get('/api/v1/portal/profiles')).rejects.toBeInstanceOf(ApiError);
    expect(onFeatureDisabled).toHaveBeenCalledWith('patientPortal');
  });

  it('shows the lock screen when the server says the screen is locked, without refreshing the token', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ title: 'Screen locked', status: 423, code: 'auth.session_locked' }), {
        status: 423,
        headers: { 'content-type': 'application/problem+json' },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const onSessionLocked = vi.fn();
    const refreshAccessToken = vi.fn(async () => 'token-2');

    const client = createApiClient({ ...baseConfig, refreshAccessToken, onSessionLocked });
    const error = await client.get('/api/v1/patients').catch((e: unknown) => e);

    expect(isSessionLocked(error)).toBe(true);
    expect(onSessionLocked).toHaveBeenCalledTimes(1);
    expect(refreshAccessToken).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(isSessionLocked(new ApiError({ status: 423, code: 'other.code' }))).toBe(false);
  });

  it('sends a read refused while the screen is locked again once it is unlocked, so the screen behind keeps its data', async () => {
    const locked = () =>
      new Response(JSON.stringify({ title: 'Screen locked', status: 423, code: 'auth.session_locked' }), {
        status: 423,
        headers: { 'content-type': 'application/problem+json' },
      });
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(async () => locked())
      .mockImplementationOnce(async () => jsonResponse(200, [{ id: 'p1' }]));
    vi.stubGlobal('fetch', fetchMock);
    let unlock: (unlocked: boolean) => void = () => undefined;
    const waitForUnlock = vi.fn(() => new Promise<boolean>((resolve) => (unlock = resolve)));

    const client = createApiClient({ ...baseConfig, onSessionLocked: vi.fn(), waitForUnlock });
    const read = client.get<{ id: string }[]>('/api/v1/patients');
    await vi.waitFor(() => expect(waitForUnlock).toHaveBeenCalledTimes(1));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    unlock(true);

    await expect(read).resolves.toEqual([{ id: 'p1' }]);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('does not send a write again after the lock, nor a read once the session is over', async () => {
    const locked = () =>
      new Response(JSON.stringify({ title: 'Screen locked', status: 423, code: 'auth.session_locked' }), {
        status: 423,
        headers: { 'content-type': 'application/problem+json' },
      });
    const fetchMock = vi.fn(async () => locked());
    vi.stubGlobal('fetch', fetchMock);
    const waitForUnlock = vi.fn(async () => false);
    const client = createApiClient({ ...baseConfig, onSessionLocked: vi.fn(), waitForUnlock });

    const write = await client.post('/api/v1/patients', { firstName: 'Ravi' }).catch((e: unknown) => e);
    const read = await client.get('/api/v1/patients').catch((e: unknown) => e);

    expect(isSessionLocked(write)).toBe(true);
    expect(isSessionLocked(read)).toBe(true);
    expect(waitForUnlock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('tells a switched-off feature from other refusals', () => {
    expect(isFeatureDisabled(new ApiError({ status: 503, code: 'pdf.unavailable' }))).toBe(false);
    expect(
      isFeatureDisabled(new ApiError({ status: 422, code: 'feature.disabled', data: { feature: 'telehealth' } })),
    ).toBe(true);
    expect(isFeatureDisabled(new Error('feature.disabled'))).toBe(false);
    expect(featureOf(new ApiError({ status: 503, code: 'ai.disabled', data: { feature: 'ai' } }))).toBeUndefined();
  });

  it('still retries a gateway failure without a problem body', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('Bad gateway', { status: 502, headers: { 'content-type': 'text/html' } }))
      .mockResolvedValueOnce(jsonResponse(200, { ok: true }));
    vi.stubGlobal('fetch', fetchMock);

    const client = createApiClient(baseConfig);
    await expect(client.get('/api/v1/patients')).resolves.toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  // While its database is down the server still answers every request, with a 500 that names no reason, so the app
  // must be told: an answer alone counts as the server being reachable.
  it('tells the app of a server failure that names no reason, and not of one the server explains or a refusal', async () => {
    const unexplained = new Response(
      JSON.stringify({ title: 'Something went wrong', status: 500, referenceId: 'r1' }),
      { status: 500, headers: { 'content-type': 'application/problem+json' } },
    );
    const explained = new Response(JSON.stringify({ title: 'PDF unavailable', status: 503, code: 'pdf.unavailable' }), {
      status: 503,
      headers: { 'content-type': 'application/problem+json' },
    });
    const responses = [unexplained, explained, jsonResponse(404, { title: 'Not found' })];
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(async () => responses.shift()),
    );
    const onServerFailure = vi.fn();
    const onReachability = vi.fn();
    const client = createApiClient({ ...baseConfig, onServerFailure, onReachability });

    await expect(client.get('/api/v1/inpatient/census')).rejects.toMatchObject({ status: 500 });
    expect(onServerFailure).toHaveBeenCalledTimes(1);
    expect(onReachability).toHaveBeenLastCalledWith(true);
    await expect(client.get('/api/v1/documents/1/pdf')).rejects.toMatchObject({ code: 'pdf.unavailable' });
    await expect(client.get('/api/v1/patients/none')).rejects.toMatchObject({ status: 404 });
    expect(onServerFailure).toHaveBeenCalledTimes(1);
  });

  it('reads the file name a download was given', () => {
    expect(
      fileNameFromDisposition(`attachment; filename="stock.csv"; filename*=UTF-8''Stock%20report%20%E2%82%B9.csv`),
    ).toBe('Stock report ₹.csv');
    expect(fileNameFromDisposition('attachment; filename="register-2026-09.csv"')).toBe('register-2026-09.csv');
    expect(fileNameFromDisposition('attachment; filename=plain.xlsx')).toBe('plain.xlsx');
    expect(fileNameFromDisposition(null)).toBeNull();
  });

  it('downloads a file with the name the server gave it', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      new Response('%PDF-1.7', {
        status: 200,
        headers: {
          'content-type': 'application/pdf',
          'content-disposition': `attachment; filename=bill-NHOB260000001.pdf; filename*=UTF-8''bill-NHOB260000001.pdf`,
          'x-report-notice': 'The PDF lists the first 2,000 of 2,345 rows.',
        },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const client = createApiClient(baseConfig);
    const file = await client.download('/api/v1/billing/bills/b1/pdf');

    expect(file.fileName).toBe('bill-NHOB260000001.pdf');
    expect(await file.blob.text()).toBe('%PDF-1.7');
    expect(file.headers.get('X-Report-Notice')).toBe('The PDF lists the first 2,000 of 2,345 rows.');
    expect(new Headers((fetchMock.mock.calls[0]![1] as RequestInit).headers).get('Authorization')).toBe(
      'Bearer token-1',
    );
  });

  it('reports why a PDF could not be made, without retrying', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          title: 'The PDF could not be made',
          status: 503,
          detail: 'Microsoft Edge is not installed on the server.',
          code: 'pdf.unavailable',
        }),
        { status: 503, headers: { 'content-type': 'application/problem+json' } },
      ),
    );
    vi.stubGlobal('fetch', fetchMock);

    const client = createApiClient(baseConfig);
    const error = await client.download('/api/v1/lab/reports/r1/pdf').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe('pdf.unavailable');
    expect((error as ApiError).userMessage).toBe('Microsoft Edge is not installed on the server.');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('refreshes once on 401 and replays the request', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(401, {}))
      .mockResolvedValueOnce(jsonResponse(200, { ok: true }));
    vi.stubGlobal('fetch', fetchMock);
    const refresh = vi.fn(async () => 'fresh');

    const client = createApiClient({ ...baseConfig, refreshAccessToken: refresh });
    await expect(client.get('/api/v1/patients')).resolves.toEqual({ ok: true });
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('turns problem details into ApiError with field errors', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        jsonResponse(400, {
          title: 'Please correct the highlighted fields.',
          errors: { firstName: ['First name is required.'] },
        }),
      ),
    );
    const client = createApiClient(baseConfig);

    const error = await client.post('/api/v1/patients', {}).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).isValidation).toBe(true);
    expect((error as ApiError).userMessage).toBe('First name is required.');
  });

  it('sends one correlation id per request, the same on each retry', async () => {
    const ids: string[] = [];
    const fetchMock = vi.fn().mockImplementation(async (_url: string, init: RequestInit) => {
      ids.push(new Headers(init.headers).get('X-Correlation-Id') ?? '');
      return ids.length === 1 ? new Response(null, { status: 503 }) : jsonResponse(200, { ok: true });
    });
    vi.stubGlobal('fetch', fetchMock);
    vi.spyOn(Math, 'random').mockReturnValue(0);

    const client = createApiClient(baseConfig);
    await client.get('/api/v1/patients');
    await client.get('/api/v1/patients');

    expect(ids).toHaveLength(3);
    expect(ids[0]).toMatch(/^[0-9a-f-]{36}$/);
    expect(ids[1]).toBe(ids[0]);
    expect(ids[2]).not.toBe(ids[0]);
    vi.restoreAllMocks();
  });

  it("shows the server's correlation id as the reference of an error", async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ title: 'Not found', status: 404 }), {
          status: 404,
          headers: { 'content-type': 'application/problem+json', 'X-Correlation-Id': 'abc123' },
        }),
      ),
    );
    const client = createApiClient(baseConfig);

    const error = (await client.get('/api/v1/patients/x').catch((e: unknown) => e)) as ApiError;
    expect(error.referenceId).toBe('abc123');
  });

  it('prefers the reference the problem body gives', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({ title: 'Something went wrong', status: 500, referenceId: 'from-body', code: 'x' }),
          {
            status: 500,
            headers: { 'content-type': 'application/problem+json', 'X-Correlation-Id': 'from-header' },
          },
        ),
      ),
    );
    const client = createApiClient(baseConfig);

    const error = (await client.get('/api/v1/patients').catch((e: unknown) => e)) as ApiError;
    expect(error.referenceId).toBe('from-body');
  });
});

describe('a write whose outcome is not known', () => {
  const receipt = { billId: 'b1', amount: 500, mode: 'Cash' };
  const config = {
    clientType: 'web' as const,
    getAccessToken: () => 'token-1',
    refreshAccessToken: async () => 'token-2',
    onSessionExpired: vi.fn(),
    getUserId: () => 'u-asha',
  };
  let keys: (string | null)[];
  let waits: number[];

  beforeEach(() => {
    forgetRememberedKeys();
    keys = [];
    // The client's waits (backoff, Retry-After) pass at once; each asked-for length is kept.
    waits = [];
    vi.spyOn(globalThis, 'setTimeout').mockImplementation(((callback: () => void, ms?: number) => {
      waits.push(ms ?? 0);
      callback();
      return 0;
    }) as unknown as typeof setTimeout);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  /** The server as the client sees it: each call answers with the next of `answers`, keeping the key it was sent. */
  function server(...answers: (() => Response | Promise<Response>)[]) {
    const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
      keys.push(new Headers(init.headers).get('Idempotency-Key'));
      const next = answers.shift();
      if (!next) {
        throw new Error('No answer left for this call.');
      }
      return next();
    });
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  const unreachable = () => Promise.reject(new TypeError('Failed to fetch'));
  const saved =
    (receiptNo = 'NHR-1') =>
    () =>
      jsonResponse(201, { receiptNo });
  const problem =
    (status: number, code: string, detail: string, headers: Record<string, string> = {}) =>
    () =>
      new Response(JSON.stringify({ title: 'Refused', status, code, detail }), {
        status,
        headers: { 'content-type': 'application/problem+json', ...headers },
      });

  /** A write that could not reach the server, after the client's own three tries. */
  async function lost(client: ReturnType<typeof createApiClient>, body: unknown = receipt): Promise<ApiError> {
    return (await client.post('/api/v1/billing/receipts', body).catch((e: unknown) => e)) as ApiError;
  }

  it('sends the same key again with the same write after a network failure, so the server answers the first attempt', async () => {
    server(unreachable, unreachable, unreachable, saved(), saved('NHR-2'));
    const client = createApiClient(config);

    const failure = await lost(client);
    const resent = await client.post('/api/v1/billing/receipts', receipt);
    const next = await client.post('/api/v1/billing/receipts', receipt);

    expect(failure.code).toBe('network');
    expect(failure.detail).toBe(
      'Check the WiFi connection, then try again. Saving again with the same details will not save it twice; if you change anything, first check whether it was saved.',
    );
    expect(resent).toEqual({ receiptNo: 'NHR-1' });
    expect(new Set(keys.slice(0, 4)).size).toBe(1);
    expect(keys[0]).toMatch(/^[0-9a-f-]{36}$/);
    // Answered, it is forgotten: the same details sent again after that are a new receipt.
    expect(next).toEqual({ receiptNo: 'NHR-2' });
    expect(keys[4]).not.toBe(keys[0]);
  });

  it('keeps the key when the answer is lost while it arrives, and asks for it again', async () => {
    const cutOff = () =>
      new Response(
        new ReadableStream({
          start(controller) {
            controller.error(new TypeError('terminated'));
          },
        }),
        { status: 201, headers: { 'content-type': 'application/json' } },
      );
    server(cutOff, saved());
    const client = createApiClient(config);

    await expect(client.post('/api/v1/billing/receipts', receipt)).resolves.toEqual({ receiptNo: 'NHR-1' });
    expect(keys).toHaveLength(2);
    expect(keys[1]).toBe(keys[0]);
  });

  it('gives a changed write a new key, and keeps the first one for the write it was made for', async () => {
    server(unreachable, unreachable, unreachable, saved('NHR-800'), saved());
    const client = createApiClient(config);

    await lost(client);
    await client.post('/api/v1/billing/receipts', { ...receipt, amount: 800 });
    await client.post('/api/v1/billing/receipts', receipt);

    expect(keys[3]).not.toBe(keys[0]);
    expect(keys[4]).toBe(keys[0]);
  });

  it('does not offer one user the key kept for another', async () => {
    let user = 'u-asha';
    server(unreachable, unreachable, unreachable, saved());
    const client = createApiClient({ ...config, getUserId: () => user });

    await lost(client);
    user = 'u-ravi';
    await client.post('/api/v1/billing/receipts', receipt);

    expect(keys[3]).not.toBe(keys[0]);
  });

  it.each([
    ['a refusal', problem(422, 'bill.finalized', 'The bill is already settled.')],
    ['a server error', problem(500, 'server.error', 'Something went wrong.')],
  ])('forgets the key once the write gets a definitive answer: %s', async (_name, answer) => {
    server(unreachable, unreachable, unreachable, answer, saved());
    const client = createApiClient(config);

    await lost(client);
    await expect(client.post('/api/v1/billing/receipts', receipt)).rejects.toBeInstanceOf(ApiError);
    await client.post('/api/v1/billing/receipts', receipt);

    expect(keys[3]).toBe(keys[0]);
    expect(keys[4]).not.toBe(keys[0]);
  });

  it('forgets every kept key at sign-out', async () => {
    server(unreachable, unreachable, unreachable, saved());
    const client = createApiClient(config);

    await lost(client);
    useSession.getState().clear();
    await client.post('/api/v1/billing/receipts', receipt);

    expect(keys[3]).not.toBe(keys[0]);
  });

  it.each([
    ['a locked screen', problem(423, 'auth.session_locked', 'The screen is locked.')],
    ['too many requests', problem(429, 'rate_limited', 'Too many requests.')],
  ])('keeps the key through a refusal that never reached the store: %s', async (_name, answer) => {
    server(unreachable, unreachable, unreachable, answer, saved());
    const client = createApiClient({ ...config, onSessionLocked: vi.fn() });

    await lost(client);
    await expect(client.post('/api/v1/billing/receipts', receipt)).rejects.toBeInstanceOf(ApiError);
    await client.post('/api/v1/billing/receipts', receipt);

    // Whether the first attempt was saved is still not known, so the write sent again still carries its key.
    expect(new Set(keys).size).toBe(1);
  });

  it('does not count a wait for the first attempt as a try: network failures after it are still retried', async () => {
    const running = problem(409, 'idempotency.in_progress', 'Still running.', { 'Retry-After': '2' });
    server(running, unreachable, unreachable, saved());
    const client = createApiClient(config);

    await expect(client.post('/api/v1/billing/receipts', receipt)).resolves.toEqual({ receiptNo: 'NHR-1' });
    expect(keys).toHaveLength(4);
  });

  it('forgets the key of an earlier attempt that did not finish, and says what the server says: check first', async () => {
    const check =
      'An earlier attempt of this request did not finish, so it may or may not have been saved: check before trying again.';
    server(unreachable, unreachable, unreachable, problem(409, 'idempotency.outcome_unknown', check), saved());
    const client = createApiClient(config);

    await lost(client);
    const error = (await client.post('/api/v1/billing/receipts', receipt).catch((e: unknown) => e)) as ApiError;
    await client.post('/api/v1/billing/receipts', receipt);

    expect(error.status).toBe(409);
    expect(error.code).toBe('idempotency.outcome_unknown');
    expect(error.userMessage).toBe(check);
    expect(keys).toHaveLength(5);
    expect(keys[3]).toBe(keys[0]);
    expect(keys[4]).not.toBe(keys[0]);
  });

  it('waits as the server asks while the first attempt is still running, then gets its answer', async () => {
    const running = problem(409, 'idempotency.in_progress', 'Still running.', { 'Retry-After': '2' });
    server(running, saved());
    const client = createApiClient(config);

    await expect(client.post('/api/v1/billing/receipts', receipt)).resolves.toEqual({ receiptNo: 'NHR-1' });

    expect(waits).toEqual([2000]);
    expect(keys).toHaveLength(2);
    expect(keys[1]).toBe(keys[0]);
  });

  it('stops waiting after a few tries, and keeps the key for when the write is made again', async () => {
    const running = problem(409, 'idempotency.in_progress', 'Still running.', { 'Retry-After': '2' });
    server(running, running, running, running, running, running, saved());
    const client = createApiClient(config);

    const error = (await client.post('/api/v1/billing/receipts', receipt).catch((e: unknown) => e)) as ApiError;
    const resent = await client.post('/api/v1/billing/receipts', receipt);

    expect(error.code).toBe('idempotency.in_progress');
    expect(waits).toEqual([2000, 2000, 2000, 2000, 2000]);
    expect(resent).toEqual({ receiptNo: 'NHR-1' });
    expect(new Set(keys).size).toBe(1);
  });

  it("lets the caller's own key win, and does not keep it", async () => {
    const other = { ...receipt, amount: 900 };
    server(unreachable, unreachable, unreachable, saved(), unreachable, unreachable, unreachable, saved());
    const client = createApiClient(config);

    await lost(client);
    await client.post('/api/v1/billing/receipts', receipt, { idempotencyKey: 'dialog-attempt-1' });
    await client.post('/api/v1/billing/receipts', other, { idempotencyKey: 'dialog-attempt-2' }).catch(() => undefined);
    await client.post('/api/v1/billing/receipts', other);

    // The caller's key wins over the one kept for the same write.
    expect(keys[3]).toBe('dialog-attempt-1');
    // Its own key is the caller's to keep: the client does not send it again by itself.
    expect(keys.slice(4, 7)).toEqual(['dialog-attempt-2', 'dialog-attempt-2', 'dialog-attempt-2']);
    expect(keys[7]).not.toBe('dialog-attempt-2');
  });

  it('gives a form (a file upload) a new key every time, and says to check before sending it again', async () => {
    server(unreachable, unreachable, unreachable, saved());
    const client = createApiClient(config);
    const form = () => {
      const data = new FormData();
      data.append('file', new Blob(['%PDF-1.7']), 'report.pdf');
      return data;
    };

    const failure = (await client.post('/api/v1/files', form()).catch((e: unknown) => e)) as ApiError;
    await client.post('/api/v1/files', form());

    expect(new Set(keys.slice(0, 3)).size).toBe(1);
    expect(keys[3]).not.toBe(keys[0]);
    expect(failure.detail).toBe('Check the WiFi connection. It may have been saved: check before you try again.');
  });

  it('keeps no key for a read, and says only to try again', async () => {
    server(unreachable, unreachable, unreachable);
    const client = createApiClient(config);

    const failure = (await client.get('/api/v1/patients').catch((e: unknown) => e)) as ApiError;

    expect(keys).toEqual([null, null, null]);
    expect(failure.detail).toBe('Check the WiFi connection and try again.');
  });
});
