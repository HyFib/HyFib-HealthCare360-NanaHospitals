import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CopyRegular, EyeOffRegular, EyeRegular } from '@fluentui/react-icons';
import { useNotify } from '@hc360/ui';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { ActionError } from '@/modules/billing/ui';
import { insuranceApi } from './api';

const useStyles = makeStyles({
  number: { fontFamily: tokens.fontFamilyMonospace, letterSpacing: '0.5px' },
  masked: { fontFamily: tokens.fontFamilyMonospace, color: tokens.colorNeutralForeground2 },
  row: { display: 'inline-flex', alignItems: 'center', gap: '4px', flexWrap: 'wrap' },
});

const PURPOSES = ['Filling the pre-authorisation form', 'Submitting or following up a claim', 'Verification call with the insurer or TPA', 'Correcting the policy record'] as const;

/** Seconds the full number stays on screen. */
const VISIBLE_FOR = 60;

/**
 * The policy number is stored encrypted and shown masked. Revealing it is recorded in the patient's access log under
 * the user's name, so the user confirms why they need it; the number hides itself again after a minute.
 */
export function PolicyNumberReveal({ policyId, masked }: { policyId: string; masked: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const canReveal = usePermission(['insurance.policies.manage', 'insurance.preauth.manage', 'insurance.claims.manage']);
  const [asking, setAsking] = useState(false);
  const [purpose, setPurpose] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [number, setNumber] = useState<string | null>(null);

  useEffect(() => {
    if (!number) {
      return;
    }
    const timer = setTimeout(() => setNumber(null), VISIBLE_FOR * 1000);
    return () => clearTimeout(timer);
  }, [number]);

  const reveal = async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await insuranceApi.revealPolicyNumber(policyId);
      setNumber(result.policyNumber);
      setAsking(false);
      setPurpose('');
    } catch (failure) {
      setError(failure);
    } finally {
      setBusy(false);
    }
  };

  if (number) {
    return (
      <span className={styles.row}>
        <Text className={styles.number} weight="semibold">
          {number}
        </Text>
        <Button
          size="small"
          appearance="subtle"
          icon={<CopyRegular />}
          aria-label="Copy the policy number"
          onClick={() => void navigator.clipboard?.writeText(number).then(() => notify.info('Policy number copied'))}
        />
        <Button size="small" appearance="subtle" icon={<EyeOffRegular />} aria-label="Hide the policy number" onClick={() => setNumber(null)} />
      </span>
    );
  }

  return (
    <span className={styles.row}>
      <Text className={styles.masked}>{masked}</Text>
      {canReveal && (
        <Button size="small" appearance="subtle" icon={<EyeRegular />} onClick={() => setAsking(true)}>
          Show
        </Button>
      )}
      <Dialog open={asking} onOpenChange={(_, d) => !d.open && !busy && setAsking(false)} modalType="alert">
        <DialogSurface style={{ maxWidth: 480 }}>
          <DialogBody>
            <DialogTitle>Show the full policy number?</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <MessageBar intent="warning">
                <MessageBarBody>Your name and the time are recorded in the patient&apos;s access log. The number hides again after a minute.</MessageBarBody>
              </MessageBar>
              <Field label="Why do you need it?" required>
                <RadioGroup value={purpose} onChange={(_, d) => setPurpose(d.value)}>
                  {PURPOSES.map((p) => (
                    <Radio key={p} value={p} label={p} />
                  ))}
                </RadioGroup>
              </Field>
              <Caption1>Show it only to the patient, the insurer or the TPA.</Caption1>
              <ActionError error={error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setAsking(false)} disabled={busy}>
                Cancel
              </Button>
              <Button appearance="primary" disabled={!purpose || busy} icon={busy ? <Spinner size="tiny" /> : <EyeRegular />} onClick={() => void reveal()}>
                Show number
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </span>
  );
}
