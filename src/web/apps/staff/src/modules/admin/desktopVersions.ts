import type { DesktopUpdateState, DeviceDto, DeviceKind } from './api';

/**
 * A release version as HC360 numbers them (as ReleaseVersion on the server): `1.2.3`, a fourth number allowed, and a
 * pre-release label after a dash (`1.3.0-rc.1`), which comes before the release. Build metadata (`+abc`) is ignored.
 */
interface ParsedVersion {
  numbers: number[];
  label: string | null;
}

function parse(text: string | null | undefined): ParsedVersion | null {
  const value = text?.trim().split('+')[0];
  if (!value) {
    return null;
  }
  const dash = value.indexOf('-');
  const label = dash >= 0 ? value.slice(dash + 1) : null;
  const parts = (dash >= 0 ? value.slice(0, dash) : value).split('.');
  if (parts.length < 2 || parts.length > 4 || parts.some((p) => !/^\d{1,9}$/.test(p)) || label === '') {
    return null;
  }
  const numbers = parts.map(Number);
  while (numbers.length < 4) {
    numbers.push(0);
  }
  return { numbers, label };
}

function compareLabels(a: string, b: string): number {
  const left = a.split('.');
  const right = b.split('.');
  for (let i = 0; i < Math.min(left.length, right.length); i++) {
    const x = left[i]!;
    const y = right[i]!;
    const xNumber = /^\d+$/.test(x);
    const yNumber = /^\d+$/.test(y);
    if (xNumber && yNumber && Number(x) !== Number(y)) {
      return Math.sign(Number(x) - Number(y));
    }
    // Numbers come before words; words in character order.
    if (xNumber !== yNumber) {
      return xNumber ? -1 : 1;
    }
    if (!xNumber && x !== y) {
      return x < y ? -1 : 1;
    }
  }
  return Math.sign(left.length - right.length);
}

/** Negative when `a` is older than `b`, positive when newer, 0 when the same; null when either is not a version. */
export function compareVersions(a: string | null | undefined, b: string | null | undefined): number | null {
  const left = parse(a);
  const right = parse(b);
  if (!left || !right) {
    return null;
  }
  for (let i = 0; i < 4; i++) {
    const order = left.numbers[i]! - right.numbers[i]!;
    if (order !== 0) {
      return Math.sign(order);
    }
  }
  if (left.label === null || right.label === null) {
    return left.label === right.label ? 0 : left.label === null ? 1 : -1;
  }
  return compareLabels(left.label, right.label);
}

/** The kinds that run HC360 Desktop, and so update themselves from the server; a lab agent is another program. */
export function runsDesktop(kind: DeviceKind): boolean {
  return kind !== 'LabAgent';
}

/**
 * How a device's HC360 Desktop compares with the version the server offers (SRS DED-18): `outdated` when older (it
 * takes the update at its next check, or IT turned self-update off for it), `current` when the same or newer, `unknown`
 * when the device has not said its version, and `none` when there is nothing to compare with: no version offered, a
 * revoked device, or a lab agent.
 */
export type DesktopVersionStatus = 'current' | 'outdated' | 'unknown' | 'none';

export function desktopVersionStatus(
  device: Pick<DeviceDto, 'kind' | 'appVersion' | 'isRevoked'>,
  offeredVersion: string | null | undefined,
): DesktopVersionStatus {
  if (!offeredVersion || device.isRevoked || !runsDesktop(device.kind)) {
    return 'none';
  }
  const order = compareVersions(device.appVersion, offeredVersion);
  if (order === null) {
    return 'unknown';
  }
  return order < 0 ? 'outdated' : 'current';
}

/** The line above the Devices list: which version workstations are offered and how many are behind, or why none is. */
export function desktopUpdateSummary(
  devices: readonly DeviceDto[] | undefined,
  state: DesktopUpdateState | undefined,
): { intent: 'info' | 'warning'; text: string } | null {
  if (state?.problem) {
    return { intent: 'warning', text: `Workstations are offered no update of HC360 Desktop: ${state.problem}` };
  }
  if (!state?.offeredVersion) {
    return null;
  }
  const behind = (devices ?? []).filter((d) => desktopVersionStatus(d, state.offeredVersion) === 'outdated').length;
  return behind === 0
    ? { intent: 'info', text: `Workstations are offered HC360 Desktop ${state.offeredVersion}, and every one runs it.` }
    : {
        intent: 'warning',
        text:
          `Workstations are offered HC360 Desktop ${state.offeredVersion}. ${behind === 1 ? '1 device runs' : `${behind} devices run`} ` +
          'an older version: each installs the update the next time HC360 Desktop is closed or the computer starts.',
      };
}
