import { afterEach, describe, expect, it, vi } from 'vitest';
import { buildUrl, createApiClient, fileNameFromDisposition } from './client';
import { ApiError, featureOf, isFeatureDisabled, isSessionLocked } from './errors';

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
