import { LogLevel } from '@microsoft/signalr';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { hubErrorMessage, hubLogger, hubReconnectDelay } from './hub';

describe('hubErrorMessage', () => {
  it("shows the server's own message", () => {
    const error = new Error(
      "An unexpected error occurred invoking 'Admit' on the server. HubException: The patient has left the waiting room.",
    );
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

describe('hubLogger', () => {
  afterEach(() => vi.restoreAllMocks());

  it('reports real connection failures', () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    hubLogger.log(
      LogLevel.Error,
      "Failed to start the connection: Error: Unexpected status code returned from negotiate '401'",
    );
    expect(error).toHaveBeenCalledOnce();
  });

  it('stays quiet when a connection is stopped before it finished connecting', () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    hubLogger.log(
      LogLevel.Error,
      'Failed to start the connection: Error: The connection was stopped during negotiation.',
    );
    hubLogger.log(
      LogLevel.Error,
      'Failed to start the connection: Error: The connection was stopped before the hub handshake could complete.',
    );
    expect(error).not.toHaveBeenCalled();
  });

  it('leaves out information and debug messages', () => {
    const info = vi.spyOn(console, 'info').mockImplementation(() => undefined);
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    hubLogger.log(LogLevel.Information, 'WebSocket connected to wss://server/hubs/events.');
    expect(info).not.toHaveBeenCalled();
    expect(warn).not.toHaveBeenCalled();
  });
});
