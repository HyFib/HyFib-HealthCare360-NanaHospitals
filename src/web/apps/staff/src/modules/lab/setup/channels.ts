import type { AnalyserChannelDto } from '../api';

/** One part of a mapping: letters, digits and . _ % # ^ -, with single spaces inside ("VIT D-25"). */
const PART = '[A-Za-z0-9._%#^-]+(?: [A-Za-z0-9._%#^-]+)*';
const MAPPING = new RegExp(`^${PART}(:${PART})?$`);

/**
 * A channel mapping a test can hold, as the server's MasterRules.IsChannelMapping reads it: CHANNEL or ANALYSER:CHANNEL,
 * at most 64 characters, a part with single spaces inside (some analysers name their tests so: "VIT D-25"), never a
 * space at an end, two in a row or beside the ':'.
 */
export function isChannelMapping(value: string): boolean {
  return value.length <= 64 && MAPPING.test(value);
}

/** What the analyser last sent on the channel: "9.4 mg/dL", or a dash when it sent no value. */
export function lastReading(channel: AnalyserChannelDto): string {
  if (!channel.lastValue) {
    return '—';
  }
  return channel.lastUnit ? `${channel.lastValue} ${channel.lastUnit}` : channel.lastValue;
}
