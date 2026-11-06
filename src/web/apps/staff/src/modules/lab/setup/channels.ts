import type { AnalyserChannelDto } from '../api';

/** What the analyser last sent on the channel: "9.4 mg/dL", or a dash when it sent no value. */
export function lastReading(channel: AnalyserChannelDto): string {
  if (!channel.lastValue) {
    return '—';
  }
  return channel.lastUnit ? `${channel.lastValue} ${channel.lastUnit}` : channel.lastValue;
}
