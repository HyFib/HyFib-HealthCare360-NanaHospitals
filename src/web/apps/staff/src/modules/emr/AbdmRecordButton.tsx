import { Button, Spinner, Tooltip } from '@fluentui/react-components';
import { DocumentArrowDownRegular } from '@fluentui/react-icons';
import { useAbdmRecordDownload, useAbdmRecordsAvailable } from './abdmRecords';
import { abdmDischargeSummaryBundle } from './api';

/**
 * "Download ABDM record" on a signed discharge summary (SRS EHR-08): the summary as a FHIR health record in the format of
 * the Ayushman Bharat Digital Mission. Offered only while ABDM health records are switched on, to staff with
 * `abdm.export`; the server also requires the patient's consent to share their records, and says so when it is missing.
 */
export function AbdmRecordButton({ summaryId, disabled = false }: { summaryId: string; disabled?: boolean }) {
  const available = useAbdmRecordsAvailable();
  const download = useAbdmRecordDownload();

  if (!available) {
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
        onClick={() => download.mutate(abdmDischargeSummaryBundle(summaryId))}
      >
        Download ABDM record
      </Button>
    </Tooltip>
  );
}
