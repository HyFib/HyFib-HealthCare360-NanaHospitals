import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, AlertUrgentRegular, CheckmarkCircleRegular, ShieldErrorRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDate, formatDateOnly, isApiError, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  ConfirmDialog,
  DataTable,
  DateField,
  FormGrid,
  SelectField,
  StatusBadge,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { ignoreEscapeWhile } from '@/lib/dialogFocus';
import { usePermission } from '@/lib/permissions';
import { canRecordNoKnownAllergies, noKnownAllergiesRefusal, recordedByText } from './allergyStatus';
import {
  ALERT_TYPES,
  alertTypeLabel,
  ALLERGY_SEVERITIES,
  ALLERGY_TYPES,
  emrApi,
  emrKeys,
  emrQueries,
  type AllergyDto,
  type PatientAlertDto,
} from './api';
import { ReasonDialog } from './ui';

const useStyles = makeStyles({
  toolbar: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', marginBottom: '12px' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  statement: { marginBottom: '12px' },
});

function severityTone(severity: string): 'danger' | 'warning' | 'informative' {
  return severity === 'Anaphylaxis' || severity === 'Severe' || severity === 'Critical'
    ? 'danger'
    : severity === 'Moderate' || severity === 'Warning'
      ? 'warning'
      : 'informative';
}

// ------------------------------------------------------------------------------------------------ allergies

const recordAllergySchema = z.object({
  allergen: z.string().trim().min(1, 'Enter the allergen.').max(200),
  type: z.enum(['Drug', 'Food', 'Environmental', 'Other']),
  severity: z.enum(['Mild', 'Moderate', 'Severe', 'Anaphylaxis']),
  reaction: z.string().max(300),
  onsetDate: z.string().nullable(),
  notes: z.string().max(500),
});

type RecordAllergyValues = z.infer<typeof recordAllergySchema>;

const updateAllergySchema = z
  .object({
    severity: z.enum(['Mild', 'Moderate', 'Severe', 'Anaphylaxis']),
    status: z.enum(['Active', 'Inactive', 'Resolved', 'EnteredInError']),
    reaction: z.string().max(300),
    statusReason: z.string().max(300),
    notes: z.string().max(500),
  })
  .superRefine((values, ctx) => {
    if (values.status !== 'Active' && !values.statusReason.trim()) {
      ctx.addIssue({
        code: 'custom',
        path: ['statusReason'],
        message: 'Give a reason: this stops the allergy from appearing in prescribing checks.',
      });
    }
  });

type UpdateAllergyValues = z.infer<typeof updateAllergySchema>;

export function RecordAllergyDialog({
  patientId,
  open,
  onClose,
  onOpenExisting,
}: {
  patientId: string;
  open: boolean;
  onClose: () => void;
  onOpenExisting?: (allergyId: string) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        {open && <RecordAllergyForm patientId={patientId} onClose={onClose} onOpenExisting={onOpenExisting} />}
      </DialogSurface>
    </Dialog>
  );
}

function RecordAllergyForm({
  patientId,
  onClose,
  onOpenExisting,
}: {
  patientId: string;
  onClose: () => void;
  onOpenExisting?: (allergyId: string) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [duplicateId, setDuplicateId] = useState<string | null>(null);
  const form = useForm<RecordAllergyValues>({
    resolver: zodResolver(recordAllergySchema),
    defaultValues: { allergen: '', type: 'Drug', severity: 'Moderate', reaction: '', onsetDate: null, notes: '' },
  });

  const save = useMutation({
    mutationFn: (values: RecordAllergyValues) =>
      emrApi.recordAllergy(patientId, {
        allergen: values.allergen.trim(),
        type: values.type,
        severity: values.severity,
        reaction: values.reaction.trim() || null,
        onsetDate: values.onsetDate,
        notes: values.notes.trim() || null,
      }),
    onSuccess: (allergy) => {
      notify.success('Allergy recorded', `${allergy.allergen} now appears on the banner and in prescribing checks.`);
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'allergy.duplicate') {
        const data = error.data as { allergyId?: string } | undefined;
        setDuplicateId(data?.allergyId ?? null);
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not record the allergy');
      }
    },
  });

  return (
    <form
      onSubmit={form.handleSubmit((values) => !save.isPending && save.mutate(values))}
      onKeyDown={ignoreEscapeWhile(save.isPending)}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Record allergy</DialogTitle>
        <DialogContent className={styles.form}>
          {duplicateId !== null && (
            <MessageBar intent="warning">
              <MessageBarBody>This allergy is already recorded. Update the existing entry instead.</MessageBarBody>
              {onOpenExisting && duplicateId && (
                <MessageBarActions>
                  <Button onClick={() => onOpenExisting(duplicateId)}>Open it</Button>
                </MessageBarActions>
              )}
            </MessageBar>
          )}
          <TextField
            control={form.control}
            name="allergen"
            label="Allergen"
            required
            autoFocus
            maxLength={200}
            hint="Use the generic drug or class for medicines (e.g. Amoxicillin, Penicillins, Sulfa drugs, NSAIDs) so prescribing checks catch it."
          />
          <ChoiceField
            control={form.control}
            name="type"
            label="Type"
            options={ALLERGY_TYPES.map((t) => ({ value: t, label: t }))}
          />
          <ChoiceField
            control={form.control}
            name="severity"
            label="Severity"
            options={ALLERGY_SEVERITIES.map((s) => ({ value: s, label: s }))}
          />
          <FormGrid columns={2}>
            <TextField
              control={form.control}
              name="reaction"
              label="Reaction"
              placeholder="e.g. rash, breathlessness"
              maxLength={300}
            />
            <DateField control={form.control} name="onsetDate" label="Since" max={todayIso()} />
          </FormGrid>
          <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={500} />
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            // Keeps focus while saving (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            Record allergy
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}

function UpdateAllergyDialog({
  allergy,
  patientId,
  onClose,
}: {
  allergy: AllergyDto | null;
  patientId: string;
  onClose: () => void;
}) {
  return (
    <Dialog open={allergy !== null} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        {allergy && <UpdateAllergyForm key={allergy.id} allergy={allergy} patientId={patientId} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function UpdateAllergyForm({
  allergy,
  patientId,
  onClose,
}: {
  allergy: AllergyDto;
  patientId: string;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const voided = allergy.status === 'EnteredInError';
  const form = useForm<UpdateAllergyValues>({
    resolver: zodResolver(updateAllergySchema),
    defaultValues: {
      severity: allergy.severity,
      status: allergy.status,
      reaction: allergy.reaction ?? '',
      statusReason: allergy.statusReason ?? '',
      notes: allergy.notes ?? '',
    },
  });
  const status = useWatch({ control: form.control, name: 'status' });

  const save = useMutation({
    mutationFn: (values: UpdateAllergyValues) =>
      emrApi.updateAllergy(allergy.id, {
        reaction: values.reaction.trim() || null,
        severity: values.severity,
        status: values.status,
        statusReason: values.statusReason.trim() || null,
        notes: values.notes.trim() || null,
        version: allergy.version,
      }),
    onSuccess: () => {
      notify.success('Allergy updated');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        notify.error(error, 'Someone else changed this allergy');
        void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
        onClose();
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not update the allergy');
      }
    },
  });

  return (
    <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate>
      <DialogBody>
        <DialogTitle>
          {allergy.allergen}
          <Caption1 block className={styles.muted}>
            {allergy.type} allergy · recorded {formatDate(allergy.recordedAt)}
          </Caption1>
        </DialogTitle>
        <DialogContent className={styles.form}>
          {voided ? (
            <MessageBar intent="info">
              <MessageBarBody>
                This entry was marked as entered in error and cannot be changed. Record a new allergy if needed.
              </MessageBarBody>
            </MessageBar>
          ) : (
            <>
              <ChoiceField
                control={form.control}
                name="severity"
                label="Severity"
                options={ALLERGY_SEVERITIES.map((s) => ({ value: s, label: s }))}
              />
              <TextField control={form.control} name="reaction" label="Reaction" maxLength={300} />
              <SelectField
                control={form.control}
                name="status"
                label="Status"
                options={[
                  { value: 'Active', label: 'Active' },
                  { value: 'Inactive', label: 'Inactive (no longer relevant)' },
                  { value: 'Resolved', label: 'Resolved (tolerates it now)' },
                  { value: 'EnteredInError', label: 'Entered in error' },
                ]}
              />
              {status !== 'Active' && (
                <>
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      An allergy that is not active is no longer checked when prescribing.
                    </MessageBarBody>
                  </MessageBar>
                  <TextField control={form.control} name="statusReason" label="Reason" required maxLength={300} />
                </>
              )}
              <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={500} />
            </>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={save.isPending}>
            {voided ? 'Close' : 'Cancel'}
          </Button>
          {!voided && (
            <Button
              type="submit"
              appearance="primary"
              disabled={save.isPending}
              icon={save.isPending ? <Spinner size="tiny" /> : undefined}
            >
              Save
            </Button>
          )}
        </DialogActions>
      </DialogBody>
    </form>
  );
}

export function AllergiesTab({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('clinical.allergies.manage');
  const [showAll, setShowAll] = useState(false);
  const [recording, setRecording] = useState(false);
  const [confirmingNone, setConfirmingNone] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const allergies = useQuery(emrQueries.allergies(patientId, showAll));
  const status = useQuery(emrQueries.allergyStatus(patientId));
  const all = useQuery({ ...emrQueries.allergies(patientId, true), enabled: editingId !== null });
  const editing = editingId ? ((all.data ?? allergies.data)?.find((a) => a.id === editingId) ?? null) : null;
  const activeListed = (allergies.data ?? []).filter((a) => a.status === 'Active').length;

  const recordNone = useMutation({
    mutationFn: () => emrApi.recordNoKnownAllergies(patientId),
    onSuccess: (recorded) => {
      queryClient.setQueryData(emrKeys.allergyStatus(patientId), recorded);
      void queryClient.invalidateQueries({ queryKey: emrKeys.summary(patientId) });
      setConfirmingNone(false);
      notify.success('No known allergies recorded', 'The banner now shows it with your name and the time.');
    },
    onError: (error) => {
      if (noKnownAllergiesRefusal(error)) {
        // An allergy was recorded since this list loaded: show it and withdraw the action.
        void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      }
    },
  });
  const closeNone = () => {
    setConfirmingNone(false);
    recordNone.reset();
  };

  return (
    <>
      <div className={styles.toolbar}>
        {canManage && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setRecording(true)}>
            Record allergy
          </Button>
        )}
        {canRecordNoKnownAllergies(status.data?.status, canManage, activeListed) && (
          <Button icon={<CheckmarkCircleRegular />} onClick={() => setConfirmingNone(true)}>
            Record no known allergies
          </Button>
        )}
        <Switch checked={showAll} onChange={(_, d) => setShowAll(d.checked)} label="Show inactive and resolved" />
      </div>
      {status.data?.status === 'NoKnownAllergies' && (
        <MessageBar intent="success" className={styles.statement}>
          <MessageBarBody>
            <MessageBarTitle>No known allergies</MessageBarTitle>
            {recordedByText(status.data)} Recording an allergy replaces this statement.
          </MessageBarBody>
        </MessageBar>
      )}
      <DataTable
        ariaLabel="Allergies"
        rows={allergies.data}
        loading={allergies.isLoading}
        error={allergies.error}
        onRetry={() => void allergies.refetch()}
        getRowId={(a) => a.id}
        onRowClick={canManage ? (a) => setEditingId(a.id) : undefined}
        size="small"
        columns={[
          { key: 'allergen', header: 'Allergen', render: (a) => <Text weight="semibold">{a.allergen}</Text> },
          { key: 'type', header: 'Type', render: (a) => a.type },
          {
            key: 'severity',
            header: 'Severity',
            render: (a) => <StatusBadge status={a.severity} tone={severityTone(a.severity)} />,
          },
          { key: 'reaction', header: 'Reaction', render: (a) => a.reaction ?? '—' },
          {
            key: 'onset',
            header: 'Since',
            priority: 'low',
            render: (a) => (a.onsetDate ? formatDateOnly(a.onsetDate) : '—'),
          },
          {
            key: 'status',
            header: 'Status',
            render: (a) => (
              <StatusBadge
                status={a.status}
                tone={a.status === 'Active' ? 'danger' : 'informative'}
                label={a.status === 'EnteredInError' ? 'Entered in error' : undefined}
              />
            ),
          },
          { key: 'recorded', header: 'Recorded', priority: 'low', render: (a) => formatDate(a.recordedAt) },
        ]}
        emptyTitle={showAll ? 'No allergies recorded' : 'No active allergies'}
        emptyDescription={
          canManage
            ? 'Ask about drug, food and other allergies and record them here; drug allergies are checked on every prescription.'
            : undefined
        }
      />
      <RecordAllergyDialog
        patientId={patientId}
        open={recording}
        onClose={() => setRecording(false)}
        onOpenExisting={(id) => {
          setRecording(false);
          setEditingId(id);
        }}
      />
      <UpdateAllergyDialog allergy={editing} patientId={patientId} onClose={() => setEditingId(null)} />
      <ConfirmDialog
        open={confirmingNone}
        title="Record no known allergies?"
        confirmLabel="Record no known allergies"
        busy={recordNone.isPending}
        onConfirm={() => recordNone.mutate()}
        onCancel={closeNone}
      >
        <div className={styles.form}>
          <Text block>
            Record this only after asking the patient, or their family, about medicine, food and other allergies.
          </Text>
          <Text block>
            The banner will then show &quot;No known allergies&quot; with your name and the time. If an allergy is
            recorded later, it replaces this statement.
          </Text>
          {recordNone.error && (
            <MessageBar intent="error" layout="multiline" politeness="assertive">
              <MessageBarBody>
                <MessageBarTitle>Not recorded</MessageBarTitle>
                {noKnownAllergiesRefusal(recordNone.error) ??
                  (isApiError(recordNone.error) ? recordNone.error.userMessage : 'Please try again.')}
              </MessageBarBody>
            </MessageBar>
          )}
        </div>
      </ConfirmDialog>
    </>
  );
}

// ------------------------------------------------------------------------------------------------ alerts

const raiseAlertSchema = z
  .object({
    type: z.enum(['Dnr', 'FallRisk', 'Isolation', 'InfectionControl', 'Pregnancy', 'Custom']),
    title: z.string().max(200),
    detail: z.string().max(500),
    severity: z.enum(['Info', 'Warning', 'Critical']).nullable(),
    expiresOn: z.string().nullable(),
  })
  .superRefine((values, ctx) => {
    if (values.type === 'Custom' && !values.title.trim()) {
      ctx.addIssue({ code: 'custom', path: ['title'], message: 'Give the alert a title.' });
    }
    if (values.type === 'InfectionControl' && !values.detail.trim()) {
      ctx.addIssue({
        code: 'custom',
        path: ['detail'],
        message: 'Name the organism or infection (for example MRSA, open TB).',
      });
    }
  });

type RaiseAlertValues = z.infer<typeof raiseAlertSchema>;

const DETAIL_HINT: Record<RaiseAlertValues['type'], string> = {
  Dnr: 'Who decided and where the signed form is kept',
  FallRisk: 'Score or reason, e.g. Morse 55, on sedatives',
  Isolation: 'Precaution type: contact, droplet or airborne',
  InfectionControl: 'Organism or infection, e.g. MRSA, VRE, open TB',
  Pregnancy: 'Gestation or LMP; set the expected delivery date as the end date',
  Custom: 'What staff need to know',
};

export function RaiseAlertDialog({
  patientId,
  open,
  onClose,
}: {
  patientId: string;
  open: boolean;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 620 }}>
        {open && <RaiseAlertForm patientId={patientId} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function RaiseAlertForm({ patientId, onClose }: { patientId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<RaiseAlertValues>({
    resolver: zodResolver(raiseAlertSchema),
    defaultValues: { type: 'FallRisk', title: '', detail: '', severity: null, expiresOn: null },
  });
  const type = useWatch({ control: form.control, name: 'type' });

  const save = useMutation({
    mutationFn: (values: RaiseAlertValues) =>
      emrApi.raiseAlert(patientId, {
        type: values.type,
        title: values.title.trim() || null,
        detail: values.detail.trim() || null,
        severity: values.severity,
        expiresOn: values.expiresOn,
      }),
    onSuccess: (alert) => {
      notify.success('Alert added', `${alert.title} now shows on the patient banner.`);
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not add the alert');
      }
    },
  });

  return (
    <form
      onSubmit={form.handleSubmit((values) => !save.isPending && save.mutate(values))}
      onKeyDown={ignoreEscapeWhile(save.isPending)}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Add patient alert</DialogTitle>
        <DialogContent className={styles.form}>
          <SelectField
            control={form.control}
            name="type"
            label="Alert"
            options={ALERT_TYPES.map((t) => ({ value: t, label: alertTypeLabel[t] }))}
          />
          {type === 'Custom' && (
            <TextField control={form.control} name="title" label="Title" required autoFocus maxLength={200} />
          )}
          <TextAreaField
            control={form.control}
            name="detail"
            label="Details"
            required={type === 'InfectionControl'}
            rows={2}
            maxLength={500}
            hint={DETAIL_HINT[type]}
          />
          <FormGrid columns={2}>
            <SelectField
              control={form.control}
              name="severity"
              label="Banner colour"
              placeholder="Default for this alert"
              options={[
                { value: 'Critical', label: 'Critical (red)' },
                { value: 'Warning', label: 'Warning (amber)' },
                { value: 'Info', label: 'Information' },
              ]}
            />
            <DateField control={form.control} name="expiresOn" label="Until (optional)" min={todayIso()} />
          </FormGrid>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            icon={save.isPending ? <Spinner size="tiny" /> : <AlertUrgentRegular />}
            disabled={save.isPending}
            // Keeps focus while saving (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={save.isPending}
          >
            Add alert
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}

export function AlertsTab({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('clinical.alerts.manage');
  const [showAll, setShowAll] = useState(false);
  const [raising, setRaising] = useState(false);
  const [deactivating, setDeactivating] = useState<PatientAlertDto | null>(null);
  const alerts = useQuery(emrQueries.alerts(patientId, showAll));

  const deactivate = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => emrApi.deactivateAlert(id, reason),
    onSuccess: () => {
      setDeactivating(null);
      notify.success('Alert removed from the banner');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    },
    onError: (error) => notify.error(error, 'Could not deactivate the alert'),
  });

  return (
    <>
      <div className={styles.toolbar}>
        {canManage && (
          <Button appearance="primary" icon={<ShieldErrorRegular />} onClick={() => setRaising(true)}>
            Add alert
          </Button>
        )}
        <Switch checked={showAll} onChange={(_, d) => setShowAll(d.checked)} label="Show history" />
      </div>
      <DataTable
        ariaLabel="Patient alerts"
        rows={alerts.data}
        loading={alerts.isLoading}
        error={alerts.error}
        onRetry={() => void alerts.refetch()}
        getRowId={(a) => a.id}
        size="small"
        columns={[
          {
            key: 'title',
            header: 'Alert',
            render: (a) => (
              <span>
                <Text weight="semibold">{a.title}</Text>
                {a.detail && <Caption1 block>{a.detail}</Caption1>}
              </span>
            ),
          },
          {
            key: 'severity',
            header: 'Level',
            render: (a) => <StatusBadge status={a.severity} tone={severityTone(a.severity)} />,
          },
          { key: 'from', header: 'Since', priority: 'low', render: (a) => formatDate(a.effectiveFrom) },
          {
            key: 'until',
            header: 'Until',
            priority: 'low',
            render: (a) => (a.expiresOn ? formatDateOnly(a.expiresOn) : '—'),
          },
          {
            key: 'status',
            header: 'Status',
            render: (a) =>
              a.isActive ? (
                <StatusBadge status={a.isCurrent ? 'Active' : 'Expired'} />
              ) : (
                <span>
                  <StatusBadge status="Inactive" tone="informative" />
                  {a.deactivationReason && (
                    <Caption1 block className={styles.muted}>
                      {a.deactivationReason}
                    </Caption1>
                  )}
                </span>
              ),
          },
        ]}
        rowActions={
          canManage
            ? (a) =>
                a.isActive ? (
                  <Button size="small" onClick={() => setDeactivating(a)}>
                    Deactivate
                  </Button>
                ) : null
            : undefined
        }
        emptyTitle={showAll ? 'No alerts recorded' : 'No active alerts'}
        emptyDescription="DNR, fall risk, isolation, infection-control and pregnancy alerts show on the patient banner on every clinical screen."
      />
      <RaiseAlertDialog patientId={patientId} open={raising} onClose={() => setRaising(false)} />
      <ReasonDialog
        open={deactivating !== null}
        title={`Deactivate "${deactivating?.title ?? ''}"?`}
        description="The alert is removed from the banner. It stays in the history with your reason."
        confirmLabel="Deactivate"
        destructive
        busy={deactivate.isPending}
        onConfirm={(reason) => deactivating && deactivate.mutate({ id: deactivating.id, reason })}
        onCancel={() => setDeactivating(null)}
      />
    </>
  );
}
