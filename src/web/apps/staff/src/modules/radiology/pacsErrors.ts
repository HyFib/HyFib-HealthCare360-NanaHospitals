import { isApiError } from '@hc360/core';

/**
 * PACS answers that mean the images under the study's accession number may be another patient's: PACS holds them under
 * another patient ID, without one, or as several studies. HC360 neither links nor opens them.
 */
export const PACS_PATIENT_SAFETY_CODES: readonly string[] = [
  'pacs.patient_mismatch',
  'pacs.patient_id_missing',
  'pacs.ambiguous_study',
];

export interface PacsNotice {
  intent: 'warning' | 'info';
  title: string;
  message: string;
}

/**
 * How to tell staff why PACS images could not be opened or linked, or null when the error is not a PACS answer. A
 * patient-safety refusal is a warning that names the reason, so nobody retries it as if PACS were only slow.
 */
export function pacsNotice(error: unknown, action: 'open' | 'link'): PacsNotice | null {
  if (!isApiError(error) || !error.code?.startsWith('pacs.')) {
    return null;
  }
  if (PACS_PATIENT_SAFETY_CODES.includes(error.code)) {
    return {
      intent: 'warning',
      title:
        action === 'open'
          ? 'Images not opened: PACS does not confirm this patient'
          : 'Images not linked: PACS does not confirm this patient',
      message: error.userMessage,
    };
  }
  return {
    intent: 'info',
    title: action === 'open' ? 'Images are not available' : 'Images not linked',
    message: error.userMessage,
  };
}
