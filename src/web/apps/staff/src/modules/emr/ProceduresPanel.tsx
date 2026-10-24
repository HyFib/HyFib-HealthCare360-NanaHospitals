import {
  Badge,
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
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Spinner,
  Switch,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, DismissRegular, EditRegular, MoreHorizontalRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  DataTable,
  FormGrid,
  SelectField,
  StatusBadge,
  TextAreaField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { nowIstInput } from '@/modules/hr/shared/dates';
import { ProcedureCodePicker } from '@/modules/mrd/CodePicker';
import { emrKeys } from './api';
import {
  emptyProcedureForm,
  PROCEDURE_SIDES,
  procedureApi,
  procedureFields,
  procedureLabel,
  procedureQueries,
  procedureSideLabel,
  procedureToForm,
  type ProcedureDto,
  type ProcedureFormValues,
} from './procedures';
import { ReasonDialog } from './ui';

const useStyles = makeStyles({
  toolbar: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', marginBottom: '12px' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  code: { display: 'flex', gap: '8px', alignItems: 'flex-end' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const schema = z
  .object({
    performedAt: z.string().min(1, 'Enter when it was done.'),
    code: z.string().max(32),
    display: z.string().max(300),
    performerName: z.string().max(200),
    side: z.enum(['NotApplicable', 'Left', 'Right', 'Bilateral']),
    bodySite: z.string().max(100),
    outcome: z.string().max(1000),
    notes: z.string().max(2000),
    reason: z.string().max(300),
  })
  .superRefine((v, ctx) => {
    if (!v.code.trim() && !v.display.trim()) {
      ctx.addIssue({ code: 'custom', path: ['display'], message: 'Describe the procedure, or choose its code.' });
    }
    if (v.performedAt > nowIstInput()) {
      ctx.addIssue({ code: 'custom', path: ['performedAt'], message: 'The procedure cannot be in the future.' });
    }
  });

type Values = ProcedureFormValues & { reason: string };

/** Record a procedure, or correct one (`editing`) with the reason it was wrong. */
function ProcedureDialog({
  patientId,
  editing,
  open,
  onClose,
}: {
  patientId: string;
  editing: ProcedureDto | null;
  open: boolean;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <CloseRequestScope value={closing}>
          {open && <ProcedureForm patientId={patientId} editing={editing} onClose={onClose} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function ProcedureForm({
  patientId,
  editing,
  onClose,
}: {
  patientId: string;
  editing: ProcedureDto | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<Values>({
    resolver: zodResolver(
      editing
        ? schema.refine((v) => v.reason.trim() !== '', { path: ['reason'], message: 'Say what was wrong.' })
        : schema,
    ),
    defaultValues: { ...(editing ? procedureToForm(editing) : emptyProcedureForm(nowIstInput())), reason: '' },
  });
  const code = useWatch({ control: form.control, name: 'code' });
  const save = useMutation({
    mutationFn: (v: Values) =>
      editing
        ? procedureApi.correct(editing.id, {
            ...procedureFields(v, editing.performerPractitionerId),
            reason: v.reason.trim(),
            version: editing.version,
          })
        : procedureApi.record(patientId, { ...procedureFields(v), encounterId: null }),
    onSuccess: () => {
      notify.success(editing ? 'Procedure corrected' : 'Procedure recorded');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, editing ? 'Could not correct the procedure' : 'Could not record the procedure');
      }
    },
  });
  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
      <DialogBody>
        <DialogTitle>{editing ? 'Correct procedure' : 'Record procedure'}</DialogTitle>
        <DialogContent className={styles.form}>
          <div className={styles.code}>
            <div style={{ flex: 1 }}>
              <ProcedureCodePicker
                label="Procedure code (ICD-10-PCS)"
                placeholder={code ? `${code} chosen · type to change` : 'Type a code or words, e.g. appendicectomy'}
                hint="Optional. Without a code, describe the procedure below."
                autoFocus={!editing}
                onPick={(picked) => {
                  form.setValue('code', picked.code, { shouldDirty: true });
                  if (!form.getValues('display').trim()) {
                    form.setValue('display', picked.title, { shouldDirty: true });
                  }
                  form.clearErrors('display');
                }}
              />
            </div>
            {code && (
              <>
                <Badge appearance="outline" size="large">
                  {code}
                </Badge>
                <Button
                  appearance="subtle"
                  icon={<DismissRegular />}
                  aria-label={`Remove the code ${code}`}
                  onClick={() => form.setValue('code', '', { shouldDirty: true })}
                />
              </>
            )}
          </div>
          <FormGrid columns={2}>
            <TextField control={form.control} name="display" label="Procedure" required={!code} maxLength={300} />
            <Controller
              control={form.control}
              name="performedAt"
              render={({ field, fieldState }) => (
                <Field
                  label="Done at"
                  required
                  hint="Hospital time"
                  validationMessage={fieldState.error?.message}
                  validationState={fieldState.error ? 'error' : 'none'}
                >
                  <Input
                    type="datetime-local"
                    value={field.value}
                    max={nowIstInput()}
                    onChange={(_, d) => field.onChange(d.value)}
                    onBlur={field.onBlur}
                    ref={field.ref}
                  />
                </Field>
              )}
            />
            <SelectField
              control={form.control}
              name="side"
              label="Side"
              options={PROCEDURE_SIDES.map((s) => ({ value: s, label: procedureSideLabel[s] }))}
            />
            <TextField
              control={form.control}
              name="bodySite"
              label="Site"
              maxLength={100}
              placeholder="e.g. left forearm"
            />
            <TextField
              control={form.control}
              name="performerName"
              label="Done by"
              maxLength={200}
              placeholder={editing?.performerPractitionerId ? (editing.performerName ?? '') : 'You'}
              hint="Leave empty if you did it; name a doctor from elsewhere otherwise."
            />
          </FormGrid>
          <TextAreaField control={form.control} name="outcome" label="Outcome" rows={2} maxLength={1000} />
          <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={2000} />
          {editing && (
            <TextAreaField
              control={form.control}
              name="reason"
              label="What was wrong"
              required
              rows={2}
              maxLength={300}
            />
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {editing ? 'Save correction' : 'Record'}
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}

/** The patient's procedures (EHR-01): recorded on the chart and from operations completed in the theatre. */
export function ProceduresTab({ patientId }: { patientId: string }) {
  const styles = useStyles();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canWrite = usePermission('clinical.emr.write');
  const [showErrors, setShowErrors] = useState(false);
  const [dialog, setDialog] = useState<{ editing: ProcedureDto | null } | null>(null);
  const [voiding, setVoiding] = useState<ProcedureDto | null>(null);
  const procedures = useQuery(procedureQueries.list(patientId, showErrors));
  const markError = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => procedureApi.enteredInError(id, reason),
    onSuccess: () => {
      setVoiding(null);
      notify.success('Marked as entered in error');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    },
    onError: (error) => notify.error(error, 'Could not update the procedure'),
  });

  return (
    <>
      <div className={styles.toolbar}>
        {canWrite && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setDialog({ editing: null })}>
            Record procedure
          </Button>
        )}
        <Switch
          checked={showErrors}
          onChange={(_, d) => setShowErrors(d.checked)}
          label="Show entries marked in error"
        />
      </div>
      <DataTable
        ariaLabel="Procedures"
        rows={procedures.data}
        loading={procedures.isLoading}
        error={procedures.error}
        onRetry={() => void procedures.refetch()}
        getRowId={(p) => p.id}
        size="small"
        columns={[
          { key: 'when', header: 'Done at', width: 160, render: (p) => formatDateTime(p.performedAt) },
          {
            key: 'procedure',
            header: 'Procedure',
            render: (p) => (
              <span>
                <Text
                  weight="semibold"
                  style={p.status === 'EnteredInError' ? { textDecoration: 'line-through' } : undefined}
                >
                  {procedureLabel(p)}
                </Text>
                {p.outcome && <Caption1 className={styles.muted}> · {p.outcome}</Caption1>}
              </span>
            ),
          },
          { key: 'by', header: 'Done by', render: (p) => p.performerName ?? '—' },
          {
            key: 'source',
            header: 'From',
            priority: 'low',
            render: (p) => (p.source === 'OperationTheatre' ? 'Operation theatre' : 'Chart'),
          },
          {
            key: 'status',
            header: 'Status',
            priority: 'low',
            render: (p) =>
              p.status === 'EnteredInError' ? (
                <StatusBadge status="Voided" tone="danger" label="Entered in error" />
              ) : p.status === 'Amended' ? (
                <StatusBadge status="Amended" label="Corrected" />
              ) : null,
          },
        ]}
        rowActions={
          canWrite
            ? (p) =>
                p.status === 'EnteredInError' ? null : (
                  <Menu>
                    <MenuTrigger disableButtonEnhancement>
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<MoreHorizontalRegular />}
                        aria-label={`Actions for ${p.display}`}
                        {...restoreFocusTarget}
                      />
                    </MenuTrigger>
                    <MenuPopover>
                      <MenuList>
                        <MenuItem icon={<EditRegular />} onClick={() => setDialog({ editing: p })}>
                          Correct
                        </MenuItem>
                        <MenuItem icon={<DeleteRegular />} onClick={() => setVoiding(p)}>
                          Mark as entered in error
                        </MenuItem>
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                )
            : undefined
        }
        emptyTitle="No procedures recorded"
        emptyDescription="Procedures recorded here, and operations completed in the theatre, appear in this list."
      />
      <ProcedureDialog
        patientId={patientId}
        editing={dialog?.editing ?? null}
        open={dialog !== null}
        onClose={() => setDialog(null)}
      />
      <ReasonDialog
        open={voiding !== null}
        title={`Mark ${voiding?.display ?? 'this procedure'} as entered in error?`}
        description="The entry stays in the audit trail but leaves the patient's record and timeline."
        label="What was wrong"
        confirmLabel="Mark as entered in error"
        destructive
        busy={markError.isPending}
        onConfirm={(reason) => voiding && markError.mutate({ id: voiding.id, reason })}
        onCancel={() => setVoiding(null)}
      />
    </>
  );
}
