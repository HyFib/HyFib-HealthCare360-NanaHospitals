import { PatientBanner, type PatientBannerData } from '@hc360/ui';
import type { ReactNode } from 'react';
import { usePatientPhotoUrl } from './usePatientPhotoUrl';

/**
 * The patient banner with the patient's photo, for screens that know the photo's file id. The banner shows the initials
 * while the photo loads, and when there is none or it cannot be read.
 */
export function PatientBannerWithPhoto({ patient, photoFileId, actions }: { patient: PatientBannerData; photoFileId: string | null | undefined; actions?: ReactNode }) {
  const photoUrl = usePatientPhotoUrl(photoFileId);
  return <PatientBanner patient={{ ...patient, photoUrl }} actions={actions} />;
}
