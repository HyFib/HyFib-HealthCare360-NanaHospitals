import {
  Badge,
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
  Input,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Select,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowDownloadRegular,
  ArrowUndoRegular,
  DismissRegular,
  DocumentPdfRegular,
  FolderOpenRegular,
  PrintRegular,
  SendRegular,
  ShieldCheckmarkRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
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
import { ReasonDialog } from '@/modules/emr/ui';
import { isPdfUnavailable, useServerDocuments } from '@/lib/documents';
import {
  copyCertificatePath,
  mrdApi,
  mrdQueries,
  purposeLabel,
  RECORD_REQUEST_PURPOSES,
  RECORD_REQUEST_STATUSES,
  type RecordCopyFileDto,
  type RecordCopyMatch,
  type RecordRequestDto,
  type RecordRequestPurpose,
  type RecordRequestStatus,
  type RequestListParams,
} from '@/modules/mrd/api';
import { fileSize, normalizeSha256, sha256Hex } from '@/modules/mrd/copies';
import { type IssueChoice, IssueDialog } from '@/modules/mrd/IssueDialog';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

export const Route = createFileRoute('/_app/mrd/requests')({
  validateSearch: (s: Record<string, unknown>): RequestListParams => ({
    status: RECORD_REQUEST_STATUSES.includes(s.status as RecordRequestStatus)
      ? (s.status as RecordRequestStatus)
      : undefined,
    copyOverdue: s.copyOverdue === true ? true : undefined,
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
          icon={create.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          onClick={() => create.mutate()}
        >
          Record request
        </Button>
      </DialogActions>
      {guard.dialogs}
    </DialogBody>
  );
}

/**
 * A copy issued as documents: its files, each with the SHA-256 its certificate states, to download byte for byte as
 * issued; and the certificate under BSA 2023 s.63(4), to print for the person in charge of the server and an expert to
 * sign.
 */
function CopyDialog({ request, onClose }: { request: RecordRequestDto | null; onClose: () => void }) {
  const notify = useNotify();
  const { print, downloadPdf } = useServerDocuments();
  const download = useMutation({
    mutationFn: ({ requestId, file }: { requestId: string; file: RecordCopyFileDto }) =>
      mrdApi.downloadCopyFile(requestId, file),
    onError: (error) => notify.error(error, 'Could not download the file'),
  });
  const files = request?.files ?? [];
  return (
    <Dialog open={request !== null} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <DialogBody>
          <DialogTitle>Copy {request?.requestNumber}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>
              Each file is kept as it was handed over on {request?.issuedAt ? formatDateTime(request.issuedAt) : '—'}.
              The certificate states these values; print it for the person in charge of the server and an expert to
              sign.
            </Text>
            {files.map((f) => (
              <div key={f.id} style={{ display: 'flex', gap: 12, alignItems: 'flex-start' }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Text weight="semibold">
                    {f.sequence}. {f.title}
                  </Text>{' '}
                  <Caption1>
                    · {f.fileName} · {fileSize(f.sizeBytes)}
                  </Caption1>
                  <div>
                    <Text size={200} font="monospace" style={{ wordBreak: 'break-all' }}>
                      SHA-256 {f.sha256}
                    </Text>
                  </div>
                </div>
                <Button
                  size="small"
                  icon={
                    download.isPending && download.variables?.file.id === f.id ? (
                      <Spinner size="tiny" aria-hidden />
                    ) : (
                      <ArrowDownloadRegular />
                    )
                  }
                  disabled={download.isPending}
                  onClick={() => request && download.mutate({ requestId: request.id, file: f })}
                  aria-label={`Download ${f.fileName}`}
                >
                  Download
                </Button>
              </div>
            ))}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
            <Button
              icon={<DocumentPdfRegular />}
              disabled={!request || downloadPdf.isPending}
              onClick={() => request && downloadPdf.mutate(copyCertificatePath(request.id))}
            >
              Certificate PDF
            </Button>
            <Button
              appearance="primary"
              icon={<PrintRegular />}
              disabled={!request || print.isPending}
              onClick={() => request && print.mutate(copyCertificatePath(request.id))}
            >
              Print certificate
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/**
 * Is this file one the hospital issued? Its SHA-256 is worked out here, so the file never leaves this computer, or the
 * value from a certificate is entered; the server says which copy issued it, or that none did.
 */
function VerifyDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const notify = useNotify();
  const [value, setValue] = useState('');
  const [reading, setReading] = useState(false);
  const [checked, setChecked] = useState<{ sha256: string; matches: RecordCopyMatch[] } | null>(null);
  const hash = normalizeSha256(value);
  const verify = useMutation({
    mutationFn: (sha256: string) => mrdApi.verifyCopy(sha256),
    onSuccess: (matches, sha256) => setChecked({ sha256, matches }),
    onError: (error) => notify.error(error, 'Could not check the file'),
  });
  const close = () => {
    setValue('');
    setChecked(null);
    onClose();
  };
  const choose = async (file: File | undefined) => {
    if (!file) return;
    setReading(true);
    try {
      setValue(await sha256Hex(file));
      setChecked(null);
    } finally {
      setReading(false);
    }
  };
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && close()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Check a file against the copies issued</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label="File" hint="Its SHA-256 is worked out on this computer; the file is not sent anywhere.">
              <input type="file" aria-label="File to check" onChange={(e) => void choose(e.target.files?.[0])} />
            </Field>
            <Field
              label="SHA-256"
              hint="Or the value a certificate states"
              validationMessage={value && !hash ? 'A SHA-256 has 64 characters, 0-9 and A-F.' : undefined}
            >
              <Input
                value={value}
                onChange={(_, d) => {
                  setValue(d.value);
                  setChecked(null);
                }}
                contentBefore={reading ? <Spinner size="tiny" aria-label="Working out the SHA-256" /> : undefined}
              />
            </Field>
            {checked &&
              (checked.matches.length === 0 ? (
                <MessageBar intent="warning">
                  <MessageBarBody>No copy issued by this hospital has a file with this SHA-256.</MessageBarBody>
                </MessageBar>
              ) : (
                checked.matches.map((m) => (
                  <MessageBar key={`${m.requestId}/${m.sequence}`} intent="success" layout="multiline">
                    <MessageBarBody>
                      File {m.sequence} of copy {m.requestNumber}: {m.title} ({m.fileName}), of {m.patientName ?? '—'} (
                      {m.uhid}), issued {m.issuedAt ? formatDateTime(m.issuedAt) : '—'} to {m.requestedByName} (
                      {purposeLabel[m.purpose]}).
                    </MessageBarBody>
                  </MessageBar>
                ))
              ))}
          </DialogContent>
          <DialogActions>
            <Button onClick={close}>Close</Button>
            <Button
              appearance="primary"
              icon={verify.isPending ? <Spinner size="tiny" aria-hidden /> : <ShieldCheckmarkRegular />}
              disabled={!hash || verify.isPending}
              onClick={() => hash && verify.mutate(hash)}
            >
              Check
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/**
 * Requests for patient files (MRD-04): record, issue with a return date, track returns and overdue files. A copy for the
 * patient or a legal authority is due within 72 hours (MCI Regulations 2002, reg. 1.3.2), and those waiting past it are
 * called out. A copy issued as documents keeps each file as it was handed over, with the certificate under the Bharatiya
 * Sakshya Adhiniyam 2023 (s.63) that states their hashes, and a file brought back can be checked against it.
 */
function RecordRequestsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const requests = useQuery(mrdQueries.requests(filters));
  const overdueCopies = useQuery({ ...mrdQueries.requests({ copyOverdue: true }), enabled: !filters.copyOverdue });
  const overdueCount = filters.copyOverdue ? 0 : (overdueCopies.data?.totalCount ?? 0);
  const [creating, setCreating] = useState(false);
  const [issuing, setIssuing] = useState<RecordRequestDto | null>(null);
  const [viewing, setViewing] = useState<RecordRequestDto | null>(null);
  const [verifying, setVerifying] = useState(false);
  const [returning, setReturning] = useState<RecordRequestDto | null>(null);
  const [cancelling, setCancelling] = useState<RecordRequestDto | null>(null);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['mrd', 'requests'] });

  const issue = useMutation({
    mutationFn: ({ id, choice }: { id: string; choice: IssueChoice }) =>
      mrdApi.issueRequest(id, choice.dueBackOn, choice.documents),
    onSuccess: (r) => {
      setIssuing(null);
      const files = r.files?.length ?? 0;
      notify.success(
        files > 0 ? `Copy issued to ${r.requestedByName}` : `File issued to ${r.requestedByName}`,
        files > 0
          ? `${files === 1 ? '1 file' : `${files} files`} kept as handed over. Print the certificate from Copy.`
          : undefined,
      );
      refresh();
      if (files > 0) setViewing(r);
    },
    onError: (error) =>
      isPdfUnavailable(error)
        ? notify.warning(error.title, `${error.userMessage} Nothing was issued.`)
        : notify.error(error, 'Could not issue the file'),
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
          <span style={{ display: 'inline-flex', gap: 8 }}>
            <Button icon={<ShieldCheckmarkRegular />} onClick={() => setVerifying(true)}>
              Check a copy
            </Button>
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
              New request
            </Button>
          </span>
        }
      />
      {overdueCount > 0 && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            {overdueCount === 1 ? '1 copy' : `${overdueCount} copies`} for a patient or a court{' '}
            {overdueCount === 1 ? 'is' : 'are'} waiting past 72 hours. A copy is due within 72 hours of the request.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={() => void navigate({ search: { copyOverdue: true }, replace: true })}>Show them</Button>
          </MessageBarActions>
        </MessageBar>
      )}
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
            <Checkbox
              checked={filters.copyOverdue === true}
              onChange={(_, d) =>
                void navigate({
                  search: (prev) => ({ ...prev, copyOverdue: d.checked === true ? true : undefined, page: undefined }),
                  replace: true,
                })
              }
              label="Copies waiting past 72 hours"
            />
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
              key: 'copy',
              header: 'Copy by',
              width: 190,
              render: (r) =>
                r.copyDueAt ? (
                  <span>
                    {formatDateTime(r.copyDueAt)}
                    {r.copyLate && (
                      <Badge size="small" color="danger" appearance="tint" style={{ marginLeft: 6 }}>
                        Late
                      </Badge>
                    )}
                  </span>
                ) : (
                  '—'
                ),
            },
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
              {(r.files?.length ?? 0) > 0 && (
                <Button size="small" icon={<FolderOpenRegular />} onClick={() => setViewing(r)}>
                  Copy
                </Button>
              )}
              {(r.status === 'Issued' || r.status === 'Overdue') && r.dueBackOn && (
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
          emptyAction={
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
              New request
            </Button>
          }
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
        onConfirm={(choice) => issuing && issue.mutate({ id: issuing.id, choice })}
        onCancel={() => setIssuing(null)}
      />
      <CopyDialog request={viewing} onClose={() => setViewing(null)} />
      <VerifyDialog open={verifying} onClose={() => setVerifying(false)} />
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
