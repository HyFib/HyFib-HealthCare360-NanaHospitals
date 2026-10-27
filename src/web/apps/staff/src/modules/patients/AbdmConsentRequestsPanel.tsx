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
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DocumentArrowDownRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, isApiError } from '@hc360/core';
import { DataTable, StatusBadge, useNotify } from '@hc360/ui';
import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';
import { useFeatureOn } from '@/lib/features';
import { usePermission } from '@/lib/permissions';
import { AbdmReceivedRecords } from './AbdmReceivedRecords';
import type { PatientDetail } from './api';

export type AbdmConsentRequestStatus = 'Sent' | 'Requested' | 'Granted' | 'Denied' | 'Revoked' | 'Expired' | 'Failed';

export type AbdmTransferStatus = 'Sent' | 'Requested' | 'Received' | 'Failed';

export interface AbdmConsentArtefact {
  artefactId: string;
  status: string;
  hipId: string | null;
  hipName: string | null;
  records: number;
  fetchedAt: string | null;
  transfer: AbdmTransferStatus | null;
  transferError: string | null;
  received: number;
}

export interface AbdmConsentRequest {
  id: string;
  patientId: string;
  status: AbdmConsentRequestStatus;
  purpose: string;
  hiTypes: string[];
  dateFrom: string;
  dateTo: string;
  dataEraseAt: string;
  requesterName: string;
  requestedAt: string;
  decidedAt: string | null;
  error: string | null;
  artefacts: AbdmConsentArtefact[];
}

/** ABDM's kinds of health information, as the doctor chooses them. */
const HI_TYPES: { value: string; label: string }[] = [
  { value: 'OPConsultation', label: 'Consultations' },
  { value: 'Prescription', label: 'Prescriptions' },
  { value: 'DischargeSummary', label: 'Discharge summaries' },
  { value: 'DiagnosticReport', label: 'Lab and imaging reports' },
  { value: 'ImmunizationRecord', label: 'Immunisations' },
  { value: 'HealthDocumentRecord', label: 'Other health documents' },
  { value: 'WellnessRecord', label: 'Wellness records' },
];

const PURPOSES: { value: string; label: string }[] = [
  { value: 'CAREMGT', label: 'Care management' },
  { value: 'BTG', label: 'Break the glass (emergency)' },
  { value: 'PATRQT', label: 'The patient asked' },
];

const STATUS_TEXT: Record<AbdmConsentRequestStatus, string> = {
  Sent: 'Sent',
  Requested: 'Waiting for the patient',
  Granted: 'Granted',
  Denied: 'Denied',
  Revoked: 'Revoked',
  Expired: 'Expired',
  Failed: 'Not sent',
};

const consentRequestQueries = {
  list: (patientId: string) =>
    queryOptions({
      queryKey: ['abdm', 'consent-requests', patientId],
      queryFn: ({ signal }) =>
        api.get<AbdmConsentRequest[]>(`/api/v1/abdm/patients/${patientId}/consent-requests`, { signal }),
    }),
};

const useStyles = makeStyles({
  intro: { display: 'flex', flexDirection: 'column', gap: '8px', marginBottom: '12px', maxWidth: '72ch' },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  types: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
  dates: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  heading: { display: 'block', marginTop: '20px', marginBottom: '8px' },
});

function transferText(a: AbdmConsentArtefact): string {
  switch (a.transfer) {
    case 'Received':
      return `${a.received} received`;
    case 'Failed':
      return `not received${a.transferError ? `: ${a.transferError}` : ''}`;
    default:
      return 'records on their way';
  }
}

const iso = (date: Date) => date.toISOString().slice(0, 10);
const typeLabel = (value: string) => HI_TYPES.find((t) => t.value === value)?.label ?? value;

/**
 * Asking the patient, through ABDM, to let the hospital see their records at other providers (the hospital as HIU,
 * ABDM M3). The doctor chooses why, which kinds of records and from when; the patient decides in their ABHA app; a
 * granted request lists each provider whose records it covers. Shown while ABDM is switched on, to doctors.
 */
export function AbdmConsentRequestsPanel({ patient }: { patient: PatientDetail }) {
  const styles = useStyles();
  const switchedOn = useFeatureOn('abdm');
  const canRequest = usePermission('abdm.records.request');
  const requests = useQuery({ ...consentRequestQueries.list(patient.id), enabled: switchedOn && canRequest });
  const [open, setOpen] = useState(false);
  const notify = useNotify();
  const queryClient = useQueryClient();

  const askAgain = useMutation({
    mutationFn: (artefactId: string) =>
      api.post(
        `/api/v1/abdm/patients/${patient.id}/consent-artefacts/${encodeURIComponent(artefactId)}/records-requests`,
      ),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['abdm', 'consent-requests', patient.id] });
      notify.success('Records asked for', 'They appear here when the provider sends them.');
    },
    onError: (error) => notify.error(error, 'Could not ask for the records'),
  });

  if (!switchedOn || !canRequest) {
    return null;
  }

  return (
    <>
      <div className={styles.intro}>
        <Caption1 className={styles.muted}>
          Ask the patient to let this hospital see their records at other hospitals and labs. The patient decides in
          their ABHA app; the request is in your name.
        </Caption1>
        {!patient.abhaAddress ? (
          <MessageBar intent="info">
            <MessageBarBody>Add the patient's ABHA address to their record to ask for their records.</MessageBarBody>
          </MessageBar>
        ) : (
          !patient.mergedIntoPatientId && (
            <div>
              <Button icon={<DocumentArrowDownRegular />} onClick={() => setOpen(true)}>
                Ask for records through ABDM
              </Button>
            </div>
          )
        )}
      </div>
      <DataTable
        ariaLabel="ABDM requests for the patient's records"
        rows={requests.data}
        loading={requests.isLoading}
        error={requests.error}
        onRetry={() => void requests.refetch()}
        getRowId={(r) => r.id}
        columns={[
          { key: 'when', header: 'Asked', width: 160, render: (r) => formatDateTime(r.requestedAt) },
          {
            key: 'what',
            header: 'Records',
            render: (r) => (
              <div className={styles.cell}>
                <Text>{r.hiTypes.map(typeLabel).join(', ')}</Text>
                <Caption1 className={styles.muted}>
                  {formatDateOnly(r.dateFrom.slice(0, 10))} to {formatDateOnly(r.dateTo.slice(0, 10))} · kept until{' '}
                  {formatDateOnly(r.dataEraseAt.slice(0, 10))} · {r.requesterName}
                </Caption1>
              </div>
            ),
          },
          {
            key: 'status',
            header: 'Status',
            render: (r) => (
              <div className={styles.cell}>
                <StatusBadge status={STATUS_TEXT[r.status]} />
                {r.error && <Caption1 className={styles.muted}>{r.error}</Caption1>}
                {r.artefacts.map((a) => (
                  <div key={a.artefactId}>
                    <Caption1>
                      {a.hipName ?? a.hipId ?? 'A provider'}
                      {a.fetchedAt ? `: ${a.records} ${a.records === 1 ? 'record' : 'records'}` : ': details awaited'}
                      {a.status !== 'GRANTED' && a.status !== 'REQUESTED' ? ` (${a.status.toLowerCase()})` : ''}
                      {a.status === 'GRANTED' && a.transfer ? ` · ${transferText(a)}` : ''}
                    </Caption1>
                    {a.status === 'GRANTED' && a.transfer === 'Failed' && (
                      <Button
                        size="small"
                        appearance="subtle"
                        disabled={askAgain.isPending}
                        onClick={() => askAgain.mutate(a.artefactId)}
                      >
                        Ask again
                      </Button>
                    )}
                  </div>
                ))}
              </div>
            ),
          },
        ]}
        emptyTitle="No records have been asked for through ABDM"
        size="small"
      />
      <Text as="h3" weight="semibold" className={styles.heading}>
        Records received
      </Text>
      <AbdmReceivedRecords patientId={patient.id} />
      {open && <AskDialog patientId={patient.id} onClose={() => setOpen(false)} />}
    </>
  );
}

function AskDialog({ patientId, onClose }: { patientId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = new Date();
  const [purpose, setPurpose] = useState('CAREMGT');
  const [types, setTypes] = useState<string[]>([
    'OPConsultation',
    'Prescription',
    'DischargeSummary',
    'DiagnosticReport',
  ]);
  const [from, setFrom] = useState(iso(new Date(today.getFullYear() - 2, today.getMonth(), today.getDate())));
  const [to, setTo] = useState(iso(today));
  const [eraseAfter, setEraseAfter] = useState(iso(new Date(today.getTime() + 30 * 24 * 3600 * 1000)));
  const [problem, setProblem] = useState<string | null>(null);

  const ask = useMutation({
    mutationFn: () =>
      api.post<AbdmConsentRequest>(`/api/v1/abdm/patients/${patientId}/consent-requests`, {
        purpose,
        hiTypes: types,
        from,
        to,
        eraseAfter,
      }),
    onSuccess: (sent) => {
      void queryClient.invalidateQueries({ queryKey: ['abdm', 'consent-requests', patientId] });
      if (sent.status === 'Failed') {
        setProblem(sent.error ?? 'The request could not be sent.');
        return;
      }
      notify.success('Request sent', 'The patient decides in their ABHA app.');
      onClose();
    },
    onError: (error) => setProblem(isApiError(error) ? error.message : 'The request could not be sent.'),
  });

  const toggle = (value: string, checked: boolean) =>
    setTypes((current) => (checked ? [...current, value] : current.filter((t) => t !== value)));

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Ask for records through ABDM</DialogTitle>
          <DialogContent className={styles.form}>
            <Field label="Why the records are needed" required>
              <Select value={purpose} onChange={(_, d) => setPurpose(d.value)}>
                {PURPOSES.map((p) => (
                  <option key={p.value} value={p.value}>
                    {p.label}
                  </option>
                ))}
              </Select>
            </Field>
            {/* A group, not a Field: a Field would give every checkbox its label as the checkbox's own name. */}
            <div role="group" aria-labelledby="abdm-kinds">
              <Text id="abdm-kinds" weight="semibold">
                Kinds of records
              </Text>
              <div className={styles.types}>
                {HI_TYPES.map((t) => (
                  <Checkbox
                    key={t.value}
                    label={t.label}
                    checked={types.includes(t.value)}
                    onChange={(_, d) => toggle(t.value, d.checked === true)}
                  />
                ))}
              </div>
            </div>
            <div className={styles.dates}>
              <Field label="From" required>
                <Input type="date" value={from} onChange={(_, d) => setFrom(d.value)} />
              </Field>
              <Field label="To" required>
                <Input type="date" value={to} onChange={(_, d) => setTo(d.value)} />
              </Field>
              <Field label="Keep until" required hint="At most a year">
                <Input type="date" value={eraseAfter} onChange={(_, d) => setEraseAfter(d.value)} />
              </Field>
            </div>
            {problem && (
              <MessageBar intent="error">
                <MessageBarBody>{problem}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button
              appearance="primary"
              disabled={types.length === 0 || !from || !to || !eraseAfter || ask.isPending}
              onClick={() => ask.mutate()}
            >
              Send request
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
