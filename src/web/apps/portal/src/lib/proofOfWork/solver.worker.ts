/// <reference lib="webworker" />
import { solve } from './solver';

/** Solves one challenge off the main thread, so typing and screen readers stay responsive while it works. */
self.onmessage = async (event: MessageEvent<{ challenge: string; bits: number }>) => {
  try {
    const nonce = await solve(event.data.challenge, event.data.bits);
    self.postMessage({ nonce });
  } catch (error) {
    self.postMessage({ error: error instanceof Error ? error.message : String(error) });
  }
};
