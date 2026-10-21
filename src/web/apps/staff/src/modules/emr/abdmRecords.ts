import { saveFile } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { useFeatureOn } from '@/lib/features';
import { usePermission } from '@/lib/permissions';

/**
 * ABDM health records (SRS EHR-08): a signed discharge summary or prescription as a FHIR document in the format of the
 * Ayushman Bharat Digital Mission, offered only while ABDM health records are switched on, to staff with `abdm.export`.
 * Like the printed copy, it does not need the patient's record-sharing consent; the server refuses a restricted record
 * the user may not open, and a prescription the hospital's DSC rule does not let it issue, and says why.
 */
export function useAbdmRecordsAvailable(): boolean {
  const switchedOn = useFeatureOn('abdm');
  const canExport = usePermission('abdm.export');
  return switchedOn && canExport;
}

/** Downloads the ABDM health record at `path` under the name the server gave it. */
async function downloadAbdmRecord(path: string): Promise<void> {
  const { blob, fileName } = await api.download(path);
  // A WiFi sign-in page or a proxy may answer with a web page instead; that must not be saved as a health record.
  if (!blob.type.toLowerCase().includes('json')) {
    throw new Error('The answer was not an ABDM health record.');
  }
  saveFile(blob, fileName ?? 'abdm-record.json');
}

/** Downloading an ABDM health record (by its path): it says when the file is saved, and why the server refused. */
export function useAbdmRecordDownload() {
  const notify = useNotify();
  return useMutation({
    mutationFn: downloadAbdmRecord,
    onSuccess: () => notify.success('ABDM record downloaded', 'It is in your Downloads folder.'),
    onError: (error) => notify.error(error, 'Could not download the ABDM record'),
  });
}
