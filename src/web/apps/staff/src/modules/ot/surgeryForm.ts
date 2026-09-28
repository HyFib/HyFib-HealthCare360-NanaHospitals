import { z } from 'zod';
import {
  ANAESTHESIA_TYPES,
  LATERALITIES,
  SURGERY_PRIORITIES,
  type CreateSurgeryBookingRequest,
  type SurgeryDetail,
  type UpdateSurgeryPlanRequest,
} from './api';
import { addMinutesIso, istDate, istDateTimeToIso, istTime, minutesBetween } from './shared/datetime';

/**
 * One form model for the three OT booking flows — book a surgery, edit the surgical plan, give a slot — so the same
 * fields and rules appear everywhere. `mode` decides which rules apply; the server validates everything again.
 */
export type SurgeryFormMode = 'create' | 'plan' | 'schedule';

const pickedPatient = z.object({ id: z.string(), uhid: z.string(), fullName: z.string() });

const baseSchema = z.object({
  patient: pickedPatient.nullable(),
  procedureServiceItemId: z.string().nullable(),
  procedureName: z.string().trim().max(300, 'Use at most 300 characters.'),
  laterality: z.enum(LATERALITIES).nullable(),
  diagnosis: z.string().trim().max(500, 'Use at most 500 characters.'),
  surgeonId: z.string().nullable(),
  anaesthetistId: z.string().nullable(),
  assistantIds: z.array(z.string()).max(10, 'Add at most 10 assistants.'),
  anaesthesiaType: z.enum(ANAESTHESIA_TYPES),
  priority: z.enum(SURGERY_PRIORITIES),
  preferredDate: z.string().nullable(),
  notes: z.string().trim().max(2000, 'Use at most 2000 characters.'),
  scheduleNow: z.boolean(),
  theatreId: z.string().nullable(),
  date: z.string().nullable(),
  time: z.string().nullable(),
  durationMinutes: z.number().nullable(),
});

export type SurgeryFormValues = z.infer<typeof baseSchema>;

export function surgeryFormSchema(mode: SurgeryFormMode, options: { requireTeam?: boolean } = {}) {
  return baseSchema.superRefine((v, ctx) => {
    const issue = (path: keyof SurgeryFormValues, message: string) => ctx.addIssue({ code: 'custom', path: [path], message });

    if (mode === 'create' && !v.patient) {
      issue('patient', 'Choose the patient.');
    }

    if (mode !== 'schedule') {
      if (!v.procedureName) {
        issue('procedureName', 'Enter the planned procedure.');
      }
      if (!v.laterality) {
        issue('laterality', "Choose the operative side, or 'No side' for a midline procedure.");
      }
      if (v.anaesthetistId && v.anaesthetistId === v.surgeonId) {
        issue('anaesthetistId', 'The anaesthetist must be a different doctor from the operating surgeon.');
      }
      const needsTeam = (mode === 'create' && v.scheduleNow) || (mode === 'plan' && options.requireTeam);
      if (needsTeam && !v.surgeonId) {
        issue('surgeonId', 'Choose the operating surgeon.');
      }
      if (needsTeam && v.anaesthesiaType !== 'Local' && !v.anaesthetistId) {
        issue('anaesthetistId', 'Choose the anaesthetist (required for every technique except local anaesthesia).');
      }
    }

    if (mode === 'schedule' || (mode === 'create' && v.scheduleNow)) {
      if (!v.theatreId) {
        issue('theatreId', 'Choose the theatre.');
      }
      if (!v.date) {
        issue('date', 'Choose the date.');
      }
      if (!v.time) {
        issue('time', 'Enter the start time.');
      }
      if (v.durationMinutes === null || v.durationMinutes < 15 || v.durationMinutes > 1440) {
        issue('durationMinutes', 'Enter a duration between 15 minutes and 24 hours.');
      }
    }
  });
}

export const emptySurgeryForm: SurgeryFormValues = {
  patient: null,
  procedureServiceItemId: null,
  procedureName: '',
  laterality: null,
  diagnosis: '',
  surgeonId: null,
  anaesthetistId: null,
  assistantIds: [],
  anaesthesiaType: 'GA',
  priority: 'Elective',
  preferredDate: null,
  notes: '',
  scheduleNow: true,
  theatreId: null,
  date: null,
  time: null,
  durationMinutes: 60,
};

/** Form values for an existing booking (edit plan or reschedule). */
export function surgeryFormFromDetail(d: SurgeryDetail): SurgeryFormValues {
  return {
    patient: d.patient ? { id: d.patient.id, uhid: d.patient.uhid, fullName: d.patient.fullName } : null,
    procedureServiceItemId: d.procedureServiceItemId,
    procedureName: d.procedureName,
    laterality: d.laterality,
    diagnosis: d.diagnosis ?? '',
    surgeonId: d.surgeonId,
    anaesthetistId: d.anaesthetistId,
    assistantIds: d.assistantIds,
    anaesthesiaType: d.anaesthesiaType,
    priority: d.priority,
    preferredDate: d.preferredDate,
    notes: d.notes ?? '',
    scheduleNow: true,
    theatreId: d.theatreId,
    date: d.scheduledStart ? istDate(d.scheduledStart) : d.preferredDate,
    time: d.scheduledStart ? istTime(d.scheduledStart) : null,
    durationMinutes: d.scheduledStart && d.scheduledEnd ? minutesBetween(d.scheduledStart, d.scheduledEnd) : 60,
  };
}

/** The slot as instants, or null while incomplete. */
export function slotOf(v: Pick<SurgeryFormValues, 'date' | 'time' | 'durationMinutes'>): { start: string; end: string } | null {
  const start = istDateTimeToIso(v.date, v.time);
  if (!start || v.durationMinutes === null || v.durationMinutes <= 0) {
    return null;
  }
  return { start, end: addMinutesIso(start, v.durationMinutes) };
}

const orNull = (value: string) => (value.trim() ? value.trim() : null);

export function toCreateRequest(v: SurgeryFormValues): CreateSurgeryBookingRequest {
  const slot = v.scheduleNow ? slotOf(v) : null;
  return {
    patientId: v.patient?.id ?? '',
    admissionId: null,
    visitId: null,
    procedureServiceItemId: v.procedureServiceItemId,
    procedureCode: null,
    procedureName: v.procedureName.trim(),
    laterality: v.laterality ?? 'NotApplicable',
    diagnosis: orNull(v.diagnosis),
    surgeonId: v.surgeonId,
    assistantIds: v.assistantIds.length ? v.assistantIds : null,
    anaesthetistId: v.anaesthetistId,
    anaesthesiaType: v.anaesthesiaType,
    priority: v.priority,
    preferredDate: slot ? (v.date ?? null) : v.preferredDate,
    theatreId: slot ? v.theatreId : null,
    scheduledStart: slot?.start ?? null,
    scheduledEnd: slot?.end ?? null,
    notes: orNull(v.notes),
  };
}

/** The same booking saved as a request without a slot (when no free slot can be found now). */
export function toRequestOnly(v: SurgeryFormValues): CreateSurgeryBookingRequest {
  return { ...toCreateRequest({ ...v, scheduleNow: false, preferredDate: v.preferredDate ?? v.date }), theatreId: null, scheduledStart: null, scheduledEnd: null };
}

export function toUpdateRequest(v: SurgeryFormValues, version: number): UpdateSurgeryPlanRequest {
  return {
    procedureServiceItemId: v.procedureServiceItemId,
    procedureCode: null,
    procedureName: v.procedureName.trim(),
    laterality: v.laterality ?? 'NotApplicable',
    diagnosis: orNull(v.diagnosis),
    surgeonId: v.surgeonId,
    assistantIds: v.assistantIds.length ? v.assistantIds : null,
    anaesthetistId: v.anaesthetistId,
    anaesthesiaType: v.anaesthesiaType,
    priority: v.priority,
    preferredDate: v.preferredDate,
    notes: orNull(v.notes),
    version,
  };
}
