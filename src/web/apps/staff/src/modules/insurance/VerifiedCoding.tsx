import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import type { VerifiedClaimCoding } from './api';

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  line: { display: 'block' },
});

/** True when a claim's codes are the ones medical records verified (as typed, ignoring surrounding spaces). */
function matchesVerifiedCoding(verified: VerifiedClaimCoding, diagnosis: string, procedures: string) {
  return (verified.diagnosisCodes ?? '') === diagnosis.trim() && (verified.procedureCodes ?? '') === procedures.trim();
}

/**
 * The coding medical records verified for a claim's stay or visit (MB-03), next to the claim's own codes: while they
 * differ, the verified codes and a button that copies them into the claim; once they match, a note. Without `onUse`
 * (a closed claim) it shows only that they match.
 */
export function VerifiedCodingNotice({
  verified,
  diagnosis,
  procedures,
  onUse,
}: {
  verified: VerifiedClaimCoding | null;
  diagnosis: string;
  procedures: string;
  onUse?: (verified: VerifiedClaimCoding) => void;
}) {
  const styles = useStyles();
  if (!verified) {
    return onUse ? (
      <Caption1 className={styles.muted}>No coding verified by medical records for this stay or visit yet.</Caption1>
    ) : null;
  }
  if (matchesVerifiedCoding(verified, diagnosis, procedures)) {
    return (
      <Caption1 className={styles.muted}>
        These are the codes medical records verified on {formatDateTime(verified.verifiedAt)}.
      </Caption1>
    );
  }
  if (!onUse) {
    return null;
  }
  return (
    <MessageBar intent="info" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Coding verified by medical records on {formatDateTime(verified.verifiedAt)}</MessageBarTitle>
        <span className={styles.line}>Diagnosis: {verified.diagnosisCodes ?? 'none coded'}</span>
        <span className={styles.line}>Procedures: {verified.procedureCodes ?? 'none coded'}</span>
      </MessageBarBody>
      <MessageBarActions>
        <Button onClick={() => onUse(verified)}>Use these codes</Button>
      </MessageBarActions>
    </MessageBar>
  );
}
