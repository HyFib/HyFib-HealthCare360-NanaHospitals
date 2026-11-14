import { Badge, Button, makeStyles, Tooltip } from '@fluentui/react-components';
import { CheckmarkCircleRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { useState } from 'react';
import type { PhoneVerified } from './api';
import { VerifyPhoneDialog } from './VerifyPhoneDialog';

const useStyles = makeStyles({
  row: { display: 'inline-flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
});

/**
 * Whether the record's mobile number is verified (ASVS V6.6.1), and "Verify mobile" for the staff who may verify it (those
 * who edit patients' details). Until the number is verified, the patient portal keeps the record from whoever signs in
 * with it unless they give the patient's date of birth or UHID, and reports, prescriptions, bills and discharge summaries
 * go to it without the document. Nothing when the record has no mobile number.
 */
export function MobileVerification({
  patientId,
  phone,
  verifiedAt,
  canVerify,
  onVerified,
}: {
  patientId: string;
  /** The number on the record as this user may see it, or null for none. */
  phone: string | null;
  /** When the number the record has now was verified, or null/undefined while it is not. */
  verifiedAt: string | null | undefined;
  canVerify: boolean;
  onVerified: (done: PhoneVerified) => void;
}) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  if (!phone) {
    return null;
  }

  if (verifiedAt) {
    return (
      <Tooltip content={`Verified ${formatDateTime(verifiedAt)}`} relationship="description">
        <Badge appearance="tint" color="success" shape="rounded" icon={<CheckmarkCircleRegular />}>
          Verified
        </Badge>
      </Tooltip>
    );
  }

  return (
    <span className={styles.row}>
      <Badge appearance="tint" color="warning" shape="rounded" icon={<WarningRegular />}>
        Not verified
      </Badge>
      {canVerify && (
        <Button size="small" appearance="transparent" onClick={() => setOpen(true)}>
          Verify mobile
        </Button>
      )}
      {open && (
        <VerifyPhoneDialog patientId={patientId} phone={phone} onClose={() => setOpen(false)} onVerified={onVerified} />
      )}
    </span>
  );
}
