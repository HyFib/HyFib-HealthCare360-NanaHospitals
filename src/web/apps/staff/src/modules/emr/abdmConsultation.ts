import type { EncounterStatus, EncounterType } from './api';

/** A signed consultation as an ABDM OP consultation record (SRS EHR-08), by the encounter's id. */
export const abdmOpConsultationBundle = (encounterId: string) => `/api/v1/abdm/bundles/op-consultations/${encounterId}`;

/**
 * Whether the server exports the encounter as an OP consultation record (`AbdmRecordLoader` on the server): once it is
 * signed (or amended), and not for an in-patient note, whose diagnoses and prescriptions go in the stay's discharge
 * summary instead.
 */
export function consultationExportable(encounter: { status: EncounterStatus; type: EncounterType }): boolean {
  return encounter.status !== 'InProgress' && encounter.type !== 'InPatient';
}
