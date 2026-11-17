import { describe, expect, it } from 'vitest';
import { isChannelMapping } from './channels';

describe('isChannelMapping', () => {
  // The server's ChannelMappingsTests: the same mappings are allowed and refused.
  it('takes a channel or an analyser and its channel, with single spaces inside as the i-CHROMA names vitamin D', () => {
    expect(isChannelMapping('HGB')).toBe(true);
    expect(isChannelMapping('XN550:HGB')).toBe(true);
    expect(isChannelMapping('NEU%')).toBe(true);
    expect(isChannelMapping('VIT D-25')).toBe(true);
    expect(isChannelMapping('VIT 25 OH')).toBe(true);
    expect(isChannelMapping('ICHROMA:VIT D-25')).toBe(true);
  });

  it('refuses a space at an end, two in a row or beside the colon, and other characters', () => {
    expect(isChannelMapping(' VITD')).toBe(false);
    expect(isChannelMapping('VITD ')).toBe(false);
    expect(isChannelMapping('VIT  D')).toBe(false);
    expect(isChannelMapping('ICHROMA :VITD')).toBe(false);
    expect(isChannelMapping('ICHROMA: VITD')).toBe(false);
    expect(isChannelMapping('VIT/D')).toBe(false);
    expect(isChannelMapping('A:B:C')).toBe(false);
    expect(isChannelMapping('X'.repeat(65))).toBe(false);
  });
});
