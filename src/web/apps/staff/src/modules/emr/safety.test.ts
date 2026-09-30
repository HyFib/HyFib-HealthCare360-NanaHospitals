import { describe, expect, it } from 'vitest';
import {
  affectedDrugs,
  alertsFromProblem,
  emptyDecision,
  groupAlerts,
  signBlockers,
  signRequirements,
  toSignRequest,
  type SafetyAlert,
} from './safety';

function alert(partial: Partial<SafetyAlert> & Pick<SafetyAlert, 'key'>): SafetyAlert {
  return {
    kind: 'Interaction',
    severity: 'Moderate',
    isBlocking: false,
    message: partial.key,
    detail: null,
    itemIds: [],
    ...partial,
  };
}

const allergy = alert({
  key: 'allergy:amox',
  kind: 'Allergy',
  severity: 'Major',
  isBlocking: true,
  message: 'Amoxicillin: patient is allergic to penicillin',
  itemIds: ['i1'],
});
const maxDose = alert({
  key: 'dose:paracetamol',
  kind: 'MaxDose',
  severity: 'Major',
  isBlocking: true,
  message: 'Paracetamol above 4 g a day',
  itemIds: ['i2'],
});
const duplicate = alert({
  key: 'dup:ibuprofen',
  kind: 'DuplicateTherapy',
  severity: 'Moderate',
  message: 'Two NSAIDs',
  itemIds: ['i2', 'i3'],
});
const minor = alert({
  key: 'dose:unverified',
  kind: 'DoseUnverified',
  severity: 'Minor',
  message: 'Dose could not be computed',
});
const controlled = alert({
  key: 'controlled:alprazolam',
  kind: 'ControlledDrug',
  severity: 'Info',
  message: 'Alprazolam is Schedule H1',
});

describe('groupAlerts', () => {
  it('separates blocking alerts, warnings and controlled medicines, most severe first', () => {
    const grouped = groupAlerts([minor, controlled, duplicate, maxDose, allergy]);
    expect(grouped.blocking.map((a) => a.key)).toEqual(['allergy:amox', 'dose:paracetamol']);
    expect(grouped.warnings.map((a) => a.key)).toEqual(['dup:ibuprofen', 'dose:unverified']);
    expect(grouped.controlled.map((a) => a.key)).toEqual(['controlled:alprazolam']);
  });

  it('removes duplicate alerts by key', () => {
    expect(groupAlerts([allergy, { ...allergy }]).blocking).toHaveLength(1);
  });
});

describe('sign requirements', () => {
  it('needs nothing when the check is clean', () => {
    const requirements = signRequirements(groupAlerts([]), false);
    expect(signBlockers(requirements, emptyDecision)).toEqual([]);
    expect(toSignRequest(requirements, emptyDecision, 7)).toEqual({
      overrideReason: null,
      acknowledgeWarnings: false,
      confirmControlledDrugs: false,
      version: 7,
    });
  });

  it('asks for an override reason of at least 10 characters for blocking alerts', () => {
    const requirements = signRequirements(groupAlerts([allergy]), false);
    expect(requirements.needsOverride).toBe(true);
    expect(signBlockers(requirements, emptyDecision)[0]).toContain('clinical reason');
    expect(signBlockers(requirements, { ...emptyDecision, overrideReason: '  too short ' })[0]).toContain('9 so far');
    expect(signBlockers(requirements, { ...emptyDecision, overrideReason: '  benefit outweighs risk  ' })).toEqual([]);
    expect(signBlockers(requirements, { ...emptyDecision, overrideReason: 'short' })[0]).toContain('5 so far');
  });

  it('asks for acknowledgement of warnings and confirmation of controlled medicines', () => {
    const requirements = signRequirements(groupAlerts([duplicate, controlled]), true);
    expect(signBlockers(requirements, emptyDecision)).toHaveLength(2);
    const decision = {
      overrideReason: 'ignored because nothing blocks',
      acknowledgeWarnings: true,
      confirmControlledDrugs: true,
    };
    expect(signBlockers(requirements, decision)).toEqual([]);
    expect(toSignRequest(requirements, decision, 3)).toEqual({
      overrideReason: null,
      acknowledgeWarnings: true,
      confirmControlledDrugs: true,
      version: 3,
    });
  });

  it('treats controlled lines as needing confirmation even without an info alert', () => {
    expect(signRequirements(groupAlerts([]), true).needsControlledConfirmation).toBe(true);
  });

  it('sends the trimmed override reason only when something blocks', () => {
    const requirements = signRequirements(groupAlerts([allergy]), false);
    const request = toSignRequest(
      requirements,
      { ...emptyDecision, overrideReason: '  Tolerated amoxicillin in 2024 without reaction  ' },
      1,
    );
    expect(request.overrideReason).toBe('Tolerated amoxicillin in 2024 without reaction');
  });
});

describe('alertsFromProblem', () => {
  it('reads alerts from a safety stop', () => {
    expect(alertsFromProblem('prescription.safety_block', [allergy, { nonsense: true }])).toEqual([allergy]);
    expect(alertsFromProblem('prescription.warnings_unacknowledged', undefined)).toEqual([]);
  });

  it('ignores other errors', () => {
    expect(alertsFromProblem('prescription.not_prescriber', [allergy])).toBeNull();
    expect(alertsFromProblem(undefined, [allergy])).toBeNull();
  });
});

describe('affectedDrugs', () => {
  it('names the lines an alert refers to', () => {
    const items = [
      { id: 'i2', drugName: 'Dolo 650' },
      { id: 'i3', drugName: 'Brufen 400' },
    ];
    expect(affectedDrugs(duplicate, items)).toEqual(['Dolo 650', 'Brufen 400']);
    expect(affectedDrugs(minor, items)).toEqual([]);
  });
});
