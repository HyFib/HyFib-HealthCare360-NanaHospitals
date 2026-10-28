import { Button, Tooltip } from '@fluentui/react-components';
import { LinkRegular } from '@fluentui/react-icons';
import { ConfirmDialog } from '@hc360/ui';
import { useState } from 'react';
import { type AbdmLinkRecord, useAbdmLink } from './abdmLinking';
import { useAbdmRecordsAvailable } from './abdmRecords';

/** Asks before a record goes to ABDM, where the patient sees it in their PHR app. */
export function AbdmLinkDialog({
  record,
  what,
  open,
  onClose,
}: {
  record: AbdmLinkRecord;
  what: string;
  open: boolean;
  onClose: () => void;
}) {
  const link = useAbdmLink();
  return (
    <ConfirmDialog
      open={open}
      title="Link to the patient's ABHA?"
      confirmLabel="Link"
      busy={link.isPending}
      onConfirm={() => link.mutate(record, { onSettled: onClose })}
      onCancel={onClose}
    >
      The {what} goes to the Ayushman Bharat Digital Mission (ABDM) as a record on the ABHA address in the
      patient&apos;s details. The patient sees it in their PHR app and decides whom to share it with.
    </ConfirmDialog>
  );
}

/**
 * "Link to patient's ABHA" beside "Download ABDM record" (ABDM M2, HIP-initiated linking): the record becomes a care
 * context on the patient's ABHA, which the patient sees in their PHR app and can consent to share with other providers.
 * Offered while ABDM health records are switched on, to staff with `abdm.export`, where the screen offers the download;
 * the server says why it cannot link a record (no ABHA address in the patient's details, the gateway not set up).
 */
export function AbdmLinkButton({
  record,
  what,
  disabled = false,
}: {
  record: AbdmLinkRecord;
  what: string;
  disabled?: boolean;
}) {
  const available = useAbdmRecordsAvailable();
  const [confirming, setConfirming] = useState(false);

  if (!available) {
    return null;
  }

  return (
    <>
      <Tooltip
        content={`Links the ${what} to the patient's ABHA, where the patient sees it in their PHR app`}
        relationship="description"
      >
        <Button icon={<LinkRegular />} disabled={disabled} onClick={() => setConfirming(true)}>
          Link to patient&apos;s ABHA
        </Button>
      </Tooltip>
      <AbdmLinkDialog record={record} what={what} open={confirming} onClose={() => setConfirming(false)} />
    </>
  );
}
