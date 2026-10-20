import { Button, Spinner, Tooltip } from '@fluentui/react-components';
import { DocumentArrowDownRegular } from '@fluentui/react-icons';
import { saveFile } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { useFeatureOn } from '@/lib/features';
import { usePermission } from '@/lib/permissions';
import { abdmDischargeSummaryBundle } from './api';

/** Downloads a signed discharge summary's ABDM health record under the name the server gave it. */
async function downloadAbdmRecord(summaryId: string): Promise<void> {
  const { blob, fileName } = await api.download(abdmDischargeSummaryBundle(summaryId));
  // A WiFi sign-in page or a proxy may answer with a web page instead; that must not be saved as a health record.
  if (!blob.type.toLowerCase().includes('json')) {
    throw new Error('The answer was not an ABDM health record.');
  }
  saveFile(blob, fileName ?? 'discharge-summary-abdm.json');
}

/**
 * "Download ABDM record" on a signed discharge summary (SRS EHR-08): the summary as a FHIR health record in the format of
 * the Ayushman Bharat Digital Mission. Offered only while ABDM health records are switched on, to staff with
 * `abdm.export`; the server also requires the patient's consent to share their records, and says so when it is missing.
 */
export function AbdmRecordButton({ summaryId, disabled = false }: { summaryId: string; disabled?: boolean }) {
  const switchedOn = useFeatureOn('abdm');
  const canExport = usePermission('abdm.export');
  const notify = useNotify();
  const download = useMutation({
    mutationFn: () => downloadAbdmRecord(summaryId),
    onSuccess: () => notify.success('ABDM record downloaded', 'It is in your Downloads folder.'),
    onError: (error) => notify.error(error, 'Could not download the ABDM record'),
  });

  if (!switchedOn || !canExport) {
    return null;
  }

  return (
    <Tooltip
      content="The signed summary as a health record in the format of the Ayushman Bharat Digital Mission (ABDM), for a patient who agreed to share their records"
      relationship="description"
    >
      <Button
        icon={download.isPending ? <Spinner size="tiny" aria-hidden /> : <DocumentArrowDownRegular />}
        disabled={disabled}
        disabledFocusable={download.isPending}
        onClick={() => download.mutate()}
      >
        Download ABDM record
      </Button>
    </Tooltip>
  );
}
