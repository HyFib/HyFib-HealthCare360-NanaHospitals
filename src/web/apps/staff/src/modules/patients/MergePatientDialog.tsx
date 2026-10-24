import {
  Button,
  Caption1,
  Checkbox,
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
  MessageBarTitle,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { useNotify, useUnsavedChanges } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { MERGE_REASON_LIMITS, patientApi, patientKeys, patientQueries, type PatientDetail } from './api';
import { PatientPicker, type PickedPatient } from './PatientPicker';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  compare: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: '12px' },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    minWidth: 0,
  },
  surviving: { border: `1px solid ${tokens.colorBrandStroke1}` },
  label: { color: tokens.colorNeutralForeground3 },
});

/** All but the last four digits hidden, as the patient lists show a number (+91******3210). */
function maskPhone(phone: string | null): string {
  if (!phone) {
    return '—';
  }
  if (phone.includes('*') || phone.length <= 4) {
    return phone;
  }
  const prefix = phone.startsWith('+91') ? 3 : 1;
  return `${phone.slice(0, prefix)}${'*'.repeat(phone.length - prefix - 4)}${phone.slice(-4)}`;
}

/**
 * Merges this patient, a duplicate registration, into the record that stays (needs `patients.merge`): its visits,
 * tokens, consents, documents and identity checks move to the surviving record, and this one remains only as a pointer
 * to it. Both records are shown side by side before the merge, which needs a reason and cannot be undone. On success
 * the surviving record opens.
 */
export function MergePatientDialog({
  patient,
  open,
  onClose,
}: {
  patient: PatientDetail;
  open: boolean;
  onClose: () => void;
}) {
  // Mounted only while open, so every merge starts with nothing chosen.
  return open ? <MergeForm patient={patient} onClose={onClose} /> : null;
}

function MergeForm({ patient, onClose }: { patient: PatientDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [target, setTarget] = useState<PickedPatient | null>(null);
  const [reason, setReason] = useState('');
  const [understood, setUnderstood] = useState(false);
  const self = target?.id === patient.id;
  const surviving = useQuery({ ...patientQueries.detail(target?.id ?? ''), enabled: Boolean(target) && !self });
  const survivor = surviving.data?.id === target?.id ? surviving.data : undefined;

  const merge = useMutation({
    mutationFn: (into: PatientDetail) =>
      patientApi.merge(patient.id, { intoPatientId: into.id, reason: reason.trim() }),
    onSuccess: (_, into) => {
      notify.success('Records merged', `${patient.uhid} was merged into ${into.uhid}.`);
      // Both records, every list they appear in and their visits change.
      void queryClient.invalidateQueries({ queryKey: patientKeys.all });
      // Saved: closes, and leaves for the surviving record, without asking about the reason typed.
      guard.close();
      void navigate({ to: '/patients/$patientId', params: { patientId: into.id } });
    },
  });

  // A chosen record or a typed reason is not thrown away without asking (SRS UX-08).
  const guard = useUnsavedChanges({ dirty: target !== null || reason !== '', busy: merge.isPending, onClose });

  const pickError = self
    ? 'Choose a different record to merge into.'
    : survivor?.mergedIntoPatientId
      ? 'This record has itself been merged. Choose the record it was merged into.'
      : undefined;
  const reasonValid = reason.trim().length >= MERGE_REASON_LIMITS.min;
  const ready = Boolean(survivor) && !pickError && reasonValid && understood && !merge.isPending;

  return (
    <>
      <Dialog open onOpenChange={guard.onOpenChange} modalType="alert">
        <DialogSurface style={{ maxWidth: 720 }}>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              if (ready && survivor) {
                merge.mutate(survivor);
              }
            }}
          >
            <DialogBody>
              <DialogTitle>Merge duplicate record</DialogTitle>
              <DialogContent className={styles.content}>
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>This record will be merged into the one you choose</MessageBarTitle>
                    Its visits, queue tokens, consents, documents and identity checks move to the surviving record, and{' '}
                    {patient.uhid} stays only as a pointer to it. A merge cannot be undone.
                  </MessageBarBody>
                </MessageBar>
                <PatientPicker
                  label="Surviving record"
                  required
                  value={target}
                  onChange={(picked) => {
                    setTarget(picked);
                    setUnderstood(false);
                  }}
                  validationMessage={pickError}
                  hint="The registration that stays: usually the older one, with the patient's visits and bills."
                  autoFocus
                />
                <div className={styles.compare}>
                  <RecordCard title="Duplicate: merged away" patient={patient} />
                  {survivor && !pickError ? (
                    <RecordCard title="Surviving record: kept" patient={survivor} surviving />
                  ) : (
                    <div className={styles.card}>
                      <Caption1 className={styles.label}>Surviving record: kept</Caption1>
                      {target && !self && surviving.isLoading ? (
                        <Spinner size="tiny" label="Loading the record…" />
                      ) : surviving.error && !self ? (
                        <Text>
                          {isApiError(surviving.error) ? surviving.error.userMessage : 'Could not open the record.'}
                        </Text>
                      ) : (
                        <Text>Find the record that stays above.</Text>
                      )}
                    </div>
                  )}
                </div>
                <Field
                  label="Reason"
                  required
                  hint={`Why these are the same person, for the audit trail. At least ${MERGE_REASON_LIMITS.min} characters.`}
                >
                  <Textarea
                    value={reason}
                    onChange={(_, data) => setReason(data.value)}
                    placeholder="For example: registered twice; same name, date of birth and Aadhaar"
                    maxLength={MERGE_REASON_LIMITS.max}
                    rows={3}
                    resize="vertical"
                  />
                </Field>
                {survivor && !pickError && (
                  <Checkbox
                    checked={understood}
                    onChange={(_, data) => setUnderstood(Boolean(data.checked))}
                    label={`Merge ${patient.uhid} into ${survivor.uhid}. I understand this cannot be undone.`}
                  />
                )}
                {merge.error !== null && (
                  <MessageBar intent="error" layout="multiline">
                    <MessageBarBody>
                      {isApiError(merge.error) ? merge.error.userMessage : 'The merge failed. Please try again.'}
                    </MessageBarBody>
                  </MessageBar>
                )}
              </DialogContent>
              <DialogActions>
                <Button onClick={guard.requestClose} disabled={merge.isPending}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={!ready}
                  // While the request runs the button stays focusable (aria-disabled): a disabled button drops focus out of
                  // the open dialog, and Fluent then returns it to the button that opened the dialog, behind it.
                  disabledFocusable={merge.isPending}
                  icon={merge.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
                  style={ready ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
                >
                  {survivor && !pickError ? `Merge into ${survivor.uhid}` : 'Merge'}
                </Button>
              </DialogActions>
            </DialogBody>
          </form>
        </DialogSurface>
      </Dialog>
      {guard.dialogs}
    </>
  );
}

function RecordCard({ title, patient, surviving }: { title: string; patient: PatientDetail; surviving?: boolean }) {
  const styles = useStyles();
  return (
    <section className={surviving ? `${styles.card} ${styles.surviving}` : styles.card} aria-label={title}>
      <Caption1 className={styles.label}>{title}</Caption1>
      <Text weight="semibold">{patient.fullName}</Text>
      <Text font="monospace">{patient.uhid}</Text>
      <Text>
        {patient.age ?? '—'} / {patient.gender.charAt(0)}
      </Text>
      <Text>{maskPhone(patient.phone)}</Text>
    </section>
  );
}
