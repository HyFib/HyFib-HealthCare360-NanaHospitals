import { PatientBanner, type PatientBannerData } from '@hc360/ui';
import type { ReactNode } from 'react';
import { usePatientPayer } from '@/modules/patients/payer';
import { PatientBannerWithPhoto } from '@/modules/patients/PatientBannerWithPhoto';
import { withFlags } from './flags';
import { usePatientFlags } from './hooks';

/**
 * The patient banner for every clinical screen outside the chart (EHR-03, UX-04): the screen's identity data plus the
 * patient's allergies ("no known allergies" only when a clinician recorded it) and current alerts, loaded for staff who
 * may see them and refreshed the moment anyone records a change. The screen's own alerts (bed alerts, emergency…) follow
 * the clinical ones. The payer is the patient's current visit's unless the screen passes its own (an admission's).
 * Pass `photoFileId` when the screen knows the patient's photo.
 */
export function PatientBannerWithFlags({
  patientId,
  patient,
  actions,
  photoFileId,
}: {
  /** The patient whose flags to show; null for a patient with no record yet. */
  patientId: string | null | undefined;
  patient: PatientBannerData;
  actions?: ReactNode;
  photoFileId?: string | null;
}) {
  const payer = usePatientPayer(patientId, patient.payer === undefined);
  const data = withFlags({ ...patient, payer: patient.payer ?? payer }, usePatientFlags(patientId));
  return photoFileId === undefined ? (
    <PatientBanner patient={data} actions={actions} />
  ) : (
    <PatientBannerWithPhoto patient={data} photoFileId={photoFileId} actions={actions} />
  );
}
