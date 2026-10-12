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
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ArchiveRegular, CheckmarkCircleRegular, OpenRegular, ScanRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  ConfirmDialog,
  DataTable,
  DateField,
  ErrorState,
  FormGrid,
  LoadingState,
  LookupField,
  Page,
  PageHeader,
  Section,
  SelectField,
  StatusBadge,
  TextAreaField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { searchPractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { ReasonDialog } from '@/modules/emr/ui';
import {
  DEFICIENCY_TYPES,
  deficiencyLabel,
  mrdApi,
  mrdKeys,
  mrdQueries,
  type ChartDeficiencyDto,
  type MedicalRecordDto,
} from '@/modules/mrd/api';

export const Route = createFileRoute('/_app/mrd/records/$recordId')({
  component: ChartPage,
});

const useStyles = makeStyles({
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))', gap: '12px 24px' },
  fact: { display: 'flex', flexDirection: 'column', gap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

function Fact({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.fact}>
      <Caption1 className={styles.muted}>{label}</Caption1>
      <Text>{children}</Text>
    </div>
  );
}

const deficiencySchema = z.object({
  type: z.enum([
    'MissingDischargeSummary',
    'UnsignedEncounter',
    'UnsignedNote',
    'MissingDiagnosis',
    'MissingConsent',
    'MissingSignature',
    'PendingCoding',
    'Other',
  ]),
  description: z.string().trim().min(1, 'Describe what is missing.').max(500),
  assignedPractitionerId: z.string().nullable(),
  dueDate: z.string().nullable(),
});

type DeficiencyValues = z.infer<typeof deficiencySchema>;

function AddDeficiencyDialog({
  record,
  open,
  onClose,
}: {
  record: MedicalRecordDto;
  open: boolean;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <CloseRequestScope value={closing}>
          {open && <AddDeficiencyForm record={record} onClose={onClose} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function AddDeficiencyForm({ record, onClose }: { record: MedicalRecordDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<DeficiencyValues>({
    resolver: zodResolver(deficiencySchema),
    defaultValues: {
      type: 'MissingSignature',
      description: '',
      assignedPractitionerId: record.assignedPractitionerId,
      dueDate: record.dueDate,
    },
  });
  const save = useMutation({
    mutationFn: (v: DeficiencyValues) =>
      mrdApi.addDeficiency(record.id, {
        type: v.type,
        description: v.description.trim(),
        assignedPractitionerId: v.assignedPractitionerId,
        dueDate: v.dueDate,
      }),
    onSuccess: (updated) => {
      queryClient.setQueryData(mrdKeys.record(record.id), updated);
      void queryClient.invalidateQueries({ queryKey: ['mrd', 'records'] });
      notify.success('Deficiency added', 'It appears on the doctor’s worklist.');
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not add the deficiency');
      }
    },
  });
  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
      <DialogBody>
        <DialogTitle>Add deficiency</DialogTitle>
        <DialogContent className={styles.form}>
          <SelectField
            control={form.control}
            name="type"
            label="What is missing"
            options={DEFICIENCY_TYPES.map((t) => ({ value: t, label: deficiencyLabel[t] }))}
          />
          <TextAreaField
            control={form.control}
            name="description"
            label="Details for the doctor"
            required
            rows={3}
            maxLength={500}
          />
          <FormGrid columns={2}>
            <LookupField
              control={form.control}
              name="assignedPractitionerId"
              label="Assign to doctor"
              load={searchPractitioners}
            />
            <DateField control={form.control} name="dueDate" label="Due by" min={todayIso()} />
          </FormGrid>
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            Add
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}

function ArchiveDialog({
  open,
  busy,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  busy: boolean;
  onConfirm: (location: string) => void;
  onCancel: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && !busy && onCancel()}>
      <DialogSurface>{open && <ArchiveBody busy={busy} onConfirm={onConfirm} onCancel={onCancel} />}</DialogSurface>
    </Dialog>
  );
}

function ArchiveBody({
  busy,
  onConfirm,
  onCancel,
}: {
  busy: boolean;
  onConfirm: (location: string) => void;
  onCancel: () => void;
}) {
  const [location, setLocation] = useState('');
  return (
    <DialogBody>
      <DialogTitle>Archive this chart</DialogTitle>
      <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        <Text>Record where the file is kept so it can be found for audits, claims and court requests.</Text>
        <Field label="Storage location" required hint="Rack and shelf, box number or e-archive reference">
          <Input
            autoFocus
            value={location}
            onChange={(_, d) => setLocation(d.value.slice(0, 200))}
            placeholder="e.g. MRD room 2, rack 14, shelf C"
          />
        </Field>
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel} disabled={busy}>
          Cancel
        </Button>
        <Button appearance="primary" disabled={!location.trim() || busy} onClick={() => onConfirm(location.trim())}>
          Archive
        </Button>
      </DialogActions>
    </DialogBody>
  );
}

function ChartPage() {
  const { recordId } = Route.useParams();
  const record = useQuery(mrdQueries.record(recordId));
  if (record.isLoading) {
    return <LoadingState />;
  }
  if (record.error || !record.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={record.error} onRetry={() => void record.refetch()} />
      </Page>
    );
  }
  return <ChartDetail record={record.data} />;
}

/** One chart: deficiencies (system-detected and manual), completion, archival and retention. */
function ChartDetail({ record: r }: { record: MedicalRecordDto }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canReadChart = usePermission('clinical.emr.read');
  const [adding, setAdding] = useState(false);
  const [resolving, setResolving] = useState<ChartDeficiencyDto | null>(null);
  const [archiving, setArchiving] = useState(false);
  const [confirmComplete, setConfirmComplete] = useState(false);

  const publish = (updated: MedicalRecordDto) => {
    queryClient.setQueryData(mrdKeys.record(updated.id), updated);
    void queryClient.invalidateQueries({ queryKey: ['mrd', 'records'] });
  };

  const scan = useMutation({
    mutationFn: () => mrdApi.scan(r.id),
    onSuccess: (updated) => {
      publish(updated);
      notify.success(
        'Chart checked',
        updated.openDeficiencies > 0 ? `${updated.openDeficiencies} open deficiencies.` : 'Nothing is missing.',
      );
    },
    onError: (error) => notify.error(error, 'Could not check the chart'),
  });
  const complete = useMutation({
    mutationFn: () => mrdApi.complete(r.id),
    onSuccess: (updated) => {
      setConfirmComplete(false);
      publish(updated);
      notify.success(
        'Chart complete',
        updated.retainUntil
          ? `Keep until ${formatDateOnly(updated.retainUntil)}.`
          : 'Medico-legal chart: keep permanently.',
      );
    },
    onError: (error) => {
      setConfirmComplete(false);
      notify.error(error, 'Could not complete the chart');
    },
  });
  const archive = useMutation({
    mutationFn: (location: string) => mrdApi.archive(r.id, location),
    onSuccess: (updated) => {
      setArchiving(false);
      publish(updated);
      notify.success('Chart archived');
    },
    onError: (error) => notify.error(error, 'Could not archive the chart'),
  });
  const resolve = useMutation({
    mutationFn: ({ id, note }: { id: string; note: string }) => mrdApi.resolveDeficiency(id, note || null),
    onSuccess: (updated) => {
      setResolving(null);
      publish(updated);
      notify.success('Deficiency resolved');
    },
    onError: (error) => notify.error(error, 'Could not resolve the deficiency'),
  });

  const open = r.deficiencies.filter((d) => !d.resolvedAt);
  const archived = r.status === 'Archived';

  return (
    <Page>
      <PageHeader
        title={`Chart · ${r.patientName ?? 'Patient'}`}
        subtitle={`${r.uhid ?? ''} · ${r.admissionId ? 'In-patient stay' : 'Out-patient encounter'}`}
        breadcrumbs={[
          { label: 'Patient charts', onClick: () => void navigate({ to: '/mrd/records' }) },
          { label: r.uhid ?? 'Chart' },
        ]}
        actions={
          <div className={styles.actions}>
            {canReadChart && (
              <Button
                icon={<OpenRegular />}
                onClick={() => void navigate({ to: '/emr/patients/$patientId', params: { patientId: r.patientId } })}
              >
                Clinical chart
              </Button>
            )}
            {!archived && (
              <Button
                icon={scan.isPending ? <Spinner size="tiny" /> : <ScanRegular />}
                disabled={scan.isPending}
                onClick={() => scan.mutate()}
              >
                Check for gaps
              </Button>
            )}
            {!archived && (
              <Button icon={<AddRegular />} onClick={() => setAdding(true)}>
                Add deficiency
              </Button>
            )}
            {(r.status === 'Incomplete' || r.status === 'Deficient') && (
              <Button
                appearance="primary"
                icon={<CheckmarkCircleRegular />}
                disabled={open.length > 0}
                onClick={() => setConfirmComplete(true)}
              >
                Mark complete
              </Button>
            )}
            {r.status === 'Complete' && (
              <Button appearance="primary" icon={<ArchiveRegular />} onClick={() => setArchiving(true)}>
                Archive
              </Button>
            )}
          </div>
        }
      />

      <Section>
        <div className={styles.facts}>
          <Fact label="Status">
            <StatusBadge status={r.status} tone={r.status === 'Deficient' ? 'danger' : undefined} />{' '}
            {r.isOverdue && (
              <Badge size="small" color="danger">
                Overdue
              </Badge>
            )}
          </Fact>
          <Fact label="Admitted">{r.admittedAt ? formatDateTime(r.admittedAt) : '—'}</Fact>
          <Fact label="Discharged">{r.dischargedAt ? formatDateTime(r.dischargedAt) : '—'}</Fact>
          <Fact label="Complete by">{r.dueDate ? formatDateOnly(r.dueDate) : '—'}</Fact>
          <Fact label="Completed">{r.completedAt ? formatDateTime(r.completedAt) : '—'}</Fact>
          <Fact label="Keep until">
            {r.completedAt ? (r.retainUntil ? formatDateOnly(r.retainUntil) : 'Permanently (medico-legal)') : '—'}
          </Fact>
          <Fact label="Archived at">
            {r.archiveLocation
              ? `${r.archiveLocation}${r.archivedAt ? ` · ${formatDateTime(r.archivedAt)}` : ''}`
              : '—'}
          </Fact>
          <Fact label="Medico-legal case">{r.isMlc ? 'Yes' : 'No'}</Fact>
        </div>
      </Section>

      {open.length > 0 && (r.status === 'Incomplete' || r.status === 'Deficient') && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Resolve the {open.length} open deficienc{open.length === 1 ? 'y' : 'ies'} before completing the chart.
            Doctors see theirs on their worklist.
          </MessageBarBody>
        </MessageBar>
      )}

      <Section title={`Deficiencies (${open.length} open)`} flush>
        <DataTable
          ariaLabel="Deficiencies"
          rows={r.deficiencies.toSorted((a, b) => Number(Boolean(a.resolvedAt)) - Number(Boolean(b.resolvedAt)))}
          getRowId={(d) => d.id}
          size="small"
          columns={[
            {
              key: 'type',
              header: 'Deficiency',
              render: (d) => (
                <span>
                  <Text weight="semibold">{deficiencyLabel[d.type]}</Text>
                  <Caption1 block>{d.description}</Caption1>
                </span>
              ),
            },
            { key: 'doctor', header: 'Assigned to', render: (d) => d.assignedPractitionerName ?? '—' },
            { key: 'due', header: 'Due', width: 120, render: (d) => (d.dueDate ? formatDateOnly(d.dueDate) : '—') },
            {
              key: 'raised',
              header: 'Raised',
              priority: 'low',
              render: (d) => `${formatDateTime(d.raisedAt)}${d.isSystemDetected ? ' · automatic' : ''}`,
            },
            {
              key: 'status',
              header: 'Status',
              render: (d) =>
                d.resolvedAt ? (
                  <span>
                    <StatusBadge status="Resolved" />
                    {d.resolutionNote && (
                      <Caption1 block className={styles.muted}>
                        {d.resolutionNote}
                      </Caption1>
                    )}
                  </span>
                ) : (
                  <StatusBadge status="Open" />
                ),
            },
          ]}
          rowActions={(d) =>
            d.resolvedAt || archived ? null : (
              <Button size="small" onClick={() => setResolving(d)}>
                Resolve
              </Button>
            )
          }
          emptyTitle="No deficiencies"
          emptyDescription="Use Check for gaps to find unsigned notes, missing diagnoses and missing discharge summaries."
        />
      </Section>

      <AddDeficiencyDialog record={r} open={adding} onClose={() => setAdding(false)} />
      <ReasonDialog
        open={resolving !== null}
        title="Resolve this deficiency?"
        description={resolving ? `${deficiencyLabel[resolving.type]}: ${resolving.description}` : undefined}
        label="Note"
        optional
        maxLength={500}
        confirmLabel="Resolve"
        busy={resolve.isPending}
        onConfirm={(note) => resolving && resolve.mutate({ id: resolving.id, note })}
        onCancel={() => setResolving(null)}
      />
      <ConfirmDialog
        open={confirmComplete}
        title="Mark the chart complete?"
        confirmLabel="Mark complete"
        busy={complete.isPending}
        onConfirm={() => complete.mutate()}
        onCancel={() => setConfirmComplete(false)}
      >
        The retention period starts now: out-patient charts are kept 3 years, in-patient charts 5 years, medico-legal
        charts permanently.
      </ConfirmDialog>
      <ArchiveDialog
        open={archiving}
        busy={archive.isPending}
        onConfirm={(location) => archive.mutate(location)}
        onCancel={() => setArchiving(false)}
      />
    </Page>
  );
}
