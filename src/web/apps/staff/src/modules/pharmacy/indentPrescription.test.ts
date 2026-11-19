import { describe, expect, it } from 'vitest';
import type { MedicationOrderDto } from '@/modules/inpatient/nursingApi';
import { indentPrescription } from './indentPrescription';

const CEFTRIAXONE = '0192a000-0000-7000-8000-00000000e001';
const PANTOPRAZOLE = '0192a000-0000-7000-8000-00000000e002';
const SALINE = '0192a000-0000-7000-8000-00000000e003';
const MORNING = '0192a000-0000-7000-8000-00000000e101';
const EVENING = '0192a000-0000-7000-8000-00000000e102';

let lines = 0;

/** A line of the patient's chart: an active e-prescription line, unless said otherwise. */
const line = (
  drugItemId: string | null,
  prescriptionId: string | null,
  more: Partial<MedicationOrderDto> = {},
): MedicationOrderDto => ({
  id: `0192a000-0000-7000-8000-${String(++lines).padStart(12, '0')}`,
  prescriptionId,
  drugItemId,
  drugName: 'Inj. Ceftriaxone',
  genericName: null,
  strength: '1 g',
  dose: '1 g',
  route: 'IV',
  frequency: 'BD',
  durationDays: 5,
  startAt: '2026-12-01T04:20:00Z',
  endAt: null,
  instructions: null,
  isControlledDrug: false,
  isHighAlert: false,
  isAsNeeded: false,
  source: 'Prescription',
  status: 'Active',
  lastDispensedAt: null,
  stoppedAt: null,
  stopReason: null,
  roundTimes: [],
  mode: 'Written',
  isEmergency: false,
  orderedByPractitionerId: null,
  orderedByName: null,
  enteredByName: null,
  awaitingCountersignature: false,
  countersignDueAt: null,
  countersignOverdue: false,
  countersignedAt: null,
  countersignedByName: null,
  stopPending: false,
  stopRequestedAt: null,
  stopRequestedByName: null,
  stopRequestReason: null,
  stopCountersignOverdue: false,
  dosesHeld: false,
  canCountersign: false,
  version: 1,
  ...more,
});

describe('the e-prescription a ward indent supplies', () => {
  it('is that of an e-prescribed medicine on it, which says so; another medicine is no one prescription', () => {
    const plan = indentPrescription([SALINE, CEFTRIAXONE], [line(CEFTRIAXONE, MORNING)]);

    expect(plan.prescriptionId).toBe(MORNING);
    expect(plan.lines[CEFTRIAXONE]).toEqual({
      order: { prescriptionId: MORNING, drugName: 'Inj. Ceftriaxone', dose: '1 g', frequency: 'BD' },
      forIndent: true,
    });
    expect(plan.lines[SALINE]).toBeUndefined();
  });

  it('is one prescription: a medicine of another is marked to be indented on its own', () => {
    const plan = indentPrescription(
      [CEFTRIAXONE, PANTOPRAZOLE],
      [
        line(CEFTRIAXONE, MORNING),
        line(PANTOPRAZOLE, EVENING, { drugName: 'Inj. Pantoprazole', dose: '40 mg', frequency: 'OD' }),
      ],
    );

    expect(plan.prescriptionId).toBe(MORNING);
    expect(plan.lines[CEFTRIAXONE]?.forIndent).toBe(true);
    expect(plan.lines[PANTOPRAZOLE]?.forIndent).toBe(false);
  });

  it("prefers the indent's prescription when two name the same medicine", () => {
    const plan = indentPrescription(
      [PANTOPRAZOLE, CEFTRIAXONE],
      [line(CEFTRIAXONE, MORNING), line(PANTOPRAZOLE, EVENING), line(CEFTRIAXONE, EVENING)],
    );

    expect(plan.prescriptionId).toBe(EVENING);
    expect(plan.lines[CEFTRIAXONE]).toMatchObject({ order: { prescriptionId: EVENING }, forIndent: true });
  });

  it('leaves out ward orders, the pharmacy’s own lines and lines that have stopped', () => {
    const plan = indentPrescription(
      [CEFTRIAXONE],
      [
        line(CEFTRIAXONE, null, { source: 'Manual' }),
        line(CEFTRIAXONE, null, { source: 'Pharmacy', frequency: 'As directed' }),
        line(CEFTRIAXONE, MORNING, { status: 'Stopped' }),
      ],
    );

    expect(plan).toEqual({ prescriptionId: null, lines: {} });
  });
});
