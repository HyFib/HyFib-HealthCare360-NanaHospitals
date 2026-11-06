import { describe, expect, it } from 'vitest';
import type { SurgeryDetail } from './api';
import {
  emptySurgeryForm,
  surgeryFormFromDetail,
  surgeryFormSchema,
  toCreateRequest,
  toUpdateRequest,
  type SurgeryFormValues,
} from './surgeryForm';

const booking: SurgeryFormValues = {
  ...emptySurgeryForm,
  patient: { id: 'p1', uhid: 'NH26000001', fullName: 'Murugan Selvam' },
  procedureName: 'Excision of sebaceous cyst',
  laterality: 'NotApplicable',
  surgeonId: 'dr1',
  scheduleNow: true,
  theatreId: 'ot3',
  date: '2026-11-10',
  time: '09:00',
  durationMinutes: 30,
};

const anaesthetistIssue = (values: SurgeryFormValues) => {
  const result = surgeryFormSchema('create').safeParse(values);
  return result.success ? undefined : result.error.issues.find((i) => i.path[0] === 'anaesthetistId')?.message;
};

describe('the anaesthetist a booking needs (as the server decides)', () => {
  it('asks for one for every technique the surgeon does not give', () => {
    expect(anaesthetistIssue({ ...booking, anaesthesiaType: 'GA' })).toBe(
      'Choose the anaesthetist (required for every technique except local and topical anaesthesia).',
    );
    expect(anaesthetistIssue({ ...booking, anaesthesiaType: 'Regional' })).toBeDefined();
  });

  it('does not ask for one for local or topical anaesthesia, which the surgeon gives', () => {
    expect(anaesthetistIssue({ ...booking, anaesthesiaType: 'Local' })).toBeUndefined();
    expect(anaesthetistIssue({ ...booking, anaesthesiaType: 'Topical' })).toBeUndefined();
  });
});

describe('the assisting nurse / OT in-charge', () => {
  it('goes with the booking and with a change of plan', () => {
    expect(toCreateRequest({ ...booking, nurseId: 'n1' }).nurseId).toBe('n1');
    expect(toUpdateRequest({ ...booking, nurseId: 'n1' }, 3)).toMatchObject({ nurseId: 'n1', version: 3 });
    expect(toCreateRequest(booking).nurseId).toBeNull();
  });

  it('stays on the booking when its plan is edited', () => {
    const detail: SurgeryDetail = {
      id: 'b1',
      bookingNumber: 'NHOT26000123',
      patient: { id: 'p1', uhid: 'NH26000001', fullName: 'Murugan Selvam' } as SurgeryDetail['patient'],
      patientId: 'p1',
      admissionId: null,
      visitId: null,
      orderItemId: null,
      procedureServiceItemId: null,
      procedureCode: null,
      procedureName: 'Phacoemulsification with IOL, right eye',
      laterality: 'Right',
      diagnosis: 'Senile cataract',
      surgeonId: 'dr1',
      surgeonName: 'Dr. Arjun Rao',
      assistantIds: [],
      anaesthetistId: null,
      anaesthetistName: null,
      anaesthesiaType: 'Topical',
      priority: 'Elective',
      preferredDate: null,
      theatreId: 'ot3',
      theatreName: 'OT 3',
      scheduledStart: '2026-11-10T03:30:00Z',
      scheduledEnd: '2026-11-10T04:00:00Z',
      status: 'Scheduled',
      receivedInPreOpAt: null,
      inRoomAt: null,
      startedAt: null,
      completedAt: null,
      preOpComplete: false,
      preOpChecklist: [],
      safetyChecklist: [],
      notes: null,
      cancelReason: null,
      postponeReason: null,
      postponeCount: 0,
      chargeStatus: 'NotDue',
      chargeError: null,
      version: 4,
      nurseId: 'n1',
      nurseName: 'Sr. Lakshmi P',
    };

    const edited = surgeryFormFromDetail(detail);
    expect(edited.nurseId).toBe('n1');
    expect(surgeryFormSchema('plan', { requireTeam: true }).safeParse(edited).success).toBe(true);
    expect(toUpdateRequest(edited, detail.version)).toMatchObject({ nurseId: 'n1', anaesthetistId: null, version: 4 });
  });
});
