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
  Select,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular, ArrowUndoRegular, DismissRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import {
  CloseRequestScope,
  ConfirmDialog,
  DataTable,
  FilterBar,
  FormGrid,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { addDaysIso } from '@/modules/emr/encounterForm';
import { ReasonDialog } from '@/modules/emr/ui';
import {
  mrdApi,
  mrdQueries,
  purposeLabel,
  RECORD_REQUEST_PURPOSES,
  RECORD_REQUEST_STATUSES,
  type RecordRequestDto,
  type RecordRequestPurpose,
  type RecordRequestStatus,
  type RequestListParams,
} from '@/modules/mrd/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

export const Route = createFileRoute('/_app/mrd/requests')({
  validateSearch: (s: Record<string, unknown>): RequestListParams => ({
    status: RECORD_REQUEST_STATUSES.includes(s.status as RecordRequestStatus)
      ? (s.status as RecordRequestStatus)
      : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: RecordRequestsPage,
});

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', gap: '12px' } });

function statusTone(status: RecordRequestStatus): 'warning' | 'brand' | 'success' | 'danger' | 'informative' {
  switch (status) {
    case 'Requested':
      return 'warning';
    case 'Issued':
      return 'brand';
    case 'Returned':
      return 'success';
    case 'Overdue':
      return 'danger';
    default:
      return 'informative';
  }
}

function NewRequestDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 620 }}>
        {open && (
          <CloseRequestScope value={closing}>
            <NewRequestForm onClose={onClose} />
          </CloseRequestScope>
        )}
      </DialogSurface>
    </Dialog>
  );
}

function NewRequestForm({ onClose }: { onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [requestedBy, setRequestedBy] = useState('');
  const [department, setDepartment] = useState('');
  const [purpose, setPurpose] = useState<RecordRequestPurpose>('PatientCare');
  const [details, setDetails] = useState('');
  const create = useMutation({
    mutationFn: () =>
      mrdApi.createRequest({
        patientId: patient?.id ?? '',
        medicalRecordId: null,
        requestedByName: requestedBy.trim(),
        requestingDepartment: department.trim() || null,
        purpose,
        details: details.trim() || null,
      }),
    onSuccess: (request) => {
      notify.success(`Request ${request.requestNumber} recorded`, 'Issue the file when it is handed over.');
      void queryClient.invalidateQueries({ queryKey: ['mrd', 'requests'] });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not record the request'),
  });
  const valid = Boolean(patient) && requestedBy.trim().length > 0;
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ patient, requestedBy, department, purpose, details }),
    busy: create.isPending,
    onClose,
  });

  return (
    <DialogBody>
      <DialogTitle>New record request</DialogTitle>
      <DialogContent className={styles.form}>
        <PatientPicker value={patient} onChange={setPatient} required />
        <FormGrid columns={2}>
          <Field label="Requested by" required>
            <Input
              value={requestedBy}
              onChange={(_, d) => setRequestedBy(d.value.slice(0, 200))}
              placeholder="Name of the person"
            />
          </Field>
          <Field label="Department">
            <Input
              value={department}
              onChange={(_, d) => setDepartment(d.value.slice(0, 128))}
              placeholder="e.g. Cardiology, Insurance desk"
            />
          </Field>
        </FormGrid>
        <Field label="Purpose">
          <Select value={purpose} onChange={(_, d) => setPurpose(d.value as RecordRequestPurpose)}>
            {RECORD_REQUEST_PURPOSES.map((p) => (
              <option key={p} value={p}>
                {purposeLabel[p]}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Details">
          <Textarea
            value={details}
            onChange={(_, d) => setDetails(d.value.slice(0, 1000))}
            rows={2}
            resize="vertical"
            placeholder="Which admission or period, court order number…"
          />
        </Field>
      </DialogContent>
      <DialogActions>
        <Button onClick={guard.requestClose} disabled={create.isPending}>
          Cancel
        </Button>
        <Button
          appearance="primary"
          disabled={!valid || create.isPending}
          icon={create.isPending ? <Spinner size="tiny" /> : undefined}
          onClick={() => create.mutate()}
        >
          Record request
        </Button>
      </DialogActions>
      {guard.dialogs}
    </DialogBody>
  );
}

function IssueDialog({
  request,
  busy,
  onConfirm,
  onCancel,
}: {
  request: RecordRequestDto | null;
  busy: boolean;
  onConfirm: (dueBackOn: string | null) => void;
  onCancel: () => void;
}) {
  return (
    <Dialog open={request !== null} onOpenChange={(_, d) => !d.open && !busy && onCancel()}>
      <DialogSurface>
        {request && <IssueBody request={request} busy={busy} onConfirm={onConfirm} onCancel={onCancel} />}
      </DialogSurface>
    </Dialog>
  );
}

function IssueBody({
  request,
  busy,
  onConfirm,
  onCancel,
}: {
  request: RecordRequestDto;
  busy: boolean;
  onConfirm: (dueBackOn: string | null) => void;
  onCancel: () => void;
}) {
  const today = todayIso();
  const [dueBack, setDueBack] = useState(() => addDaysIso(today, request.purpose === 'PatientCare' ? 3 : 7));
  return (
    <DialogBody>
      <DialogTitle>Issue the file</DialogTitle>
      <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        <Text>
          {request.patientName} ({request.uhid}) to {request.requestedByName}
          {request.requestingDepartment ? `, ${request.requestingDepartment}` : ''}.
        </Text>
        <Field label="Return by" hint="Files not returned by this date show as overdue">
          <Input type="date" value={dueBack} min={today} onChange={(_, d) => setDueBack(d.value)} />
        </Field>
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel} disabled={busy}>
          Cancel
        </Button>
        <Button
          appearance="primary"
          disabled={busy}
          icon={busy ? <Spinner size="tiny" /> : <SendRegular />}
          onClick={() => onConfirm(dueBack || null)}
        >
          Issue
        </Button>
      </DialogActions>
    </DialogBody>
  );
}

/** Requests for patient files (MRD-04): record, issue with a return date, track returns and overdue files. */
function RecordRequestsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const requests = useQuery(mrdQueries.requests(filters));
  const [creating, setCreating] = useState(false);
  const [issuing, setIssuing] = useState<RecordRequestDto | null>(null);
  const [returning, setReturning] = useState<RecordRequestDto | null>(null);
  const [cancelling, setCancelling] = useState<RecordRequestDto | null>(null);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['mrd', 'requests'] });

  const issue = useMutation({
    mutationFn: ({ id, dueBackOn }: { id: string; dueBackOn: string | null }) => mrdApi.issueRequest(id, dueBackOn),
    onSuccess: (r) => {
      setIssuing(null);
      notify.success(`File issued to ${r.requestedByName}`);
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not issue the file'),
  });
  const giveBack = useMutation({
    mutationFn: (id: string) => mrdApi.returnRequest(id),
    onSuccess: () => {
      setReturning(null);
      notify.success('File returned');
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not record the return'),
  });
  const cancel = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => mrdApi.cancelRequest(id, reason),
    onSuccess: () => {
      setCancelling(null);
      notify.success('Request cancelled');
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not cancel the request'),
  });

  return (
    <Page>
      <PageHeader
        title="Record requests"
        subtitle="Files requested by departments, insurers, courts and patients, and whether they came back."
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
            New request
          </Button>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Status">
              <Select
                value={filters.status ?? ''}
                onChange={(_, d) =>
                  void navigate({
                    search: { status: (d.value || undefined) as RecordRequestStatus | undefined },
                    replace: true,
                  })
                }
              >
                <option value="">All</option>
                {RECORD_REQUEST_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {s === 'Overdue' ? 'Overdue (not returned)' : s}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Record requests"
          rows={requests.data?.items}
          loading={requests.isLoading}
          error={requests.error}
          onRetry={() => void requests.refetch()}
          getRowId={(r) => r.id}
          size="small"
          columns={[
            {
              key: 'number',
              header: 'Request',
              width: 140,
              render: (r) => <Text font="monospace">{r.requestNumber}</Text>,
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName ?? '—'}</Text> <Caption1>· {r.uhid}</Caption1>
                  {r.isPatientRequest && (
                    <Badge size="small" appearance="outline" style={{ marginLeft: 6 }}>
                      From portal
                    </Badge>
                  )}
                </span>
              ),
            },
            {
              key: 'by',
              header: 'Requested by',
              render: (r) => `${r.requestedByName}${r.requestingDepartment ? ` · ${r.requestingDepartment}` : ''}`,
            },
            { key: 'purpose', header: 'Purpose', priority: 'low', render: (r) => purposeLabel[r.purpose] },
            { key: 'at', header: 'Requested', priority: 'low', render: (r) => formatDateTime(r.requestedAt) },
            {
              key: 'due',
              header: 'Return by',
              width: 120,
              render: (r) => (r.dueBackOn ? formatDateOnly(r.dueBackOn) : '—'),
            },
            {
              key: 'status',
              header: 'Status',
              width: 110,
              render: (r) => <StatusBadge status={r.status} tone={statusTone(r.status)} />,
            },
          ]}
          rowActions={(r) => (
            <span style={{ display: 'inline-flex', gap: 4 }}>
              {r.status === 'Requested' && (
                <Button size="small" appearance="primary" icon={<SendRegular />} onClick={() => setIssuing(r)}>
                  Issue
                </Button>
              )}
              {(r.status === 'Issued' || r.status === 'Overdue') && (
                <Button size="small" icon={<ArrowUndoRegular />} onClick={() => setReturning(r)}>
                  Returned
                </Button>
              )}
              {r.status === 'Requested' && (
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DismissRegular />}
                  aria-label={`Cancel ${r.requestNumber}`}
                  onClick={() => setCancelling(r)}
                />
              )}
            </span>
          )}
          emptyTitle="No record requests"
          emptyDescription="Use New request when a department or an outside agency asks for a patient file."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={requests.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      <NewRequestDialog open={creating} onClose={() => setCreating(false)} />
      <IssueDialog
        request={issuing}
        busy={issue.isPending}
        onConfirm={(dueBackOn) => issuing && issue.mutate({ id: issuing.id, dueBackOn })}
        onCancel={() => setIssuing(null)}
      />
      <ConfirmDialog
        open={returning !== null}
        title="Record the file as returned?"
        confirmLabel="Returned"
        busy={giveBack.isPending}
        onConfirm={() => returning && giveBack.mutate(returning.id)}
        onCancel={() => setReturning(null)}
      >
        {returning
          ? `${returning.patientName ?? 'The patient'}'s file (${returning.requestNumber}) is back in medical records.`
          : ''}
      </ConfirmDialog>
      <ReasonDialog
        open={cancelling !== null}
        title={`Cancel ${cancelling?.requestNumber ?? 'request'}?`}
        confirmLabel="Cancel request"
        destructive
        busy={cancel.isPending}
        onConfirm={(reason) => cancelling && cancel.mutate({ id: cancelling.id, reason })}
        onCancel={() => setCancelling(null)}
      />
    </Page>
  );
}
