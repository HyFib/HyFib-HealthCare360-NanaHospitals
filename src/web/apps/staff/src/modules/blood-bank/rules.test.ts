import { describe, expect, it } from 'vitest';
import {
  compatibilityText,
  compatibleDonorGroups,
  crossmatchValidity,
  expiryFor,
  expiryUrgency,
  isCompatible,
  issueCheck,
  plasmaCompatible,
  redCellsCompatible,
  returnOutcome,
  separationCheck,
  stockLevel,
  ttiState,
  wholeBloodCompatible,
  type IssueCheckInput,
} from './rules';

describe('ABO/Rh(D) compatibility table (mirrors BloodCompatibility)', () => {
  it('gives red cells by antigen and keeps Rh(D)-negative recipients on negative cells', () => {
    expect(redCellsCompatible('ONegative', 'ABPositive')).toBe(true);
    expect(redCellsCompatible('OPositive', 'ANegative')).toBe(false);
    expect(redCellsCompatible('ANegative', 'APositive')).toBe(true);
    expect(redCellsCompatible('BPositive', 'APositive')).toBe(false);
    expect(redCellsCompatible('ABNegative', 'ONegative')).toBe(false);
  });

  it('requires whole blood to be ABO-identical', () => {
    expect(wholeBloodCompatible('APositive', 'APositive')).toBe(true);
    expect(wholeBloodCompatible('ONegative', 'APositive')).toBe(false);
    expect(wholeBloodCompatible('APositive', 'ANegative')).toBe(false);
  });

  it('gives plasma by antibody: AB is the universal plasma donor', () => {
    expect(plasmaCompatible('ABNegative', 'OPositive')).toBe(true);
    expect(plasmaCompatible('OPositive', 'APositive')).toBe(false);
    expect(plasmaCompatible('APositive', 'ONegative')).toBe(true);
    expect(plasmaCompatible('BPositive', 'ABPositive')).toBe(false);
  });

  it('handles Bombay (Oh) recipients and donors', () => {
    expect(redCellsCompatible('ONegative', 'BombayNegative')).toBe(false);
    expect(redCellsCompatible('BombayNegative', 'BombayPositive')).toBe(true);
    expect(redCellsCompatible('BombayNegative', 'ANegative')).toBe(true);
    expect(plasmaCompatible('BombayPositive', 'APositive')).toBe(false);
    expect(plasmaCompatible('OPositive', 'BombayPositive')).toBe(true);
  });

  it('never matches an unknown group', () => {
    expect(isCompatible('PRBC', 'Unknown', 'APositive')).toBe(false);
    expect(isCompatible('FFP', 'ABPositive', 'Unknown')).toBe(false);
  });
});

describe('compatibility display helper', () => {
  it('lists the unit groups a patient may receive', () => {
    expect(compatibleDonorGroups('PRBC', 'APositive')).toEqual([
      'APositive',
      'ANegative',
      'OPositive',
      'ONegative',
      'BombayPositive',
      'BombayNegative',
    ]);
    expect(compatibleDonorGroups('PRBC', 'ONegative')).toEqual(['ONegative', 'BombayNegative']);
    expect(compatibleDonorGroups('WholeBlood', 'BPositive')).toEqual(['BPositive', 'BNegative']);
    expect(compatibleDonorGroups('FFP', 'OPositive')).toEqual([
      'APositive',
      'ANegative',
      'BPositive',
      'BNegative',
      'ABPositive',
      'ABNegative',
      'OPositive',
      'ONegative',
    ]);
    expect(compatibleDonorGroups('PlateletConcentrate', 'ABPositive')).toEqual(['ABPositive', 'ABNegative']);
    expect(compatibleDonorGroups('PRBC', 'Unknown')).toEqual([]);
  });

  it('explains the rule in words, and refuses to guess for an ungrouped patient', () => {
    expect(compatibilityText('PRBC', 'ONegative')).toMatch(
      /Compatible unit groups for a O− patient: O−, Bombay \(hh\)−\./,
    );
    expect(compatibilityText('FFP', 'Unknown')).toMatch(/Group the patient’s sample first/);
  });
});

describe('cross-match validity', () => {
  const performed = '2026-09-24T04:00:00.000Z';

  it('is valid for 72 hours after a compatible result', () => {
    const in71h = Date.parse(performed) + 71 * 3_600_000;
    expect(crossmatchValidity(performed, in71h)).toEqual({
      valid: true,
      expiresAt: '2026-09-27T04:00:00.000Z',
      hoursLeft: 1,
    });
    const in73h = Date.parse(performed) + 73 * 3_600_000;
    expect(crossmatchValidity(performed, in73h).valid).toBe(false);
    expect(crossmatchValidity(performed, in71h, false).valid).toBe(false);
  });
});

describe('issue gate (mirrors IssueCompatibility and BloodComponent.Issue)', () => {
  const base: IssueCheckInput = {
    component: 'PRBC',
    unitGroup: 'APositive',
    patientGroup: 'APositive',
    hasValidCompatibleCrossmatch: true,
    emergencyReleaseAuthorised: false,
    unitStatus: 'Reserved',
    unitExpired: false,
    ttiCleared: true,
    reservedForOtherRequest: false,
  };

  it('issues cross-matched compatible red cells', () => {
    expect(issueCheck(base)).toEqual({ allowed: true, emergency: false, reasons: [] });
  });

  it('refuses red cells without a valid cross-match', () => {
    const check = issueCheck({ ...base, hasValidCompatibleCrossmatch: false });
    expect(check.allowed).toBe(false);
    expect(check.reasons).toContain(
      'Red cells can only be issued against a compatible cross-match done in the last 72 hours.',
    );
  });

  it('allows only O Rh(D)-negative packed red cells under emergency release', () => {
    const emergency = {
      ...base,
      hasValidCompatibleCrossmatch: false,
      emergencyReleaseAuthorised: true,
      patientGroup: 'Unknown' as const,
      unitStatus: 'Available' as const,
    };
    expect(issueCheck({ ...emergency, unitGroup: 'ONegative' })).toEqual({
      allowed: true,
      emergency: true,
      reasons: [],
    });
    expect(issueCheck({ ...emergency, unitGroup: 'OPositive' }).reasons).toContain(
      'Uncross-matched emergency release is limited to group O Rh(D)-negative packed red cells.',
    );
    expect(issueCheck({ ...emergency, component: 'WholeBlood', unitGroup: 'ONegative' }).allowed).toBe(false);
  });

  it('allows AB plasma for an ungrouped patient only under emergency release', () => {
    const plasma = {
      ...base,
      component: 'FFP' as const,
      hasValidCompatibleCrossmatch: false,
      patientGroup: 'Unknown' as const,
      unitStatus: 'Available' as const,
    };
    expect(issueCheck({ ...plasma, unitGroup: 'ABPositive' }).reasons).toContain(
      'Group the patient’s sample before issuing plasma, platelets or cryoprecipitate.',
    );
    expect(issueCheck({ ...plasma, unitGroup: 'ABPositive', emergencyReleaseAuthorised: true })).toEqual({
      allowed: true,
      emergency: true,
      reasons: [],
    });
    expect(issueCheck({ ...plasma, unitGroup: 'APositive', patientGroup: 'OPositive' }).allowed).toBe(true);
    expect(issueCheck({ ...plasma, unitGroup: 'OPositive', patientGroup: 'APositive' }).allowed).toBe(false);
  });

  it('never issues expired, unscreened or someone else’s units', () => {
    expect(issueCheck({ ...base, unitExpired: true }).allowed).toBe(false);
    expect(issueCheck({ ...base, ttiCleared: false, unitStatus: 'Quarantine' }).allowed).toBe(false);
    expect(issueCheck({ ...base, reservedForOtherRequest: true }).reasons).toContain(
      'This unit is reserved for another patient’s request.',
    );
  });
});

describe('cold chain on return (mirrors ColdChain.CanRestock)', () => {
  const issued = '2026-09-26T04:00:00.000Z';
  const after = (minutes: number) => Date.parse(issued) + minutes * 60_000;

  it('restocks red cells back within 30 minutes with the temperature kept', () => {
    expect(returnOutcome('PRBC', issued, after(25), true).restock).toBe(true);
    expect(returnOutcome('PRBC', issued, after(31), true).restock).toBe(false);
    expect(returnOutcome('PRBC', issued, after(10), false).reason).toMatch(/temperature was not maintained/);
  });

  it('never restocks thawed plasma products', () => {
    expect(returnOutcome('FFP', issued, after(5), true).restock).toBe(false);
    expect(returnOutcome('Cryoprecipitate', issued, after(5), true).restock).toBe(false);
  });

  it('discards a unit that expired while out', () => {
    expect(returnOutcome('PlateletConcentrate', issued, after(20), true, '2026-09-26T04:10:00.000Z').restock).toBe(
      false,
    );
  });
});

describe('components, shelf life and screening', () => {
  const collected = '2026-09-26T02:00:00.000Z';

  it('dates expiry from collection', () => {
    expect(expiryFor('PlateletConcentrate', collected)).toBe('2026-10-01T02:00:00.000Z');
    expect(expiryFor('PRBC', collected)).toBe('2026-10-31T02:00:00.000Z');
  });

  it('validates a separation plan like the server', () => {
    const now = Date.parse(collected) + 2 * 3_600_000;
    expect(separationCheck('Triple', ['PRBC', 'FFP', 'PlateletConcentrate'], collected, now)).toBeNull();
    expect(separationCheck('Double', ['PRBC', 'FFP', 'PlateletConcentrate'], collected, now)).toMatch(/at most 2/);
    expect(separationCheck('Triple', ['FFP', 'PlateletConcentrate'], collected, now)).toMatch(/packed red cells/);
    expect(separationCheck('Single', ['PRBC'], collected, now)).toMatch(/single bag/);
    expect(separationCheck('Triple', ['WholeBlood', 'FFP'], collected, now)).toMatch(/Whole blood cannot be stored/);
    expect(separationCheck('Triple', ['PRBC', 'FFP'], collected, Date.parse(collected) + 9 * 3_600_000)).toMatch(
      /within 8 hours/,
    );
    expect(separationCheck('Triple', ['PRBC'], collected, Date.parse(collected) + 9 * 3_600_000)).toBeNull();
  });

  it('clears a unit only when all five markers are non-reactive', () => {
    const clean = {
      hiv: 'NonReactive',
      hbsAg: 'NonReactive',
      hcv: 'NonReactive',
      syphilis: 'NonReactive',
      malaria: 'NonReactive',
    } as const;
    expect(ttiState(clean)).toEqual({ complete: true, cleared: true, reactive: [], pending: [] });
    expect(ttiState({ ...clean, malaria: 'Pending' })).toMatchObject({
      complete: false,
      cleared: false,
      pending: ['malaria'],
    });
    expect(ttiState({ ...clean, hbsAg: 'Reactive' })).toMatchObject({
      complete: true,
      cleared: false,
      reactive: ['hbsAg'],
    });
  });

  it('flags expiry and low stock', () => {
    const now = Date.parse('2026-09-26T04:00:00.000Z');
    expect(expiryUrgency('2026-09-26T03:00:00.000Z', now)).toBe('expired');
    expect(expiryUrgency('2026-09-26T20:00:00.000Z', now)).toBe('today');
    expect(expiryUrgency('2026-09-28T20:00:00.000Z', now)).toBe('soon');
    expect(expiryUrgency('2026-10-26T20:00:00.000Z', now)).toBe('ok');
    expect(stockLevel('PRBC', 0)).toBe('out');
    expect(stockLevel('PRBC', 2)).toBe('low');
    expect(stockLevel('PRBC', 6)).toBe('ok');
  });
});
