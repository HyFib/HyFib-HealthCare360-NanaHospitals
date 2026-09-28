import { describe, expect, it } from 'vitest';
import {
  HANDSHAKE_REQUEST,
  hubErrorMessage,
  hubSocketUrl,
  invocationFrame,
  parseHandshakeResponse,
  parseHubMessage,
  reconnectDelay,
  RECORD_SEPARATOR,
  RecordReader,
} from './hubProtocol';

describe('framing', () => {
  it('terminates every record with the record separator', () => {
    expect(HANDSHAKE_REQUEST).toBe(`{"protocol":"json","version":1}${RECORD_SEPARATOR}`);
    expect(invocationFrame('SendChat', ['s1', 'hello'], '7')).toBe(`{"type":1,"invocationId":"7","target":"SendChat","arguments":["s1","hello"]}${RECORD_SEPARATOR}`);
    expect(invocationFrame('Leave', ['s1'])).toBe(`{"type":1,"target":"Leave","arguments":["s1"]}${RECORD_SEPARATOR}`);
  });

  it('reassembles records split across frames and splits frames holding several', () => {
    const reader = new RecordReader();
    expect(reader.push(`{}${RECORD_SEPARATOR}{"type":6}${RECORD_SEPARATOR}{"type":1,"tar`)).toEqual(['{}', '{"type":6}']);
    expect(reader.push(`get":"offer","arguments":[]}${RECORD_SEPARATOR}`)).toEqual(['{"type":1,"target":"offer","arguments":[]}']);
    expect(reader.push('')).toEqual([]);
  });
});

describe('parsing', () => {
  it('reads the handshake answer', () => {
    expect(parseHandshakeResponse('{}')).toEqual({ error: null });
    expect(parseHandshakeResponse('{"error":"Requested protocol is not available."}')).toEqual({ error: 'Requested protocol is not available.' });
    expect(parseHandshakeResponse('not json').error).toMatch(/unreadable/);
  });

  it('reads invocations, completions, pings and close messages', () => {
    expect(parseHubMessage('{"type":1,"target":"chatMessage","arguments":[{"text":"hi"}]}')).toEqual({
      type: 1,
      target: 'chatMessage',
      invocationId: undefined,
      arguments: [{ text: 'hi' }],
    });
    expect(parseHubMessage('{"type":3,"invocationId":"2","result":{"ok":true}}')).toEqual({ type: 3, invocationId: '2', result: { ok: true }, error: undefined });
    expect(parseHubMessage('{"type":3,"invocationId":"3","error":"Nope"}')).toMatchObject({ error: 'Nope' });
    expect(parseHubMessage('{"type":6}')).toEqual({ type: 6 });
    expect(parseHubMessage('{"type":7,"error":"Server shutting down","allowReconnect":true}')).toEqual({ type: 7, error: 'Server shutting down', allowReconnect: true });
  });

  it('ignores unknown, malformed and streaming messages', () => {
    expect(parseHubMessage('{"type":2,"invocationId":"1","item":1}')).toBeNull();
    expect(parseHubMessage('{"type":1}')).toBeNull();
    expect(parseHubMessage('[1,2]')).toBeNull();
    expect(parseHubMessage('garbage')).toBeNull();
  });
});

describe('errors and reconnects', () => {
  it('shows only the HubException message', () => {
    expect(hubErrorMessage(new Error("An unexpected error occurred invoking 'Admit' on the server. HubException: Nobody is waiting to be admitted."))).toBe(
      'Nobody is waiting to be admitted.',
    );
    expect(hubErrorMessage(new Error("An unexpected error occurred invoking 'Admit' on the server."))).toMatch(/could not complete/);
    expect(hubErrorMessage(new Error('Not connected'))).toBe('Not connected');
    expect(hubErrorMessage(undefined)).toMatch(/failed/);
  });

  it('backs off 0, 2, 5, 10, 20 then 30 seconds plus jitter', () => {
    const noJitter = () => 0;
    expect([0, 1, 2, 3, 4, 5, 12].map((attempt) => reconnectDelay(attempt, noJitter))).toEqual([0, 2000, 5000, 10000, 20000, 30000, 30000]);
    expect(reconnectDelay(0, () => 0.999)).toBe(999);
  });

  it('builds a secure WebSocket address with the token', () => {
    expect(hubSocketUrl('/hubs/telehealth', 'abc.def', 'https://hc360.local:7360/telehealth/1')).toBe('wss://hc360.local:7360/hubs/telehealth?access_token=abc.def');
    expect(hubSocketUrl('/hubs/telehealth', null, 'http://localhost:5360/')).toBe('ws://localhost:5360/hubs/telehealth');
  });
});
