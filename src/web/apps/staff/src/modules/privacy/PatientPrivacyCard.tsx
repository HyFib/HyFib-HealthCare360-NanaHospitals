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
  Input,
  makeStyles,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, EditRegular, PersonNoteRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatPhone, isApiError } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  ConfirmDialog,
  FormGrid,
  Section,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { useId, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { usePermission } from '@/lib/permissions';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import type { PatientDetail } from '@/modules/patients/api';
import { privacyApi, privacyKeys, privacyQueries, type NomineeDto } from './api';
import { PRIVACY_PERMISSIONS as P } from './labels';
import { nomineeDefaults, nomineeSchema, RELATIONSHIP_SUGGESTIONS, toSaveNominee, type NomineeValues } from './nominee';
import { nomineeSourceText } from './rules';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '10px' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  link: { color: tokens.colorBrandForegroundLink },
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

type CardPatient = Pick<PatientDetail, 'id' | 'fullName' | 'isDeceased' | 'mergedIntoPatientId'>;

/**
 * The patient record's privacy card (DPDP Act): the nominee who can act for the patient, their open privacy requests,
 * and logging a new one. Shown only to staff who log or read privacy requests.
 */
export function PatientPrivacyCard({ patient }: { patient: CardPatient }) {
  const canLog = usePermission(P.requestsLog);
  const canRead = usePermission(P.requestsRead);
  if (!canLog && !canRead) {
    return null;
  }
  return <PrivacyCard patient={patient} canLog={canLog} canRead={canRead} />;
}

function PrivacyCard({ patient, canLog, canRead }: { patient: CardPatient; canLog: boolean; canRead: boolean }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [removing, setRemoving] = useState(false);
  const nominee = useQuery(privacyQueries.nominee(patient.id));
  const open = useQuery({ ...privacyQueries.openForPatient(patient.id), enabled: canRead });
  const remove = useMutation({
    mutationFn: () => privacyApi.removeNominee(patient.id),
    onSuccess: () => {
      notify.success('Nominee removed');
      setRemoving(false);
      void queryClient.invalidateQueries({ queryKey: privacyKeys.nominee(patient.id) });
      void queryClient.invalidateQueries({ queryKey: ['privacy', 'summary'] });
    },
    onError: (error) => notify.error(error, 'Could not remove the nominee'),
  });

  // A restricted record this user may not open: say nothing rather than show an error on the record.
  if (isApiError(nominee.error) && nominee.error.status === 403) {
    return null;
  }

  const merged = Boolean(patient.mergedIntoPatientId);
  const canChange = canLog && !merged && !patient.isDeceased;
  const n = nominee.data?.nominee ?? null;
  const openCount = open.data?.totalCount;

  return (
    <Section
      title="Privacy"
      description="The nominee who can act for the patient, and their requests about personal data (DPDP Act)."
      actions={
        canLog &&
        !merged && (
          <Button
            icon={<PersonNoteRegular />}
            onClick={() => void navigate({ to: '/privacy/requests/new', search: { patientId: patient.id } })}
          >
            Log a request for this patient
          </Button>
        )
      }
    >
      <div className={styles.stack}>
        <DetailGrid minColumnWidth={220}>
          <Detail label="Nominee">
            {nominee.isLoading ? (
              <Spinner size="extra-tiny" label="Loading…" labelPosition="after" />
            ) : nominee.error ? (
              <span className={styles.muted}>
                Could not be loaded.{' '}
                <Button appearance="transparent" size="small" onClick={() => void nominee.refetch()}>
                  Try again
                </Button>
              </span>
            ) : n ? (
              `${n.name} (${n.relationship})`
            ) : (
              'None named'
            )}
          </Detail>
          {n && (
            <Detail label="Nominee’s contact">
              {[n.phone ? formatPhone(n.phone) : null, n.email].filter(Boolean).join(' · ')}
            </Detail>
          )}
          {canRead && (
            <Detail label="Privacy requests">
              {openCount === undefined ? (
                open.isLoading ? (
                  '…'
                ) : null
              ) : (
                <Link
                  to="/privacy/requests"
                  search={{ patientId: patient.id, view: openCount > 0 ? undefined : 'all' }}
                  className={styles.link}
                >
                  {openCount > 0 ? `${openCount.toLocaleString('en-IN')} open` : 'None open'}
                </Link>
              )}
            </Detail>
          )}
        </DetailGrid>
        {n && <Caption1 className={styles.muted}>{nomineeSourceText(n.recordedVia, n.updatedAt)}</Caption1>}
        {patient.isDeceased && n && (
          <Caption1 className={styles.muted}>The patient has died: the nominee on record can act for them.</Caption1>
        )}
        {canChange && !nominee.isLoading && !nominee.error && (
          <div className={styles.actions}>
            {n ? (
              <>
                <Button size="small" icon={<EditRegular />} onClick={() => setEditing(true)}>
                  Replace the nominee
                </Button>
                <Button size="small" icon={<DeleteRegular />} onClick={() => setRemoving(true)}>
                  Remove
                </Button>
              </>
            ) : (
              <Button size="small" icon={<AddRegular />} onClick={() => setEditing(true)}>
                Add a nominee
              </Button>
            )}
          </div>
        )}
      </div>

      <NomineeDialog open={editing} patientId={patient.id} nominee={n} onClose={() => setEditing(false)} />
      <ConfirmDialog
        open={removing}
        title="Remove the nominee?"
        confirmLabel="Remove"
        destructive
        busy={remove.isPending}
        onConfirm={() => remove.mutate()}
        onCancel={() => setRemoving(false)}
      >
        <Text>
          {n?.name ?? 'The nominee'} will no longer be able to act for {patient.fullName}. The patient can name a
          nominee again at the desk or in the patient portal.
        </Text>
      </ConfirmDialog>
    </Section>
  );
}

/** Records the nominee the patient names, or replaces the one on record. */
function NomineeDialog({
  open,
  patientId,
  nominee,
  onClose,
}: {
  open: boolean;
  patientId: string;
  nominee: NomineeDto | null;
  onClose: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 580 }}>
        {open && (
          <CloseRequestScope value={closing}>
            <NomineeBody patientId={patientId} nominee={nominee} busy={busy} setBusy={setBusy} onClose={onClose} />
          </CloseRequestScope>
        )}
      </DialogSurface>
    </Dialog>
  );
}

function NomineeBody({
  patientId,
  nominee,
  busy,
  setBusy,
  onClose,
}: {
  patientId: string;
  nominee: NomineeDto | null;
  busy: boolean;
  setBusy: (busy: boolean) => void;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const listId = useId();
  const form = useForm<NomineeValues>({
    resolver: zodResolver(nomineeSchema),
    defaultValues: nomineeDefaults(nominee),
  });

  const submit = async (values: NomineeValues) => {
    setBusy(true);
    try {
      await privacyApi.saveNominee(patientId, toSaveNominee(values));
      notify.success(nominee ? 'Nominee replaced' : 'Nominee recorded');
      void queryClient.invalidateQueries({ queryKey: privacyKeys.nominee(patientId) });
      void queryClient.invalidateQueries({ queryKey: ['privacy', 'summary'] });
      onClose();
    } catch (error) {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the nominee');
      }
    } finally {
      setBusy(false);
    }
  };

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy, onClose });

  return (
    <form onSubmit={form.handleSubmit(submit)} noValidate>
      <DialogBody>
        <DialogTitle>{nominee ? 'Replace the nominee' : 'Add a nominee'}</DialogTitle>
        <DialogContent className={styles.content}>
          <Text>
            The nominee can act for the patient under the DPDP Act if the patient dies or cannot act for themselves.
            Record the person the patient names.
          </Text>
          {nominee && (
            <Caption1 className={styles.muted}>
              This replaces {nominee.name} ({nominee.relationship}).
            </Caption1>
          )}
          <TextField
            control={form.control}
            name="name"
            label="Name"
            required
            autoFocus
            maxLength={200}
            autoComplete="off"
          />
          <Controller
            control={form.control}
            name="relationship"
            render={({ field, fieldState }) => (
              <Field
                label="Relationship to the patient"
                required
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <Input
                  value={field.value}
                  onChange={(_, d) => field.onChange(d.value)}
                  onBlur={field.onBlur}
                  ref={field.ref}
                  maxLength={64}
                  list={listId}
                  placeholder="For example: Spouse"
                  autoComplete="off"
                />
              </Field>
            )}
          />
          <datalist id={listId}>
            {RELATIONSHIP_SUGGESTIONS.map((r) => (
              <option key={r} value={r} />
            ))}
          </datalist>
          <FormGrid columns={2} minColumnWidth={200}>
            <TextField
              control={form.control}
              name="phone"
              label="Mobile number"
              type="tel"
              maxLength={20}
              autoComplete="off"
              hint="10 digits"
            />
            <TextField
              control={form.control}
              name="email"
              label="E-mail address"
              type="email"
              maxLength={200}
              autoComplete="off"
            />
          </FormGrid>
          <Caption1 className={styles.muted}>
            A mobile number or an e-mail address is needed, so the hospital can reach the nominee.
          </Caption1>
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            Save nominee
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
