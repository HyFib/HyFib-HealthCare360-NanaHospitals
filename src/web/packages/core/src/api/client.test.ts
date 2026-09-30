import { afterEach, describe, expect, it, vi } from 'vitest';
import { buildUrl, createApiClient, fileNameFromDisposition } from './client';
import { ApiError } from './errors';

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
});
