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
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ShieldCheckmarkRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { DataTable, StatusBadge } from '@hc360/ui';
import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';
import { useFeatureOn } from '@/lib/features';
import { usePermission } from '@/lib/permissions';
import type { PatientDetail } from './api';

/** Which identifier on the record ABDM sends the code for. */
export type AbhaCheckMethod = 'AbhaNumber' | 'Mobile';

export type AbhaCheckStatus = 'CodeSent' | 'Verified' | 'Mismatch' | 'Failed' | 'Expired';

export interface AbhaCheck {
  id: string;
  patientId: string;
  method: AbhaCheckMethod;
  status: AbhaCheckStatus;
  sentMessage: string | null;
  codeExpiresAt: string;
  attemptsLeft: number;
  startedAt: string;
  completedAt: string | null;
  abhaNumberMasked: string | null;
  abhaAddress: string | null;
  name: string | null;
  gender: string | null;
  yearOfBirth: number | null;
  kycVerified: boolean | null;
  differences: string[];
  error: string | null;
}

const abhaCheckQueries = {
  list: (patientId: string) =>
    queryOptions({
      queryKey: ['abdm', 'abha-checks', patientId],
      queryFn: ({ signal }) => api.get<AbhaCheck[]>(`/api/v1/abdm/patients/${patientId}/abha-checks`, { signal }),
    }),
};

const STATUS_TEXT: Record<AbhaCheckStatus, string> = {
  CodeSent: 'Waiting for the code',
  Verified: 'Matches the record',
  Mismatch: 'Differs from the record',
  Failed: 'Not checked',
  Expired: 'Code expired',
};

const useStyles = makeStyles({
  intro: { display: 'flex', flexDirection: 'column', gap: '8px', marginBlock: '24px 12px', maxWidth: '72ch' },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/**
 * Checking the patient's ABHA with ABDM (ABDM M1): ABDM sends a code to the mobile number linked to the ABHA number on
 * the record (or to the mobile number on the record); the patient reads it out; the ABHA it opens is compared with the
 * record. Shown while ABDM is switched on; checking needs `patients.identity.verify`.
 */
export function AbhaChecksPanel({ patient }: { patient: PatientDetail }) {
  const styles = useStyles();
  const switchedOn = useFeatureOn('abdm');
  const canCheck = usePermission('patients.identity.verify');
  const checks = useQuery({ ...abhaCheckQueries.list(patient.id), enabled: switchedOn });
  const [open, setOpen] = useState(false);

  if (!switchedOn) {
    return null;
  }

  return (
    <>
      <div className={styles.intro}>
        <Text weight="semibold" size={400}>
          ABHA checks with ABDM
        </Text>
        <Caption1 className={styles.muted}>
          ABDM sends a code to the mobile number linked to the patient's ABHA. When the patient reads it out, the ABHA
          is compared with this record. The record is not changed: correct it if the patient agrees.
        </Caption1>
        {canCheck && !patient.mergedIntoPatientId && (
          <div>
            <Button icon={<ShieldCheckmarkRegular />} onClick={() => setOpen(true)}>
              Check ABHA with ABDM
            </Button>
          </div>
        )}
      </div>
      <DataTable
        ariaLabel="ABHA checks"
        rows={checks.data}
        loading={checks.isLoading}
        error={checks.error}
        onRetry={() => void checks.refetch()}
        getRowId={(c) => c.id}
        columns={[
          { key: 'when', header: 'When', width: 170, render: (c) => formatDateTime(c.completedAt ?? c.startedAt) },
          {
            key: 'by',
            header: 'By',
            width: 120,
            render: (c) => (c.method === 'AbhaNumber' ? 'ABHA number' : 'Mobile number'),
          },
          {
            key: 'result',
            header: 'Result',
            render: (c) => (
              <div className={styles.cell}>
                <StatusBadge status={STATUS_TEXT[c.status]} />
                {c.differences.map((d) => (
                  <Caption1 key={d}>{d}</Caption1>
                ))}
                {c.error && <Caption1 className={styles.muted}>{c.error}</Caption1>}
              </div>
            ),
          },
          {
            key: 'abha',
            header: 'ABHA',
            priority: 'low',
            render: (c) =>
              c.abhaNumberMasked ? (
                <div className={styles.cell}>
                  <Text>{c.abhaNumberMasked}</Text>
                  {c.abhaAddress && <Caption1>{c.abhaAddress}</Caption1>}
                  {c.kycVerified && <Caption1 className={styles.muted}>KYC verified</Caption1>}
                </div>
              ) : (
                '—'
              ),
          },
        ]}
        emptyTitle="The ABHA has not been checked with ABDM"
        size="small"
      />
      {open && <AbhaCheckDialog patient={patient} onClose={() => setOpen(false)} />}
    </>
  );
}

function AbhaCheckDialog({ patient, onClose }: { patient: PatientDetail; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [method, setMethod] = useState<AbhaCheckMethod>(patient.abhaNumberMasked ? 'AbhaNumber' : 'Mobile');
  const [code, setCode] = useState('');
  const [check, setCheck] = useState<AbhaCheck | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['abdm', 'abha-checks', patient.id] });
  const message = (error: unknown) => (isApiError(error) ? error.message : 'ABDM could not be reached. Try again.');

  const send = useMutation({
    mutationFn: () => api.post<AbhaCheck>(`/api/v1/abdm/patients/${patient.id}/abha-checks`, { method }),
    onSuccess: (sent) => {
      setProblem(null);
      setCheck(sent);
      refresh();
    },
    onError: (error) => setProblem(message(error)),
  });
  const verify = useMutation({
    mutationFn: () => api.post<AbhaCheck>(`/api/v1/abdm/abha-checks/${check!.id}/code`, { code }),
    onSuccess: (done) => {
      setProblem(null);
      setCheck(done);
      refresh();
    },
    onError: (error) => {
      setProblem(message(error));
      refresh();
    },
  });

  const finished = check !== null && check.status !== 'CodeSent';
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Check ABHA with ABDM</DialogTitle>
          <DialogContent className={styles.form}>
            {!check && (
              <RadioGroup
                value={method}
                onChange={(_, d) => setMethod(d.value as AbhaCheckMethod)}
                aria-label="Send the code for"
              >
                <Radio
                  value="AbhaNumber"
                  disabled={!patient.abhaNumberMasked}
                  label={`The ABHA number on record${patient.abhaNumberMasked ? ` (${patient.abhaNumberMasked})` : ': none'}`}
                />
                <Radio value="Mobile" disabled={!patient.phone} label="The mobile number on record" />
              </RadioGroup>
            )}
            {check?.status === 'CodeSent' && (
              <>
                <Text>{check.sentMessage ?? 'ABDM sent the patient a code.'} Ask the patient to read it out.</Text>
                <Field label="Code" required>
                  <Input
                    value={code}
                    inputMode="numeric"
                    maxLength={6}
                    autoComplete="one-time-code"
                    onChange={(_, d) => setCode(d.value.replace(/\D/g, ''))}
                  />
                </Field>
              </>
            )}
            {finished && (
              <MessageBar intent={check.status === 'Verified' ? 'success' : 'warning'}>
                <MessageBarBody>
                  {STATUS_TEXT[check.status]}
                  {check.abhaNumberMasked ? ` (${check.abhaNumberMasked})` : ''}.{' '}
                  {[...check.differences, check.error].filter(Boolean).join(' ')}
                </MessageBarBody>
              </MessageBar>
            )}
            {problem && (
              <MessageBar intent="error">
                <MessageBarBody>{problem}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{finished ? 'Close' : 'Cancel'}</Button>
            {!check && (
              <Button appearance="primary" disabled={send.isPending} onClick={() => send.mutate()}>
                Send code
              </Button>
            )}
            {check?.status === 'CodeSent' && (
              <Button
                appearance="primary"
                disabled={code.length !== 6 || verify.isPending}
                onClick={() => verify.mutate()}
              >
                Check code
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
