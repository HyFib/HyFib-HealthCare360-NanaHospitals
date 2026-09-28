import type { Gender } from '@hc360/core';
import { PatientBanner, type PatientBannerData } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { patientQueries, type PatientSummary } from '@/modules/patients/api';

export interface BannerFallback {
  uhid: string;
  fullName: string;
  gender?: Gender;
  age?: string | null;
}

/**
 * Patient context on every specialty-care screen. Uses the summary that came with the record when there is one;
 * otherwise loads the demographics (all staff may read them) and shows the identity we already have meanwhile.
 *
 * `hideBloodGroup` is used by the blood bank: the group recorded at registration is self-reported and must never be
 * mistaken for the blood-bank grouping of the patient's sample.
 */
export function SpecialtyPatientBanner({
  summary,
  patientId,
  fallback,
  hideBloodGroup,
  alerts,
  actions,
}: {
  summary?: PatientSummary | null;
  patientId?: string | null;
  fallback: BannerFallback;
  hideBloodGroup?: boolean;
  alerts?: string[];
  actions?: ReactNode;
}) {
  const canReadPatients = usePermission('patients.read');
  const detail = useQuery({
    ...patientQueries.detail(patientId ?? ''),
    enabled: !summary && Boolean(patientId) && canReadPatients,
    retry: false,
  });

  const source = summary ?? detail.data ?? null;
  const data: PatientBannerData = source
    ? {
        id: source.id,
        uhid: source.uhid,
        fullName: source.fullName,
        gender: source.gender,
        age: source.age,
        phone: source.phone,
        bloodGroup: hideBloodGroup ? null : source.bloodGroup,
        category: source.category,
        isVip: source.isVip,
        isDeceased: source.isDeceased,
        alerts,
      }
    : {
        id: patientId ?? fallback.uhid,
        uhid: fallback.uhid,
        fullName: fallback.fullName,
        gender: fallback.gender ?? 'Unknown',
        age: fallback.age ?? null,
        alerts,
      };

  return <PatientBanner patient={data} actions={actions} />;
}
