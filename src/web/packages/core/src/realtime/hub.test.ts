import { describe, expect, it } from 'vitest';
import { hubErrorMessage, hubReconnectDelay } from './hub';

describe('hubErrorMessage', () => {
  it("shows the server's own message", () => {
    const error = new Error("An unexpected error occurred invoking 'Admit' on the server. HubException: The patient has left the waiting room.");
    expect(hubErrorMessage(error)).toBe('The patient has left the waiting room.');
  });

  it('hides unexplained server failures behind a plain sentence', () => {
    expect(hubErrorMessage(new Error("An unexpected error occurred invoking 'Admit' on the server."))).toBe(
      'The server could not complete the request. Please try again.',
    );
  });

  it('falls back when there is no message', () => {
    expect(hubErrorMessage(undefined)).toBe('The connection to the server failed.');
  });
});

describe('hubReconnectDelay', () => {
  it('backs off to every 30 seconds and never gives up', () => {
    expect([0, 1, 2, 3, 4, 5, 50].map(hubReconnectDelay)).toEqual([0, 2_000, 5_000, 10_000, 20_000, 30_000, 30_000]);
  });
});
