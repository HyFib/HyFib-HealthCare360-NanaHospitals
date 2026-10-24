import { Button, Spinner, Tooltip } from '@fluentui/react-components';
import { DocumentArrowDownRegular } from '@fluentui/react-icons';
import { useAbdmRecordDownload, useAbdmRecordsAvailable } from '@/modules/emr/abdmRecords';

/**
 * "Download ABDM record" on a released lab, radiology or cardiology report (SRS EHR-08): the report as a FHIR health
 * record in the format of the Ayushman Bharat Digital Mission, the same file the discharge summary and prescription
 * screens offer. Offered only while ABDM health records are switched on, to staff with `abdm.export`; the caller shows
 * it only for a report the server exports, and the server says why when it refuses one (a restricted record).
 */
export function AbdmReportButton({ path, what }: { path: string; what: string }) {
  const available = useAbdmRecordsAvailable();
  const download = useAbdmRecordDownload();

  if (!available) {
    return null;
  }

  return (
    <Tooltip
      content={`The released ${what} as a health record in the format of the Ayushman Bharat Digital Mission (ABDM)`}
      relationship="description"
    >
      <Button
        icon={download.isPending ? <Spinner size="tiny" aria-hidden /> : <DocumentArrowDownRegular />}
        disabledFocusable={download.isPending}
        onClick={() => download.mutate(path)}
      >
        Download ABDM record
      </Button>
    </Tooltip>
  );
}
