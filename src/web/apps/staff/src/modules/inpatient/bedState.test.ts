import { describe, expect, it } from 'vitest';
import {
  BED_LEGEND,
  BED_STATE,
  BED_STATUSES,
  bedActions,
  bedAlertInfo,
  bedStateInfo,
  bulkBedLabels,
  canMove,
  countBeds,
  matchesBedSearch,
  nextBedNumber,
  occupancyBand,
  occupancyPercent,
  sortAlerts,
} from './bedState';

const everyone = { admit: true, manage: true, housekeeping: true };

describe('bed state legend', () => {
  it('describes every state with a label, an icon and a distinct colour', () => {
    expect(BED_LEGEND.map((s) => s.status)).toEqual(['Vacant', 'Occupied', 'Reserved', 'Cleaning', 'Blocked', 'Maintenance']);
    for (const status of BED_STATUSES) {
      const info = BED_STATE[status];
      expect(info.label).toBeTruthy();
      expect(info.icon).not.toBe('unknown');
    }
    expect(new Set(BED_LEGEND.map((s) => s.tone)).size).toBe(BED_LEGEND.length);
  });

  it('falls back safely for an unknown status', () => {
    expect(bedStateInfo('Teleported')).toMatchObject({ status: 'Unknown', tone: 'neutral', inService: false });
    expect(bedStateInfo(undefined).label).toBe('Unknown');
  });

  it('treats blocked and maintenance beds as out of service', () => {
    expect(BED_STATE.Blocked.inService).toBe(false);
    expect(BED_STATE.Maintenance.inService).toBe(false);
    expect(BED_STATE.Cleaning.inService).toBe(true);
  });
});

describe('bed transitions (mirror of the server state machine)', () => {
  it.each([
    ['Vacant', 'Occupied', true],
    ['Occupied', 'Vacant', false],
    ['Occupied', 'Cleaning', true],
    ['Reserved', 'Cleaning', false],
    ['Maintenance', 'Vacant', false],
    ['Maintenance', 'Cleaning', true],
    ['Blocked', 'Vacant', true],
    ['Cleaning', 'Occupied', false],
  ])('%s -> %s is %s', (from, to, allowed) => {
    expect(canMove(from, to)).toBe(allowed);
  });

  it('offers admit, reserve, cleaning, block and maintenance on a vacant bed', () => {
    expect(bedActions('Vacant', everyone)).toEqual(['admit', 'reserve', 'sendForCleaning', 'block', 'startMaintenance']);
  });

  it('never offers a status change on an occupied bed (discharge or transfer releases it)', () => {
    expect(bedActions('Occupied', everyone)).toEqual([]);
  });

  it('lets a reserved bed be admitted into or released', () => {
    expect(bedActions('Reserved', everyone)).toEqual(['admit', 'cancelReservation']);
  });

  it('sends a repaired bed to cleaning, never straight to vacant', () => {
    const actions = bedActions('Maintenance', everyone);
    expect(actions).toContain('endMaintenance');
    expect(actions).not.toContain('unblock');
    expect(actions).not.toContain('sendForCleaning');
  });

  it('gives housekeeping the cleaning task actions only', () => {
    const housekeeping = { admit: false, manage: false, housekeeping: true };
    expect(bedActions('Cleaning', housekeeping)).toEqual(['startCleaning', 'completeCleaning']);
    expect(bedActions('Vacant', housekeeping)).toEqual(['sendForCleaning']);
    expect(bedActions('Maintenance', housekeeping)).toEqual([]);
  });

  it('shows nothing to a read-only user', () => {
    expect(bedActions('Vacant', { admit: false, manage: false, housekeeping: false })).toEqual([]);
  });
});

describe('bed alerts', () => {
  it('orders alerts most serious first', () => {
    expect(sortAlerts(['DischargeInitiated', 'Mlc', 'HighFallRisk'])).toEqual(['HighFallRisk', 'Mlc', 'DischargeInitiated']);
  });

  it('keeps unknown alerts readable', () => {
    expect(bedAlertInfo('NewServerAlert')).toEqual({ label: 'NewServerAlert', description: 'NewServerAlert', severity: 'informative' });
  });
});

describe('occupancy', () => {
  it('counts beds by state', () => {
    expect(countBeds(['Vacant', 'Occupied', 'Occupied', 'Blocked', 'Cleaning'])).toEqual({
      total: 5,
      occupied: 2,
      vacant: 1,
      reserved: 0,
      cleaning: 1,
      blocked: 1,
      maintenance: 0,
    });
  });

  it('divides occupied beds by beds in service, to one decimal', () => {
    const counts = countBeds(['Occupied', 'Occupied', 'Vacant', 'Blocked', 'Maintenance', 'Cleaning']);
    expect(occupancyPercent(counts)).toBe(50);
    expect(occupancyPercent(countBeds(['Occupied', 'Vacant', 'Vacant']))).toBe(33.3);
    expect(occupancyPercent(countBeds(['Blocked']))).toBe(0);
    expect(occupancyPercent(countBeds([]))).toBe(0);
  });

  it('bands occupancy for colouring', () => {
    expect(occupancyBand(50)).toBe('low');
    expect(occupancyBand(70)).toBe('busy');
    expect(occupancyBand(92.5)).toBe('full');
  });
});

describe('bed search', () => {
  const bed = { label: 'GWM-04', roomNumber: '12', occupant: { patientName: 'Ramesh Kumar', uhid: 'NH26000123', admissionNumber: 'NHIP26000045' } };

  it.each(['gwm-04', 'ramesh', 'nh26000123', 'IP26000045', '12', ''])('matches "%s"', (term) => {
    expect(matchesBedSearch(bed, term)).toBe(true);
  });

  it('does not match other text', () => {
    expect(matchesBedSearch(bed, 'suresh')).toBe(false);
    expect(matchesBedSearch({ label: 'ICU-01' }, 'ramesh')).toBe(false);
  });
});

describe('bulk bed labels', () => {
  it('numbers beds like the server ({PREFIX}-{nn})', () => {
    expect(bulkBedLabels(' gwm ', 1, 3)).toEqual(['GWM-01', 'GWM-02', 'GWM-03']);
    expect(bulkBedLabels('ICU', 99, 3)).toEqual(['ICU-99', 'ICU-100', 'ICU-101']);
    expect(bulkBedLabels('', 1, 3)).toEqual([]);
    expect(bulkBedLabels('A', 1, 0)).toEqual([]);
  });

  it('continues an existing series', () => {
    expect(nextBedNumber('GWM', ['GWM-01', 'GWM-07', 'gwm-03', 'GWF-20', 'GWM-X'])).toBe(8);
    expect(nextBedNumber('NICU', [])).toBe(1);
    expect(nextBedNumber('A.B', ['A.B-04', 'AXB-09'])).toBe(5);
  });
});
