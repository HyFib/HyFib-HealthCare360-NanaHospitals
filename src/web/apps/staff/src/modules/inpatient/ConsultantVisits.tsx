import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Text,
  Tooltip,
} from '@fluentui/react-components';
import { DismissCircleRegular, StethoscopeRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, isApiError, useSession, type LookupItem } from '@hc360/core';
import { applyServerErrors, DataTable, LookupField, TextAreaField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { searchPractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { admissionApi, inpatientKeys, inpatientQueries, type ConsultantVisitDto } from './api';
import { ReasonDialog } from './ReasonDialog';
import { fromIstInput, toIstInput } from './time';

const schema = z
  .object({
    practitionerId: z.string().nullable(),
    earlier: z.boolean(),
    visitedAt: z.string(),
    notes: z.string().trim().max(1000),
  })
  .superRefine((v, ctx) => {
    if (v.earlier && !fromIstInput(v.visitedAt)) {
      ctx.addIssue({ code: 'custom', path: ['visitedAt'], message: 'Enter the time of the visit.' });
    }
  });

type VisitValues = z.infer<typeof schema>;

/** Consultant ward-round visits: each visit is charged (VISIT-DR, or VISIT-DR-ICU in critical care). */
export function ConsultantVisits({
  admissionId,
  chartOpen,
  defaultPractitioner,
}: {
  admissionId: string;
  chartOpen: boolean;
  defaultPractitioner: LookupItem | null;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRecord = usePermission('nursing.chart') && chartOpen;
  const visits = useQuery(inpatientQueries.visits(admissionId));
  const [recording, setRecording] = useState(false);
  const [cancelling, setCancelling] = useState<ConsultantVisitDto | null>(null);

  const cancel = useMutation({
    mutationFn: ({ visit, reason }: { visit: ConsultantVisitDto; reason: string }) =>
      admissionApi.cancelVisit(visit.id, reason),
    onSuccess: () => {
      notify.success('Visit cancelled', 'Its charge has been reversed.');
      setCancelling(null);
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.admission(admissionId) });
    },
  });

  return (
    <>
      {canRecord && (
        <div style={{ marginBottom: 12 }}>
          <Button icon={<StethoscopeRegular />} onClick={() => setRecording(true)}>
            Record consultant visit
          </Button>
        </div>
      )}
      <DataTable
        ariaLabel="Consultant visits"
        rows={visits.data}
        loading={visits.isLoading}
        error={visits.error}
        onRetry={() => void visits.refetch()}
        getRowId={(v) => v.id}
        size="small"
        columns={[
          { key: 'at', header: 'Visited', width: 180, render: (v) => formatDateTime(v.visitedAt) },
          { key: 'doctor', header: 'Doctor', render: (v) => v.practitionerName ?? '—' },
          {
            key: 'charge',
            header: 'Charge',
            width: 170,
            render: (v) =>
              v.isCharged ? (
                <Badge appearance="tint" color="success">
                  {v.serviceCode}
                </Badge>
              ) : (
                <Tooltip content={v.chargeError ?? 'Billing will retry within the hour.'} relationship="description">
                  <Badge appearance="tint" color="warning">
                    Charge pending
                  </Badge>
                </Tooltip>
              ),
          },
          { key: 'notes', header: 'Notes', priority: 'low', render: (v) => v.notes ?? '—' },
        ]}
        rowActions={
          canRecord
            ? (v) => (
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DismissCircleRegular />}
                  onClick={() => setCancelling(v)}
                  aria-label={`Cancel visit of ${formatDateTime(v.visitedAt)}`}
                >
                  Cancel
                </Button>
              )
            : undefined
        }
        emptyTitle="No consultant visits recorded"
        emptyDescription={canRecord ? 'Record each ward-round visit so it is billed.' : undefined}
      />
      {recording && (
        <RecordVisitDialog
          admissionId={admissionId}
          defaultPractitioner={defaultPractitioner}
          onClose={() => setRecording(false)}
          onSaved={() => {
            setRecording(false);
            void queryClient.invalidateQueries({ queryKey: inpatientKeys.admission(admissionId) });
          }}
        />
      )}
      <ReasonDialog
        open={cancelling !== null}
        title="Cancel this consultant visit?"
        description="The visit charge is reversed on the patient's bill. Use this only for a visit recorded by mistake."
        confirmLabel="Cancel visit"
        destructive
        busy={cancel.isPending}
        error={cancel.error}
        onConfirm={(reason) => cancelling && cancel.mutate({ visit: cancelling, reason })}
        onCancel={() => {
          setCancelling(null);
          cancel.reset();
        }}
      />
    </>
  );
}

/** Records a consultant's ward-round visit, which charges the visit to the patient's in-patient bill. */
export function RecordVisitDialog({
  admissionId,
  defaultPractitioner,
  description,
  onClose,
  onSaved,
}: {
  admissionId: string;
  defaultPractitioner: LookupItem | null;
  /** Who and where, when the dialog opens away from the admission (the ward round). */
  description?: ReactNode;
  onClose: () => void;
  onSaved: () => void;
}) {
  const notify = useNotify();
  const ownPractitionerId = useSession((s) => s.user?.practitionerId ?? null);
  const [nowInput] = useState(() => toIstInput(new Date()));
  const form = useForm<VisitValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      practitionerId: ownPractitionerId ?? defaultPractitioner?.id ?? null,
      earlier: false,
      visitedAt: nowInput,
      notes: '',
    },
  });
  const earlier = useWatch({ control: form.control, name: 'earlier' });

  const record = useMutation({
    mutationFn: (v: VisitValues) =>
      admissionApi.recordVisit(admissionId, {
        practitionerId: v.practitionerId,
        visitedAt: v.earlier ? fromIstInput(v.visitedAt) : null,
        notes: v.notes || null,
      }),
    onSuccess: (visit) => {
      notify.success(
        'Visit recorded',
        visit.isCharged ? `Charged as ${visit.serviceCode}.` : 'The charge will be posted when billing is reachable.',
      );
      onSaved();
    },
    onError: (error) => applyServerErrors(error, form.setError),
  });

  const error = record.error && !(isApiError(record.error) && record.error.isValidation) ? record.error : null;

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !record.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form onSubmit={form.handleSubmit((v) => record.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>Record consultant visit</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {description}
              <LookupField
                control={form.control}
                name="practitionerId"
                label="Doctor"
                load={searchPractitioners}
                initialItems={defaultPractitioner ? [defaultPractitioner] : []}
                hint={ownPractitionerId ? 'Leave as is to record your own visit.' : 'Type the doctor’s name.'}
              />
              <Controller
                control={form.control}
                name="earlier"
                render={({ field }) => (
                  <Switch
                    checked={field.value}
                    onChange={(_, d) => field.onChange(d.checked)}
                    label="The visit was earlier (within 48 hours)"
                  />
                )}
              />
              {earlier && (
                <Controller
                  control={form.control}
                  name="visitedAt"
                  render={({ field, fieldState }) => (
                    <Field
                      label="Visited at"
                      required
                      validationMessage={fieldState.error?.message}
                      validationState={fieldState.error ? 'error' : 'none'}
                    >
                      <Input
                        type="datetime-local"
                        value={field.value}
                        max={nowInput}
                        onChange={(_, d) => field.onChange(d.value)}
                      />
                    </Field>
                  )}
                />
              )}
              <TextAreaField control={form.control} name="notes" label="Visit notes" rows={3} maxLength={1000} />
              {error && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <Text weight="semibold">{isApiError(error) ? error.title : 'Could not record the visit'}</Text>{' '}
                    {isApiError(error) ? error.userMessage : ''}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={record.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={record.isPending}
                icon={record.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record visit
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
