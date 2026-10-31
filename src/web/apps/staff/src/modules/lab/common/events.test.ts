import { describe, expect, it } from 'vitest';
import { invalidationTargets, mergePrefixes } from './events';

describe('realtime invalidation map', () => {
  it('refreshes only the lists an event can change', () => {
    expect(invalidationTargets('lab.qc.run')).toEqual([['lab', 'qc']]);
    expect(invalidationTargets('lab.sample.received')).toContainEqual(['lab', 'worklist']);
    expect(invalidationTargets('lab.sample.received')).not.toContainEqual(['lab', 'qc']);
    expect(invalidationTargets('lab.critical')).toContainEqual(['lab', 'critical-calls']);
  });

  it('refreshes the unmatched analyser results, and only them, when an analyser result is kept aside', () => {
    expect(invalidationTargets('lab.results.unmatched')).toEqual([['lab', 'unmatched']]);
  });

  it('covers other departments and unknown events', () => {
    expect(invalidationTargets('radiology.study.changed')).toEqual([['radiology']]);
    expect(invalidationTargets('cardiology.study.changed')).toEqual([['cardiology']]);
    expect(invalidationTargets('lab.something.new')).toContainEqual(['lab', 'worklist']);
    expect(invalidationTargets('visits.changed')).toEqual([]);
  });

  it('merges duplicate and covered prefixes', () => {
    expect(
      mergePrefixes([
        ['lab', 'orders'],
        ['lab', 'orders'],
        ['radiology'],
        ['radiology', 'studies'],
        ['lab', 'worklist'],
      ]),
    ).toEqual([['radiology'], ['lab', 'orders'], ['lab', 'worklist']]);
  });
});
