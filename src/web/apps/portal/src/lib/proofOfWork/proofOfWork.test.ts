import { ApiError } from '@hc360/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { leadingZeroBits, solve } from './solver';

// The server is outside the test: it hands out the challenges below.
const server = vi.hoisted(() => ({ post: vi.fn() }));
vi.mock('@/lib/api', () => ({ api: { post: server.post } }));

const { CHECK_FAILED, clearPreparedProofs, PROOF_HEADER, prepareProof, SLOW_AFTER_MS, withProof } =
  await import('./index');

async function sha256(text: string): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text)));
}

describe('leadingZeroBits', () => {
  it('counts from the highest bit of the first byte, as the server does', () => {
    expect(leadingZeroBits(new Uint8Array([0xff]))).toBe(0);
    expect(leadingZeroBits(new Uint8Array([0x7f]))).toBe(1);
    expect(leadingZeroBits(new Uint8Array([0x01]))).toBe(7);
    expect(leadingZeroBits(new Uint8Array([0x00, 0x10]))).toBe(11);
    expect(leadingZeroBits(new Uint8Array([0x00, 0x00]))).toBe(16);
  });
});

describe('solve', () => {
  it('finds the first nonce whose SHA-256 starts with the bits asked for', async () => {
    const challenge = '1.otp.10.1760600000.00112233445566778899aabbccddeeff.sig';
    const nonce = await solve(challenge, 10);

    expect(leadingZeroBits(await sha256(challenge + String(nonce)))).toBeGreaterThanOrEqual(10);
    for (let earlier = 0; earlier < nonce; earlier++) {
      expect(leadingZeroBits(await sha256(challenge + String(earlier)))).toBeLessThan(10);
    }
  });

  it('asks nothing of a challenge with no bits', async () => {
    expect(await solve('anything', 0)).toBe(0);
  });

  it('stops when cancelled', async () => {
    const controller = new AbortController();
    controller.abort();
    await expect(solve('x', 30, { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' });
  });

  it('gives up long after the expected work instead of spinning for ever', async () => {
    const never = () => Promise.resolve(new Uint8Array(32).fill(0xff).buffer);
    await expect(solve('x', 1, { digest: never })).rejects.toThrow('No solution');
  });
});

describe('withProof', () => {
  beforeEach(() => {
    server.post.mockReset();
    clearPreparedProofs();
    let n = 0;
    server.post.mockImplementation(async () => ({
      required: true,
      challenge: `1.otp.4.1760600000.${String(++n).padStart(32, '0')}.sig`,
      bits: 4,
      expiresAt: null,
    }));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('sends a solved challenge with the request', async () => {
    const send = vi.fn(async (_headers: Record<string, string>) => 'sent');

    await expect(withProof('otp', send, { subject: '+919876543210' })).resolves.toBe('sent');

    expect(server.post).toHaveBeenCalledWith('/api/v1/public/challenge', { action: 'otp', subject: '+919876543210' });
    const proof = send.mock.calls[0]?.[0][PROOF_HEADER] ?? '';
    const colon = proof.lastIndexOf(':');
    expect(leadingZeroBits(await sha256(proof.slice(0, colon) + proof.slice(colon + 1)))).toBeGreaterThanOrEqual(4);
  });

  it('sends nothing extra while the hospital has the check turned off', async () => {
    server.post.mockResolvedValue({ required: false, challenge: null, bits: 0, expiresAt: null });
    const send = vi.fn(async (_headers: Record<string, string>) => 'sent');

    await withProof('feedback', send);

    expect(send).toHaveBeenCalledWith({});
  });

  it('uses a proof solved in advance, once', async () => {
    prepareProof('otp');
    await vi.waitFor(() => expect(server.post).toHaveBeenCalledTimes(1));
    const send = vi.fn(async (_headers: Record<string, string>) => 'sent');

    await withProof('otp', send);
    await withProof('otp', send);

    expect(server.post).toHaveBeenCalledTimes(2);
    expect(send.mock.calls[0]?.[0][PROOF_HEADER]).not.toBe(send.mock.calls[1]?.[0][PROOF_HEADER]);
  });

  it('tries once more with a fresh challenge for the number when the proof is refused', async () => {
    const refused = new ApiError({ status: 403, title: 'Not allowed', code: CHECK_FAILED });
    const send = vi
      .fn<(headers: Record<string, string>) => Promise<string>>()
      .mockRejectedValueOnce(refused)
      .mockResolvedValueOnce('sent');
    prepareProof('otp');

    await expect(withProof('otp', send, { subject: '+919876543210' })).resolves.toBe('sent');

    expect(send).toHaveBeenCalledTimes(2);
    expect(server.post).toHaveBeenLastCalledWith('/api/v1/public/challenge', {
      action: 'otp',
      subject: '+919876543210',
    });
  });

  it('gives up after the second refusal, and passes other errors on at once', async () => {
    const refused = new ApiError({ status: 403, title: 'Not allowed', code: CHECK_FAILED });
    const always = vi.fn(async () => Promise.reject(refused));
    await expect(withProof('otp', always)).rejects.toBe(refused);
    expect(always).toHaveBeenCalledTimes(2);

    const wrongCode = new ApiError({ status: 400, title: 'Wrong', code: 'validation' });
    const once = vi.fn(async () => Promise.reject(wrongCode));
    await expect(withProof('otp', once)).rejects.toBe(wrongCode);
    expect(once).toHaveBeenCalledTimes(1);
  });

  it('says it is checking only when the check takes long', async () => {
    vi.useFakeTimers();
    let answer: (value: unknown) => void = () => undefined;
    server.post.mockImplementation(() => new Promise((resolve) => (answer = resolve)));
    const onSlow = vi.fn();

    const sending = withProof('otp', async () => 'sent', { onSlow });
    await vi.advanceTimersByTimeAsync(SLOW_AFTER_MS - 1);
    expect(onSlow).not.toHaveBeenCalledWith(true);
    await vi.advanceTimersByTimeAsync(1);
    expect(onSlow).toHaveBeenLastCalledWith(true);

    answer({ required: false, challenge: null, bits: 0, expiresAt: null });
    await expect(sending).resolves.toBe('sent');
    expect(onSlow).toHaveBeenLastCalledWith(false);
  });
});
