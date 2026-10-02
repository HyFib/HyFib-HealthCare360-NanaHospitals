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
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDate, formatDateTime, isApiError } from '@hc360/core';
import {
  ChoiceField,
  DataTable,
  EmptyState,
  FormGrid,
  StatusBadge,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { usePermission } from '@/lib/permissions';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { consentApi, patientKeys, patientQueries, type CaptureConsentRequest, type ConsentDto } from './api';
import {
  CONSENT_LIMITS,
  consentFormDefaults,
  consentFormSchema,
  consentServerErrors,
  toCaptureRequest,
  type ConsentFormValues,
} from './consentForm';
import {
  CONSENT_GROUPS,
  consentStatus,
  consentStatusBadge,
  consentTypeHint,
  consentTypeLabel,
  givenSummary,
  otherGivenConsents,
  otherGivenNote,
  recordedNotice,
  signedByText,
  withdrawalEffect,
  withdrawnNotice,
  withdrawnText,
} from './consents';

const useStyles = makeStyles({
  toolbar: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', marginBottom: '12px' },
  // Table cells keep to one line by default; consent details wrap instead, so no note or reason is cut off.
  cell: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    paddingBlock: '6px',
    whiteSpace: 'normal',
    overflowWrap: 'anywhere',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  details: { display: 'flex', flexDirection: 'column', gap: '8px' },
});

interface Withdrawal {
  consent: ConsentDto;
  /** The patient's other consents of the same type still given when the dialog opened. */
  others: ConsentDto[];
  open: boolean;
}

/**
 * A patient's consents (EHR-06), on the profile and in the clinical chart: every consent given or refused, newest
 * first, with who recorded it. Staff with `patients.consents` record new ones and withdraw those that stand; a
 * withdrawn consent stays in the list as history.
 */
export function ConsentsPanel({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRead = usePermission('patients.read');
  const canManage = usePermission('patients.consents');
  const consents = useQuery({ ...patientQueries.consents(patientId), enabled: canRead });
  const [recording, setRecording] = useState(false);
  const [withdrawal, setWithdrawal] = useState<Withdrawal | null>(null);
  // The consent is kept while the dialog closes, so its text does not change during the animation.
  const closeWithdrawal = () => setWithdrawal((current) => current && { ...current, open: false });

  const revoke = useMutation({
    mutationFn: ({ consent, reason }: { consent: ConsentDto; reason: string }) => consentApi.revoke(consent.id, reason),
    onSuccess: (_, { consent }) => {
      const notice = withdrawnNotice(consent.type);
      notify.success(notice.title, notice.body);
      closeWithdrawal();
      void queryClient.invalidateQueries({ queryKey: patientKeys.consents(patientId) });
    },
    onError: (error) => {
      // Withdrawn by someone else meanwhile, or gone: the list behind the message shows it as it is now.
      if (isApiError(error) && [404, 409, 422].includes(error.status)) {
        void queryClient.invalidateQueries({ queryKey: patientKeys.consents(patientId) });
      }
    },
  });

  const startWithdrawal = (consent: ConsentDto) => {
    revoke.reset();
    setWithdrawal({ consent, others: otherGivenConsents(consents.data ?? [], consent), open: true });
  };

  if (!canRead) {
    return (
      <EmptyState
        title="Consents are not available to you"
        description="Ask your administrator for access to patient records."
        icon={<ShieldKeyholeRegular />}
      />
    );
  }

  if (isApiError(consents.error) && consents.error.code === 'patient.restricted') {
    // Refreshing this patient's data brings up the page's own emergency-access request (profile or chart).
    return (
      <RestrictedNotice
        onRequestAccess={() =>
          void queryClient.invalidateQueries({ predicate: (query) => query.queryKey.includes(patientId) })
        }
      />
    );
  }

  return (
    <>
      {canManage && (
        <div className={styles.toolbar}>
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setRecording(true)}>
            Record consent
          </Button>
        </div>
      )}
      <DataTable
        ariaLabel="Consents"
        actionsWidth={112}
        rows={consents.data}
        loading={consents.isLoading}
        error={consents.error}
        onRetry={() => void consents.refetch()}
        getRowId={(c) => c.id}
        size="small"
        columns={[
          {
            key: 'type',
            header: 'Consent',
            render: (c) => (
              <span className={styles.cell}>
                <Text weight="semibold">{consentTypeLabel(c.type)}</Text>
                {c.notes && <Caption1 className={styles.muted}>{c.notes}</Caption1>}
              </span>
            ),
          },
          { key: 'decision', header: 'Decision', render: (c) => <Decision consent={c} /> },
          {
            key: 'recorded',
            header: 'Recorded',
            render: (c) => (
              <span className={styles.cell}>
                <Text>{formatDateTime(c.capturedAt)}</Text>
                {c.capturedByName && <Caption1 className={styles.muted}>by {c.capturedByName}</Caption1>}
              </span>
            ),
          },
          {
            key: 'signed',
            header: 'Signed by',
            priority: 'low',
            render: (c) => (
              <span className={styles.cell}>
                <Text>{signedByText(c)}</Text>
                {c.witnessName && <Caption1 className={styles.muted}>Witness: {c.witnessName}</Caption1>}
              </span>
            ),
          },
          { key: 'version', header: 'Version', priority: 'low', width: 80, render: (c) => c.consentVersion },
        ]}
        rowActions={
          canManage
            ? (c) =>
                c.isActive ? (
                  <Button
                    size="small"
                    aria-label={`Withdraw consent: ${consentTypeLabel(c.type)}, given ${formatDate(c.capturedAt)}`}
                    onClick={() => startWithdrawal(c)}
                  >
                    Withdraw
                  </Button>
                ) : null
            : undefined
        }
        emptyTitle="No consents recorded"
        emptyDescription={
          canManage
            ? 'When the patient signs or refuses a consent form, record it here.'
            : 'Consents the patient gives or refuses appear here.'
        }
      />

      <RecordConsentDialog patientId={patientId} open={recording} onClose={() => setRecording(false)} />
      <ReasonDialog
        open={withdrawal?.open ?? false}
        title={withdrawal ? `Withdraw consent: ${consentTypeLabel(withdrawal.consent.type)}` : 'Withdraw consent'}
        description={withdrawal && <WithdrawalDetails consent={withdrawal.consent} others={withdrawal.others} />}
        placeholder="For example: the patient asked at the front desk"
        hint="Kept with the consent, with your name and the time."
        confirmLabel="Withdraw consent"
        destructive
        maxLength={CONSENT_LIMITS.reason}
        busy={revoke.isPending}
        error={revoke.error}
        onConfirm={(reason) => withdrawal && revoke.mutate({ consent: withdrawal.consent, reason })}
        onCancel={closeWithdrawal}
      />
    </>
  );
}

/** Given, Refused or Withdrawn; a withdrawn consent also says when, by whom and why. */
function Decision({ consent }: { consent: ConsentDto }) {
  const styles = useStyles();
  const badge = consentStatusBadge[consentStatus(consent)];
  const withdrawn = withdrawnText(consent);
  return (
    <span className={styles.cell}>
      <StatusBadge status={badge.status} label={badge.label} />
      {withdrawn && <Caption1>{withdrawn}</Caption1>}
      {consent.revokedReason && <Caption1 className={styles.muted}>Reason: {consent.revokedReason}</Caption1>}
    </span>
  );
}

function WithdrawalDetails({ consent, others }: { consent: ConsentDto; others: ConsentDto[] }) {
  const styles = useStyles();
  const note = otherGivenNote(consent.type, others);
  return (
    <div className={styles.details}>
      <Text block>{givenSummary(consent)}</Text>
      <Text block>
        <Text weight="semibold">What happens: </Text>
        {withdrawalEffect(consent.type).effect}
      </Text>
      {note && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>{note}</MessageBarBody>
        </MessageBar>
      )}
      <Text block className={styles.muted}>
        The consent stays in the list, marked withdrawn, with your name, the time and the reason. If the patient agrees
        again later, record a new consent.
      </Text>
    </div>
  );
}

function RestrictedNotice({ onRequestAccess }: { onRequestAccess: () => void }) {
  return (
    <MessageBar intent="warning" layout="multiline" icon={<ShieldKeyholeRegular />}>
      <MessageBarBody>
        <MessageBarTitle>Restricted record</MessageBarTitle>
        Only staff who may open this patient’s record can see their consents. Use emergency access only when it is
        clinically necessary: it is time-limited, and every screen you open is recorded.
      </MessageBarBody>
      <MessageBarActions>
        <Button onClick={onRequestAccess}>Request emergency access</Button>
      </MessageBarActions>
    </MessageBar>
  );
}

function RecordConsentDialog({ patientId, open, onClose }: { patientId: string; open: boolean; onClose: () => void }) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        {open && <RecordConsentForm patientId={patientId} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

/** The record-consent form. The dialog mounts it each time it opens, so it always starts empty. */
export function RecordConsentForm({ patientId, onClose }: { patientId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [problems, setProblems] = useState<string[]>([]);
  const form = useForm<ConsentFormValues>({
    resolver: zodResolver(consentFormSchema),
    mode: 'onTouched',
    defaultValues: consentFormDefaults(),
  });
  const signer = useWatch({ control: form.control, name: 'signer' });

  const save = useMutation({
    mutationFn: (request: CaptureConsentRequest) => consentApi.capture(patientId, request),
    onSuccess: (_, request) => {
      const notice = recordedNotice(request.type, request.granted);
      notify.success(notice.title, notice.body);
      void queryClient.invalidateQueries({ queryKey: patientKeys.consents(patientId) });
      onClose();
    },
    onError: (error) => {
      // A field the server refused keeps Save disabled until it is changed; the first one gets the focus.
      const { fields, other } = consentServerErrors(error);
      fields.forEach(([field, message], index) =>
        form.setError(field, { type: 'server', message }, { shouldFocus: index === 0 }),
      );
      setProblems(other);
    },
  });

  return (
    <form
      onSubmit={form.handleSubmit((values) => {
        setProblems([]);
        save.mutate(toCaptureRequest(values));
      })}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Record consent</DialogTitle>
        <DialogContent className={styles.form}>
          <Controller
            control={form.control}
            name="type"
            render={({ field, fieldState }) => (
              <Field
                label="Consent"
                required
                hint={consentTypeHint(field.value)}
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <Select
                  name={field.name}
                  ref={field.ref}
                  onBlur={field.onBlur}
                  value={field.value ?? ''}
                  onChange={(_, data) => field.onChange(data.value === '' ? null : data.value)}
                >
                  <option value="">Choose the consent</option>
                  {CONSENT_GROUPS.map((group) => (
                    <optgroup key={group.label} label={group.label}>
                      {group.types.map((type) => (
                        <option key={type} value={type}>
                          {consentTypeLabel(type)}
                        </option>
                      ))}
                    </optgroup>
                  ))}
                </Select>
              </Field>
            )}
          />
          <ChoiceField
            control={form.control}
            name="decision"
            label="Decision"
            options={[
              { value: 'given', label: 'Given' },
              { value: 'refused', label: 'Refused' },
            ]}
          />
          <ChoiceField
            control={form.control}
            name="signer"
            label="Signed by"
            options={[
              { value: 'patient', label: 'The patient' },
              { value: 'other', label: 'Someone on their behalf' },
            ]}
          />
          {signer === 'other' && (
            <FormGrid columns={2} minColumnWidth={200}>
              <TextField
                control={form.control}
                name="signedByName"
                label="Name of the person signing"
                required
                maxLength={CONSENT_LIMITS.signerName}
                autoComplete="off"
              />
              <TextField
                control={form.control}
                name="signedByRelation"
                label="Relation to the patient"
                required
                maxLength={CONSENT_LIMITS.relation}
                placeholder="For example: father, wife, guardian"
                autoComplete="off"
              />
            </FormGrid>
          )}
          <FormGrid columns={2} minColumnWidth={200}>
            <TextField
              control={form.control}
              name="witnessName"
              label="Witness"
              maxLength={CONSENT_LIMITS.witnessName}
              autoComplete="off"
            />
            <TextField
              control={form.control}
              name="consentVersion"
              label="Form version"
              hint="As printed on the form. Leave blank for 1.0."
              maxLength={CONSENT_LIMITS.version}
              autoComplete="off"
            />
          </FormGrid>
          <TextAreaField
            control={form.control}
            name="notes"
            label="Notes"
            rows={3}
            maxLength={CONSENT_LIMITS.notes}
            hint="For example, what was explained to the patient, and in which language."
          />
          {problems.length > 0 && (
            <MessageBar intent="error" layout="multiline" politeness="assertive">
              <MessageBarBody>
                <MessageBarTitle>The consent was not saved</MessageBarTitle>
                {problems.join(' ')}
              </MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={!form.formState.isValid || save.isPending}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            Save
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
