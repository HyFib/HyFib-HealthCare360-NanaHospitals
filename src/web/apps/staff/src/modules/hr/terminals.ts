import type { BiometricDeviceDto, TerminalPullResult } from '@/modules/hr/api';

/**
 * Plain words for attendance terminals (INT-BIO): whether a terminal is working, how far its clock has drifted and what
 * a read brought in. Terminals either send their punches (push) or are read by HC360 (pull).
 */

type Tone = 'success' | 'warning' | 'danger' | 'informative';

/** A terminal clock this far out is pointed out (BiometricPullService.ClockWarningSeconds). */
export const CLOCK_WARNING_SECONDS = 120;

export const DEFAULT_PULL_PORT = 4370;
export const DEFAULT_PULL_MINUTES = 10;
export const MAX_PULL_MINUTES = 1440;

/** The status shown for a terminal: working, not heard from, or not read, with the reason. */
export function terminalStatus(device: BiometricDeviceDto): { label: string; tone: Tone; detail: string | null } {
  if (!device.isActive) {
    return { label: 'Switched off here', tone: 'informative', detail: null };
  }
  if (device.connection === 'Pull') {
    if (device.lastPullError) {
      return {
        label: device.isOnline ? 'Last read failed' : 'Not read',
        tone: device.isOnline ? 'warning' : 'danger',
        detail: device.lastPullError,
      };
    }
    if (!device.lastPullAt) {
      return { label: 'Not read yet', tone: 'informative', detail: null };
    }
    return device.isOnline
      ? { label: 'Read', tone: 'success', detail: null }
      : { label: 'Not read lately', tone: 'danger', detail: null };
  }
  if (device.isOnline) {
    return { label: 'Online', tone: 'success', detail: null };
  }
  return device.lastPushAt || device.lastHandshakeAt
    ? { label: 'Not heard from', tone: 'danger', detail: null }
    : { label: 'Never connected', tone: 'danger', detail: null };
}

/** How the terminal's punches reach HC360, in a few words. */
export function connectionText(device: BiometricDeviceDto): string {
  if (device.connection === 'Push') {
    return 'Sends its punches';
  }
  const where = device.pullAddress
    ? `${device.pullAddress}${device.pullPort === DEFAULT_PULL_PORT ? '' : `:${device.pullPort}`}`
    : 'no address';
  return `Read every ${everyText(device.pullEveryMinutes)} at ${where}`;
}

/** "10 minutes", "1 hour", "1 day". */
export function everyText(minutes: number): string {
  if (minutes % 1440 === 0) {
    return plural(minutes / 1440, 'day');
  }
  if (minutes % 60 === 0) {
    return plural(minutes / 60, 'hour');
  }
  return plural(minutes, 'minute');
}

/** "Clock 7 min fast" when a terminal’s clock is out by more than two minutes; null otherwise. */
export function clockDrift(offsetSeconds: number | null | undefined): string | null {
  if (offsetSeconds === null || offsetSeconds === undefined || Math.abs(offsetSeconds) < CLOCK_WARNING_SECONDS) {
    return null;
  }
  const minutes = Math.round(Math.abs(offsetSeconds) / 60);
  const amount = minutes >= 120 ? plural(Math.round(minutes / 60), 'hour') : `${minutes} min`;
  return `Clock ${amount} ${offsetSeconds > 0 ? 'fast' : 'slow'}`;
}

/** A read in a sentence, and what to do about anything left out. */
export function readSummary(result: TerminalPullResult): { title: string; detail: string | null } {
  if (!result.success || !result.punches) {
    return { title: 'The terminal could not be read', detail: result.error };
  }
  const { accepted, duplicates, unknownPins, rejected } = result.punches;
  const title = accepted === 0 ? 'No new punches' : `${plural(accepted, 'new punch', 'new punches')} read`;
  const notes: string[] = [];
  if (duplicates > 0) {
    notes.push(`${plural(duplicates, 'punch was', 'punches were')} already here.`);
  }
  if (unknownPins > 0) {
    notes.push(
      `${plural(unknownPins, 'punch is', 'punches are')} from employee numbers not yet given to anyone; they are linked when HR gives the number to an employee.`,
    );
  }
  if (rejected > 0) {
    notes.push(
      `${plural(rejected, 'punch was', 'punches were')} left out: stamped in the future or more than 90 days ago. Check the terminal’s clock.`,
    );
  }
  const drift = clockDrift(result.clockOffsetSeconds);
  if (drift) {
    notes.push(`${drift}: set the terminal’s clock.`);
  }
  return { title, detail: notes.length > 0 ? notes.join(' ') : null };
}

function plural(count: number, one: string, many = `${one}s`): string {
  return `${count} ${count === 1 ? one : many}`;
}
