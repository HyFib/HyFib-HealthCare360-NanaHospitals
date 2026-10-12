import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Spinner,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { todayIso } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  CloseRequestScope,
  DateField,
  FormGrid,
  SelectField,
  TextAreaField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { addDays } from '@/modules/hr/shared/dates';
import { UserPicker } from '@/modules/hr/shared/UserPicker';
import { qualityApi, qualityKeys, type CapaDetail } from './api';
import { CAPA_SOURCES, capaSourceLabel, type CapaSource } from './labels';

const schema = z.object({
  source: z.enum(CAPA_SOURCES),
  type: z.enum(['Corrective', 'Preventive']),
  action: z.string().trim().min(5, 'Describe the action: what will be done, where and by when.').max(2000),
  owner: z
    .object({ id: z.string(), name: z.string() })
    .nullable()
    .refine((v) => Boolean(v), 'Choose who owns this action.'),
  dueDate: z.string({ error: 'Choose the due date.' }).min(1, 'Choose the due date.'),
  effectivenessCheckDue: z.string().nullable(),
});

type Values = z.infer<typeof schema>;

export interface CapaContext {
  source: CapaSource;
  incidentId?: string | null;
  incidentNumber?: string | null;
  riskId?: string | null;
  riskNumber?: string | null;
}

/**
 * Plan (or edit) a corrective/preventive action. From an incident or a risk the source is fixed; the owner is notified
 * as soon as it is saved.
 */
export function CapaDialog({
  open,
  onClose,
  context,
  capa,
}: {
  open: boolean;
  onClose: () => void;
  context?: CapaContext;
  capa?: CapaDetail | null;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 680 }}>
        <CloseRequestScope value={closing}>
          {open && <Body onClose={onClose} context={context} capa={capa ?? null} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, context, capa }: { onClose: () => void; context?: CapaContext; capa: CapaDetail | null }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const fixedSource = Boolean(capa) || Boolean(context?.incidentId) || Boolean(context?.riskId);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      source: capa?.source ?? context?.source ?? 'Other',
      type: capa?.type ?? 'Corrective',
      action: capa?.action ?? '',
      owner: capa ? { id: capa.ownerUserId, name: capa.ownerName ?? 'Owner' } : null,
      dueDate: capa?.dueDate ?? addDays(todayIso(), 30),
      effectivenessCheckDue: capa?.effectivenessCheckDue ?? null,
    },
  });

  const submit = async (values: Values) => {
    if (!values.owner) {
      return;
    }
    setBusy(true);
    try {
      if (capa) {
        await qualityApi.updateCapa(capa.id, {
          action: values.action.trim(),
          ownerUserId: values.owner.id,
          dueDate: values.dueDate,
          effectivenessCheckDue: values.effectivenessCheckDue,
          version: capa.version,
        });
        notify.success('Action updated');
      } else {
        await qualityApi.createCapa({
          source: values.source,
          incidentId: context?.incidentId ?? null,
          riskId: context?.riskId ?? null,
          type: values.type,
          action: values.action.trim(),
          ownerUserId: values.owner.id,
          dueDate: values.dueDate,
        });
        notify.success('Action planned', `${values.owner.name} has been notified.`);
      }
      void queryClient.invalidateQueries({ queryKey: qualityKeys.all });
      onClose();
    } catch (error) {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the action');
      }
    } finally {
      setBusy(false);
    }
  };

  const about = context?.incidentNumber
    ? `For incident ${context.incidentNumber}`
    : context?.riskNumber
      ? `For risk ${context.riskNumber}`
      : capa
        ? capa.capaNumber
        : null;

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy, onClose });

  return (
    <form onSubmit={form.handleSubmit(submit)} noValidate>
      <DialogBody>
        <DialogTitle>
          {capa ? 'Edit corrective / preventive action' : 'Plan a corrective / preventive action'}
          {about && <Caption1 block>{about}</Caption1>}
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {!capa && (
            <FormGrid columns={2} minColumnWidth={220}>
              <SelectField
                control={form.control}
                name="source"
                label="Arising from"
                disabled={fixedSource}
                options={CAPA_SOURCES.filter((s) => (fixedSource ? true : s !== 'Incident' && s !== 'Risk')).map(
                  (s) => ({ value: s, label: capaSourceLabel[s] }),
                )}
              />
              <ChoiceField
                control={form.control}
                name="type"
                label="Kind"
                options={[
                  { value: 'Corrective', label: 'Corrective (fix the cause)' },
                  { value: 'Preventive', label: 'Preventive (stop it happening)' },
                ]}
              />
            </FormGrid>
          )}
          <TextAreaField
            control={form.control}
            name="action"
            label="Action"
            required
            rows={3}
            maxLength={2000}
            placeholder="For example: separate look-alike insulin vials on the ward trolley and add tall-man labels"
          />
          <FormGrid columns={2} minColumnWidth={220}>
            <Controller
              control={form.control}
              name="owner"
              render={({ field, fieldState }) => (
                <UserPicker
                  label="Owner"
                  required
                  value={field.value}
                  onChange={field.onChange}
                  validationMessage={fieldState.error?.message}
                />
              )}
            />
            <DateField
              control={form.control}
              name="dueDate"
              label="Due by"
              required
              min={capa ? undefined : todayIso()}
            />
            {capa && (
              <DateField
                control={form.control}
                name="effectivenessCheckDue"
                label="Check effectiveness by"
                hint="Set automatically 30 days after completion."
              />
            )}
          </FormGrid>
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            {capa ? 'Save' : 'Plan action'}
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
