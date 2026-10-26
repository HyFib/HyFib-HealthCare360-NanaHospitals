import { describe, expect, it } from 'vitest';
import { parseSlotCodes, releaseProblems } from './api';

const empty = { receivedBy: 'Suresh Kumar', relationship: '', idProof: '', policeClearance: '' };

describe('releasing a body', () => {
  it('to relatives needs their relationship and identity proof, and police clearance if medico-legal', () => {
    const problems = releaseProblems({ isMlc: true, identified: true }, { ...empty, to: 'Relatives' });
    expect(problems.relationship).not.toBeNull();
    expect(problems.idProof).not.toBeNull();
    expect(problems.policeClearance).not.toBeNull();
    expect(
      Object.values(
        releaseProblems(
          { isMlc: false, identified: true },
          { ...empty, to: 'Relatives', relationship: 'Son', idProof: 'Aadhaar' },
        ),
      ).every((p) => p === null),
    ).toBe(true);
  });

  it('never donates a medico-legal body or hands an unidentified one to relatives', () => {
    expect(releaseProblems({ isMlc: true, identified: true }, { ...empty, to: 'BodyDonation' }).to).toMatch(/cannot/);
    expect(releaseProblems({ isMlc: false, identified: false }, { ...empty, to: 'Relatives' }).to).toMatch(
      /unidentified/,
    );
  });

  it('to the municipality only on police clearance; to the police on the officer’s details', () => {
    expect(
      releaseProblems({ isMlc: false, identified: false }, { ...empty, to: 'Municipality' }).policeClearance,
    ).not.toBeNull();
    expect(releaseProblems({ isMlc: true, identified: true }, { ...empty, to: 'PoliceForPostMortem' }).idProof).toMatch(
      /officer/,
    );
  });
});

describe('new slots', () => {
  it('are read one per line or comma-separated, in capitals, each once', () => {
    expect(parseSlotCodes('a-1\nA-2, a-1 ')).toEqual(['A-1', 'A-2']);
  });
});
