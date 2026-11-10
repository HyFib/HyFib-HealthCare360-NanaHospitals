import { describe, expect, it } from 'vitest';
import { createFakeFetch } from './fakeApi';

describe('createFakeFetch', () => {
  it('answers a request from its canned answers and counts it', async () => {
    const server = createFakeFetch({ 'GET /api/v1/patients': [{ id: 'p1' }] });

    const response = await server.fetch('/api/v1/patients?search=ravi');

    await expect(response.json()).resolves.toEqual([{ id: 'p1' }]);
    expect(server.requests.map((r) => `${r.method} ${r.path}`)).toEqual(['GET /api/v1/patients']);
  });

  it('refuses a request whose signal is already aborted, as fetch does, without counting it', async () => {
    // A screen closed while the API client waited to try a 503 again: its late try must not count as the next screen's.
    const server = createFakeFetch({ 'GET /api/v1/patients': [] });
    const closed = new AbortController();
    closed.abort();

    await expect(server.fetch('/api/v1/patients', { signal: closed.signal })).rejects.toMatchObject({
      name: 'AbortError',
    });
    expect(server.requests).toEqual([]);
    expect(server.unanswered).toEqual([]);
  });
});
