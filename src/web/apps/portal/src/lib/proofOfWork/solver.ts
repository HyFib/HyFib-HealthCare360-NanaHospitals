/**
 * The proof of work the server asks for before the portal's sign-in and feedback (SRS SEC-06): find a nonce, a decimal
 * number, for which SHA-256 of the UTF-8 text `{challenge}{nonce}` starts with `bits` zero bits. Hashcash, in the
 * manner of ALTCHA, with nothing sent to a third party. Pure and unit-tested; it runs in a worker (`solver.worker.ts`).
 */

/** Leading zero bits of a hash, counted from the first byte's highest bit (as the server counts them). */
export function leadingZeroBits(hash: Uint8Array): number {
  let bits = 0;
  for (const byte of hash) {
    if (byte === 0) {
      bits += 8;
      continue;
    }
    return bits + Math.clz32(byte) - 24;
  }
  return bits;
}

export type Digest = (data: Uint8Array) => Promise<ArrayBuffer>;

const sha256: Digest = (data) => crypto.subtle.digest('SHA-256', data as Uint8Array<ArrayBuffer>);

/**
 * Hashes this many nonces at once: WebCrypto answers each digest with a promise, and waiting for them in batches about
 * doubles the hashes a second over one at a time.
 */
const BATCH = 64;

/** Stops a search that has gone on far beyond what the bits ask for (2^bits tries on average). */
function triesAllowed(bits: number): number {
  return Math.min(Number.MAX_SAFE_INTEGER, 2 ** Math.min(bits + 8, 40));
}

/**
 * The first nonce that solves the challenge. Rejects with an `AbortError` when `signal` aborts, and with an error when
 * none is found within 256 times the expected work (which only a wrong challenge would cause).
 */
export async function solve(
  challenge: string,
  bits: number,
  options: { signal?: AbortSignal; digest?: Digest } = {},
): Promise<number> {
  const digest = options.digest ?? sha256;
  const encoder = new TextEncoder();
  const limit = triesAllowed(bits);
  for (let start = 0; start < limit; start += BATCH) {
    if (options.signal?.aborted) {
      throw new DOMException('The check was cancelled.', 'AbortError');
    }
    const hashes = await Promise.all(
      Array.from({ length: BATCH }, (_, i) => digest(encoder.encode(challenge + String(start + i)))),
    );
    const found = hashes.findIndex((hash) => leadingZeroBits(new Uint8Array(hash)) >= bits);
    if (found >= 0) {
      return start + found;
    }
  }
  throw new Error('No solution found for the challenge.');
}
