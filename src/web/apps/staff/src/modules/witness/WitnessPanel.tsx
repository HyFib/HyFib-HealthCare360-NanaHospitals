import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  tokens,
} from '@fluentui/react-components';
import { PersonAvailableRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { useMutation } from '@tanstack/react-query';
import { useState, type KeyboardEvent } from 'react';
import { witnessApi, type WitnessConfirmed, type WitnessPurposeKey } from './api';

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    margin: 0,
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  legend: { padding: '0 4px', fontWeight: tokens.fontWeightSemibold },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: '8px', alignItems: 'end' },
  confirmed: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * A second nurse witnesses, at this screen, what the signed-in nurse does: they type their own user name and password,
 * and their authenticator code when the server asks for it. Nothing is signed in. The server answers with a confirmation
 * good for this one record, of this patient, for a few minutes; the password is sent once and cleared here.
 */
export function WitnessPanel({
  purpose,
  patientId,
  witness,
  onChange,
}: {
  purpose: WitnessPurposeKey;
  patientId: string | null;
  witness: WitnessConfirmed | null;
  onChange: (witness: WitnessConfirmed | null) => void;
}) {
  const styles = useStyles();
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [askCode, setAskCode] = useState(false);

  const confirm = useMutation({
    mutationFn: () =>
      witnessApi.confirm({
        userName: userName.trim(),
        password,
        code: askCode ? code.trim() : null,
        purpose,
        patientId,
      }),
    onSuccess: (confirmed) => {
      setUserName('');
      setPassword('');
      setCode('');
      setAskCode(false);
      onChange(confirmed);
    },
    onError: (error) => {
      // The password was right and the account has an authenticator: the code goes with it on the next try.
      if (isApiError(error) && error.code === 'witness.code_required') {
        setAskCode(true);
        return;
      }
      setPassword('');
      setCode('');
    },
  });

  const ready = userName.trim().length > 0 && password.length > 0 && (!askCode || /^\d{6}$/.test(code.trim()));
  const send = () => {
    if (ready && !confirm.isPending) {
      confirm.mutate();
    }
  };
  // The panel sits inside the record's own form: Enter confirms the witness, never the record.
  const enterConfirms = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      send();
    }
  };

  if (witness) {
    return (
      <div className={styles.confirmed} role="status">
        <PersonAvailableRegular aria-hidden />
        <span>
          Witnessed by <strong>{witness.name}</strong>
        </span>
        <Button size="small" appearance="subtle" onClick={() => onChange(null)}>
          Change witness
        </Button>
      </div>
    );
  }

  const failed = confirm.error && !(isApiError(confirm.error) && confirm.error.code === 'witness.code_required');
  return (
    <fieldset className={styles.panel}>
      <legend className={styles.legend}>Witness (a second nurse, at this screen)</legend>
      <Caption1 className={styles.muted}>
        A controlled drug is witnessed: the second nurse types their own user name and password here. Nothing is signed
        in, and the password is not kept.
      </Caption1>
      <div className={styles.row}>
        <Field label="Witness's user name" required>
          <Input
            value={userName}
            onChange={(_, d) => setUserName(d.value)}
            onKeyDown={enterConfirms}
            maxLength={64}
            autoComplete="off"
          />
        </Field>
        <Field label="Witness's password" required>
          <Input
            type="password"
            value={password}
            onChange={(_, d) => setPassword(d.value)}
            onKeyDown={enterConfirms}
            maxLength={128}
            autoComplete="off"
          />
        </Field>
        {askCode && (
          <Field label="Witness's authenticator code" required hint="6 digits from their authenticator app">
            <Input
              value={code}
              onChange={(_, d) => setCode(d.value)}
              onKeyDown={enterConfirms}
              inputMode="numeric"
              maxLength={6}
              autoComplete="one-time-code"
            />
          </Field>
        )}
        <Button icon={<ShieldKeyholeRegular />} disabled={!ready || confirm.isPending} onClick={send}>
          Confirm witness
        </Button>
      </div>
      {failed && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            {isApiError(confirm.error)
              ? confirm.error.userMessage
              : 'The witness could not be confirmed. Please try again.'}
          </MessageBarBody>
        </MessageBar>
      )}
    </fieldset>
  );
}
