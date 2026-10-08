import { describe, expect, it } from 'vitest';
import type { BiometricDeviceDto, TerminalPullResult } from './api';
import { clockDrift, connectionText, everyText, readSummary, terminalStatus } from './terminals';

const device = (overrides: Partial<BiometricDeviceDto> = {}): BiometricDeviceDto => ({
  id: 'd1',
  serialNumber: 'CQZ7232460163',
  name: 'Main gate',
  location: null,
  departmentId: null,
  model: null,
  allowedIpAddress: null,
  hasApiKey: false,
  isActive: true,
  lastPushAt: null,
  lastHandshakeAt: null,
  lastIpAddress: null,
  isOnline: false,
  version: 1,
  connection: 'Pull',
  pullAddress: '192.168.1.201',
  pullPort: 4370,
  pullEveryMinutes: 10,
  lastPullAt: null,
  lastPullError: null,
  clockOffsetSeconds: null,
  offlineSince: null,
  ...overrides,
});

const result = (overrides: Partial<TerminalPullResult> = {}): TerminalPullResult => ({
  success: true,
  error: null,
  read: 12,
  earlier: 0,
  punches: { accepted: 3, duplicates: 9, unknownPins: 0, rejected: 0 },
  clockOffsetSeconds: 4,
  ...overrides,
});

describe('attendance terminals', () => {
  it('says whether a terminal HC360 reads is working, and why not', () => {
    expect(terminalStatus(device())).toMatchObject({ label: 'Not read yet', tone: 'informative' });
    expect(terminalStatus(device({ lastPullAt: '2026-10-08T04:00:00Z', isOnline: true }))).toMatchObject({
      label: 'Read',
      tone: 'success',
    });
    expect(
      terminalStatus(device({ lastPullAt: '2026-10-08T04:00:00Z', lastPullError: 'No answer.', isOnline: true })),
    ).toEqual({ label: 'Last read failed', tone: 'warning', detail: 'No answer.' });
    expect(terminalStatus(device({ lastPullAt: '2026-10-08T04:00:00Z', lastPullError: 'No answer.' }))).toMatchObject({
      label: 'Not read',
      tone: 'danger',
    });
    expect(terminalStatus(device({ lastPullAt: '2026-10-08T04:00:00Z' }))).toMatchObject({ label: 'Not read lately' });
    expect(terminalStatus(device({ isActive: false })).label).toBe('Switched off here');
  });

  it('says whether a terminal that sends its punches is heard from', () => {
    const push = device({ connection: 'Push', pullAddress: null });
    expect(terminalStatus({ ...push, isOnline: true }).label).toBe('Online');
    expect(terminalStatus({ ...push, lastHandshakeAt: '2026-10-08T04:00:00Z' }).label).toBe('Not heard from');
    expect(terminalStatus(push).label).toBe('Never connected');
  });

  it('describes how punches reach HC360', () => {
    expect(connectionText(device())).toBe('Read every 10 minutes at 192.168.1.201');
    expect(connectionText(device({ pullPort: 5005, pullEveryMinutes: 60 }))).toBe(
      'Read every 1 hour at 192.168.1.201:5005',
    );
    expect(connectionText(device({ connection: 'Push' }))).toBe('Sends its punches');
    expect(everyText(1)).toBe('1 minute');
    expect(everyText(1440)).toBe('1 day');
    expect(everyText(90)).toBe('90 minutes');
  });

  it('points out a clock more than two minutes out', () => {
    expect(clockDrift(null)).toBeNull();
    expect(clockDrift(119)).toBeNull();
    expect(clockDrift(420)).toBe('Clock 7 min fast');
    expect(clockDrift(-180)).toBe('Clock 3 min slow');
    expect(clockDrift(3 * 3600)).toBe('Clock 3 hours fast');
  });

  it('sums up a read and says what to do about what was left out', () => {
    expect(readSummary(result())).toEqual({ title: '3 new punches read', detail: '9 punches were already here.' });
    expect(readSummary(result({ punches: { accepted: 1, duplicates: 0, unknownPins: 1, rejected: 0 } }))).toEqual({
      title: '1 new punch read',
      detail:
        '1 punch is from employee numbers not yet given to anyone; they are linked when HR gives the number to an employee.',
    });
    const late = readSummary(
      result({ punches: { accepted: 0, duplicates: 0, unknownPins: 0, rejected: 2 }, clockOffsetSeconds: 900 }),
    );
    expect(late.title).toBe('No new punches');
    expect(late.detail).toContain('2 punches were left out');
    expect(late.detail).toContain('Clock 15 min fast: set the terminal’s clock.');
    expect(readSummary(result({ success: false, error: 'No answer.', punches: null }))).toEqual({
      title: 'The terminal could not be read',
      detail: 'No answer.',
    });
  });
});
