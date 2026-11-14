// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useSession } from '../auth/session';
import { createApiClient } from './client';
import { forgetKey, forgetRememberedKeys, rememberedKey, rememberKey, requestFingerprint } from './rememberedKeys';

const STORAGE_KEY = 'hc360:remembered-keys';

describe('remembered keys', () => {
  beforeEach(() => {
    forgetRememberedKeys();
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('keeps a hash of the write and its key in this tab, never what was sent', async () => {
    vi.spyOn(globalThis, 'setTimeout').mockImplementation(((callback: () => void) => {
      callback();
      return 0;
    }) as unknown as typeof setTimeout);
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('Failed to fetch');
      }),
    );
    const client = createApiClient({
      clientType: 'web',
      getAccessToken: () => 'token-1',
      refreshAccessToken: async () => null,
      onSessionExpired: vi.fn(),
      getUserId: () => 'u-asha',
    });

    await client.post('/api/v1/patients/p1/notes', { text: 'Ravi Kumar, diabetic foot ulcer' }).catch(() => undefined);

    const kept = sessionStorage.getItem(STORAGE_KEY) ?? '';
    expect(kept).not.toContain('Ravi');
    expect(kept).not.toContain('/api/v1/patients');
    expect(kept).not.toContain('u-asha');
    const entries = Object.entries(JSON.parse(kept) as Record<string, { key: string; at: number }>);
    expect(entries).toHaveLength(1);
    expect(entries[0]?.[0]).toMatch(/^[0-9a-f]{64}$/);
    expect(entries[0]?.[1].key).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('finds the key again after the page is reloaded', async () => {
    const fingerprint = await requestFingerprint('u-asha', 'POST', '/api/v1/billing/receipts', '{"amount":500}');
    rememberKey(fingerprint ?? '', 'key-1');

    vi.resetModules();
    const reloaded = await import('./rememberedKeys');

    expect(reloaded.rememberedKey(fingerprint ?? '')).toBe('key-1');
  });

  it('forgets a key after a day, as the server does its answer', () => {
    vi.useFakeTimers({ now: new Date('2026-11-14T09:00:00Z'), toFake: ['Date'] });
    rememberKey('f1', 'key-1');

    vi.setSystemTime(new Date('2026-11-15T08:59:00Z'));
    expect(rememberedKey('f1')).toBe('key-1');

    vi.setSystemTime(new Date('2026-11-15T09:00:00Z'));
    expect(rememberedKey('f1')).toBeUndefined();
    expect(sessionStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('forgets one key once its write is answered, and every key at sign-out', () => {
    rememberKey('f1', 'key-1');
    rememberKey('f2', 'key-2');

    forgetKey('f1');
    expect(rememberedKey('f1')).toBeUndefined();
    expect(rememberedKey('f2')).toBe('key-2');

    useSession.getState().clear();
    expect(rememberedKey('f2')).toBeUndefined();
    expect(sessionStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('tells writes apart by the user, the method, the path and the body', async () => {
    const write = await requestFingerprint('u-asha', 'POST', '/api/v1/x', '{"a":1}');

    expect(write).toMatch(/^[0-9a-f]{64}$/);
    expect(await requestFingerprint('u-asha', 'POST', '/api/v1/x', '{"a":1}')).toBe(write);
    const others = await Promise.all([
      requestFingerprint('u-ravi', 'POST', '/api/v1/x', '{"a":1}'),
      requestFingerprint('u-asha', 'PUT', '/api/v1/x', '{"a":1}'),
      requestFingerprint('u-asha', 'POST', '/api/v1/y', '{"a":1}'),
      requestFingerprint('u-asha', 'POST', '/api/v1/x?force=true', '{"a":1}'),
      requestFingerprint('u-asha', 'POST', '/api/v1/x', '{"a":2}'),
    ]);
    expect(new Set([write, ...others]).size).toBe(6);
  });
});
