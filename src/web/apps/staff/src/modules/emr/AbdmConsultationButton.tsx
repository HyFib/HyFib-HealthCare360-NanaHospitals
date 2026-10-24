import { Button, Spinner, Tooltip } from '@fluentui/react-components';
import { DocumentArrowDownRegular } from '@fluentui/react-icons';
import { useAbdmRecordDownload, useAbdmRecordsAvailable } from './abdmRecords';
import { abdmOpConsultationBundle, consultationExportable } from './abdmConsultation';
import type { EncounterStatus, EncounterType } from './api';

/**
 * "Download ABDM record" on a signed consultation: the note, its diagnoses and prescriptions as a FHIR health record in
 * the format of the Ayushman Bharat Digital Mission, like the discharge summary's. Offered only while ABDM health
 * records are switched on, to staff with `abdm.export`, and only for a consultation the server exports; the server says
 * why when it refuses one (a restricted record).
 */
export function AbdmConsultationButton({
  encounter,
  disabled = false,
}: {
  encounter: { id: string; status: EncounterStatus; type: EncounterType };
  disabled?: boolean;
}) {
  const available = useAbdmRecordsAvailable();
  const download = useAbdmRecordDownload();

  if (!available || !consultationExportable(encounter)) {
    return null;
  }

  return (
    <Tooltip
      content="The signed consultation as a health record in the format of the Ayushman Bharat Digital Mission (ABDM)"
      relationship="description"
    >
      <Button
        icon={download.isPending ? <Spinner size="tiny" aria-hidden /> : <DocumentArrowDownRegular />}
        disabled={disabled}
        disabledFocusable={download.isPending}
        onClick={() => download.mutate(abdmOpConsultationBundle(encounter.id))}
      >
        Download ABDM record
      </Button>
    </Tooltip>
  );
}
