import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Radio,
  RadioGroup,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { AttachRegular, DismissRegular, PersonAvailableRegular } from '@fluentui/react-icons';
import { isApiError, useSession } from '@hc360/core';
import { useMutation } from '@tanstack/react-query';
import { useRef } from 'react';
import type { WitnessPurposeKey } from './api';
import {
  AGAINST_ADVICE_SIGNATORY_LABEL,
  uploadSignedForm,
  type AgainstAdviceSignatory,
  type AgainstAdviceState,
  useDoctorSignedIn,
} from './againstAdvice';
import { WitnessPanel } from './WitnessPanel';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '8px' },
  doctor: { display: 'flex', alignItems: 'center', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const SIGNATORIES: AgainstAdviceSignatory[] = ['Patient', 'Relative', 'RefusedToSign'];

/**
 * A patient leaving against medical advice (maker-checker, slice 8): who signed the form, or that nobody would (then the
 * doctor and the witness record the refusal); the doctor who explained the risks, who is the user signed in or confirms
 * here with their own credentials; a witness, another member of staff who confirms with theirs, never the doctor; and,
 * when there is one, the scanned signed form.
 */
export function AgainstAdviceFields({
  value,
  onChange,
  patientId,
  witnessPurpose,
  witnessWho,
}: {
  value: AgainstAdviceState;
  onChange: (value: AgainstAdviceState) => void;
  patientId: string;
  witnessPurpose: WitnessPurposeKey;
  /** Who may witness, in words ("a nurse or doctor of the ward"). */
  witnessWho: string;
}) {
  const styles = useStyles();
  const doctorSignedIn = useDoctorSignedIn();
  const user = useSession((state) => state.user);
  const fileInput = useRef<HTMLInputElement>(null);
  const set = (patch: Partial<AgainstAdviceState>) => onChange({ ...value, ...patch });

  const upload = useMutation({
    mutationFn: (file: File) =>
      uploadSignedForm(patientId, file).then((stored) => ({ id: stored.id, name: file.name })),
    onSuccess: (stored) => set({ signedFormFileId: stored.id, signedFormName: stored.name }),
  });

  return (
    <div className={styles.stack}>
      <Field label="The form for leaving against medical advice" required>
        <RadioGroup
          value={value.signedBy}
          onChange={(_, d) => set({ signedBy: d.value as AgainstAdviceSignatory })}
          layout="horizontal"
        >
          {SIGNATORIES.map((s) => (
            <Radio key={s} value={s} label={AGAINST_ADVICE_SIGNATORY_LABEL[s]} />
          ))}
        </RadioGroup>
      </Field>
      {value.signedBy === 'Relative' && (
        <div className={styles.row}>
          <Field label="Relative's name" required>
            <Input value={value.signatoryName} maxLength={200} onChange={(_, d) => set({ signatoryName: d.value })} />
          </Field>
          <Field label="Relationship to the patient" required>
            <Input value={value.relationship} maxLength={64} onChange={(_, d) => set({ relationship: d.value })} />
          </Field>
        </div>
      )}
      {value.signedBy === 'RefusedToSign' && (
        <Caption1 className={styles.muted}>
          The refusal is recorded by the doctor who explained the risks and the witness, each from their own sign-in.
        </Caption1>
      )}

      {doctorSignedIn ? (
        <div className={styles.doctor} role="status">
          <PersonAvailableRegular aria-hidden />
          <span>
            Risks explained by <strong>{user?.displayName ?? 'you'}</strong> (signed in)
          </span>
        </div>
      ) : (
        <WitnessPanel
          purpose="against_advice.doctor"
          patientId={patientId}
          witness={value.doctor}
          onChange={(doctor) => set({ doctor })}
          who="Doctor"
          legend="The doctor who explained the risks"
          description="The doctor confirms here with their own user name and password that they explained the risks of leaving. Nothing is signed in."
          confirmedLabel="Risks explained by"
        />
      )}
      <WitnessPanel
        purpose={witnessPurpose}
        patientId={patientId}
        witness={value.witness}
        onChange={(witness) => set({ witness })}
        legend={`Witness (${witnessWho}, not the doctor)`}
        description="The witness types their own user name and password here. Nothing is signed in, and the password is not kept."
      />

      <Field
        label="Scanned signed form (optional)"
        validationMessage={isApiError(upload.error) ? upload.error.userMessage : undefined}
        validationState={upload.error ? 'error' : 'none'}
      >
        <input
          ref={fileInput}
          type="file"
          accept="application/pdf,image/png,image/jpeg,image/webp"
          hidden
          aria-label="Scanned signed form"
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) {
              upload.mutate(file);
            }
            e.target.value = '';
          }}
        />
        {value.signedFormFileId ? (
          <div className={styles.doctor}>
            <span>{value.signedFormName ?? 'Signed form attached'}</span>
            <Button
              size="small"
              appearance="subtle"
              icon={<DismissRegular />}
              onClick={() => set({ signedFormFileId: null, signedFormName: null })}
            >
              Remove
            </Button>
          </div>
        ) : (
          <Button
            icon={upload.isPending ? <Spinner size="tiny" aria-hidden /> : <AttachRegular />}
            disabled={upload.isPending}
            onClick={() => fileInput.current?.click()}
          >
            Attach the scanned form
          </Button>
        )}
      </Field>
    </div>
  );
}
