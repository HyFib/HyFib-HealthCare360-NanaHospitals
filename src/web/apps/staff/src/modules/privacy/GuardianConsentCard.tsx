import {
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  makeStyles,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular, SendRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, normalizePhone } from '@hc360/core';
import { applyServerErrors, ChoiceField, FormGrid, Section, StatusBadge, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import {
  dpdpApi,
  dpdpKeys,
  dpdpQueries,
  PURPOSE_LABELS,
  RELATIONSHIP_LABELS,
  type GuardianConsentDto,
} from './dpdpApi';
import { isChild } from './childAge';
import { PRIVACY_PERMISSIONS as P } from './labels';
import { PlainTable } from './PlainTable';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '8px', alignItems: 'flex-end', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const schema = z
  .object({
    guardianName: z.string().trim().min(1, 'Enter the parent’s or guardian’s name.').max(200),
    relationship: z.enum(['Parent', 'LawfulGuardian']),
    guardianPhone: z.string().trim().min(1, 'Enter the guardian’s mobile number.').max(20),
    method: z.enum(['Otp', 'Document']),
    documentType: z.string().trim().max(60),
    documentLast4: z.string().trim().max(4),
    guardianIsAdult: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (normalizePhone(v.guardianPhone) === null) {
      ctx.addIssue({ code: 'custom', path: ['guardianPhone'], message: 'Enter a valid mobile number.' });
    }
    if (!v.guardianIsAdult) {
      ctx.addIssue({
        code: 'custom',
        path: ['guardianIsAdult'],
        message: 'Confirm the parent or guardian is an adult.',
      });
    }
    if (v.method === 'Document') {
      if (!v.documentType) {
        ctx.addIssue({ code: 'custom', path: ['documentType'], message: 'Say which identity document you saw.' });
      }
      if (!/^[A-Za-z0-9]{4}$/.test(v.documentLast4)) {
        ctx.addIssue({
          code: 'custom',
          path: ['documentLast4'],
          message: 'Enter only its last four letters or digits.',
        });
      }
    }
  });

type Values = z.infer<typeof schema>;

/**
 * DPDP Act s.9 on the patient record: for a child, the parent’s or lawful guardian’s verifiable consent (by a code to
 * their mobile number or their identity document), and the consents received from consent managers. Health care needs
 * neither; sharing records, research and photographs wait for the guardian’s consent, and marketing is never allowed.
 */
export function GuardianConsentCard({ patientId, dateOfBirth }: { patientId: string; dateOfBirth: string | null }) {
  const canLog = usePermission(P.requestsLog);
  const canRead = usePermission(P.requestsRead);
  // Only a child's record asks: the server decides again (its own date and clock), this only saves the request for adults.
  if ((!canLog && !canRead) || !isChild(dateOfBirth)) {
    return null;
  }
  return <Card patientId={patientId} canLog={canLog} />;
}

function Card({ patientId, canLog }: { patientId: string; canLog: boolean }) {
  const styles = useStyles();
  const guardian = useQuery(dpdpQueries.guardian(patientId));
  const artefacts = useQuery(dpdpQueries.artefacts(patientId));
  if ((isApiError(guardian.error) && guardian.error.status === 403) || !guardian.data) {
    return null;
  }

  const view = guardian.data;
  const pending = view.consents.find((c) => c.status === 'Pending');
  const verified = view.consents.find((c) => c.status === 'Verified');
  const hasArtefacts = (artefacts.data?.length ?? 0) > 0;
  if (!view.isChild && !hasArtefacts) {
    return null;
  }

  return (
    <Section
      title="Consent for a child and consent managers"
      description={
        view.isChild
          ? 'The patient is under 18. Their care needs no further consent; sharing records, research and photographs wait for a parent’s or lawful guardian’s verified consent, and no marketing ever reaches them (DPDP Act s.9).'
          : 'Consents the patient gave through a consent manager registered with the Data Protection Board.'
      }
    >
      <div className={styles.stack}>
        {view.isChild && (
          <>
            {verified ? (
              <VerifiedConsent patientId={patientId} consent={verified} canLog={canLog} />
            ) : pending ? (
              <CodeEntry patientId={patientId} consent={pending} canLog={canLog} />
            ) : (
              <Text>No parent’s or guardian’s consent is verified yet.</Text>
            )}
            {canLog && !verified && !pending && <StartForm patientId={patientId} />}
          </>
        )}
        {hasArtefacts && <ArtefactTable patientId={patientId} canLog={canLog} />}
      </div>
    </Section>
  );
}

function VerifiedConsent({
  patientId,
  consent,
  canLog,
}: {
  patientId: string;
  consent: GuardianConsentDto;
  canLog: boolean;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const revoke = useMutation({
    mutationFn: () => dpdpApi.revokeGuardianConsent(patientId, consent.id, 'Withdrawn by the guardian at the desk.'),
    onSuccess: async () => {
      notify.success('Guardian’s consent withdrawn');
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.guardian(patientId) });
    },
    onError: (error) => notify.error(error, 'Could not withdraw it'),
  });
  return (
    <div>
      <StatusBadge status="Verified" tone="success" label="Guardian’s consent verified" />{' '}
      <Text>
        {consent.guardianName} ({RELATIONSHIP_LABELS[consent.relationship]}),{' '}
        {consent.method === 'Otp'
          ? `by a code sent to ${consent.guardianPhone}`
          : `by ${consent.documentType ?? 'an identity document'} ending ${consent.documentLast4 ?? ''}`}
        .
      </Text>{' '}
      {canLog && (
        <Button size="small" disabled={revoke.isPending} onClick={() => revoke.mutate()}>
          Withdraw
        </Button>
      )}
    </div>
  );
}

function CodeEntry({
  patientId,
  consent,
  canLog,
}: {
  patientId: string;
  consent: GuardianConsentDto;
  canLog: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const id = useId();
  const [code, setCode] = useState('');
  const verify = useMutation({
    mutationFn: () => dpdpApi.verifyGuardianCode(patientId, consent.id, code.trim()),
    onSuccess: async () => {
      notify.success('Guardian’s consent verified');
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.guardian(patientId) });
    },
    onError: async (error) => {
      notify.error(error, 'Not verified');
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.guardian(patientId) });
    },
  });
  return (
    <div className={styles.stack}>
      <Text>
        A code went to {consent.guardianName} on {consent.guardianPhone}. Enter it as they read it out (
        {consent.attemptsLeft} tries left).
      </Text>
      {canLog && (
        <div className={styles.row}>
          <Field label="Code from the guardian’s SMS" required>
            <Input
              id={id}
              value={code}
              inputMode="numeric"
              maxLength={6}
              autoComplete="one-time-code"
              onChange={(_, data) => setCode(data.value)}
            />
          </Field>
          <Button
            appearance="primary"
            icon={verify.isPending ? <Spinner size="tiny" aria-hidden /> : <CheckmarkRegular />}
            disabled={!/^\d{6}$/.test(code.trim()) || verify.isPending}
            onClick={() => verify.mutate()}
          >
            Verify
          </Button>
        </div>
      )}
    </div>
  );
}

function StartForm({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      guardianName: '',
      relationship: 'Parent',
      guardianPhone: '',
      method: 'Otp',
      documentType: '',
      documentLast4: '',
      guardianIsAdult: false,
    },
  });
  const method = useWatch({ control: form.control, name: 'method' });
  const start = useMutation({
    mutationFn: (v: Values) =>
      dpdpApi.startGuardianConsent(patientId, {
        guardianName: v.guardianName,
        relationship: v.relationship,
        guardianPhone: normalizePhone(v.guardianPhone) ?? v.guardianPhone,
        guardianIsAdult: v.guardianIsAdult,
        method: v.method,
        documentType: v.method === 'Document' ? v.documentType : null,
        documentLast4: v.method === 'Document' ? v.documentLast4 : null,
      }),
    onSuccess: async (consent) => {
      notify.success(consent.status === 'Verified' ? 'Guardian’s consent verified' : 'Code sent to the guardian');
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.guardian(patientId) });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not record the guardian’s consent');
      }
    },
  });
  return (
    <form onSubmit={form.handleSubmit((v) => start.mutate(v))} noValidate aria-label="Record a guardian’s consent">
      <div className={styles.stack}>
        <FormGrid columns={2} minColumnWidth={220}>
          <TextField
            control={form.control}
            name="guardianName"
            label="Parent’s or guardian’s name"
            required
            maxLength={200}
          />
          <ChoiceField
            control={form.control}
            name="relationship"
            label="Relationship"
            options={[
              { value: 'Parent', label: RELATIONSHIP_LABELS.Parent },
              { value: 'LawfulGuardian', label: RELATIONSHIP_LABELS.LawfulGuardian },
            ]}
          />
          <TextField
            control={form.control}
            name="guardianPhone"
            label="Guardian’s mobile number"
            type="tel"
            required
            maxLength={20}
          />
          <ChoiceField
            control={form.control}
            name="method"
            label="Verify by"
            options={[
              { value: 'Otp', label: 'A code sent to the guardian’s mobile number' },
              { value: 'Document', label: 'The guardian’s identity document, seen here' },
            ]}
          />
          {method === 'Document' && (
            <>
              <TextField
                control={form.control}
                name="documentType"
                label="Document"
                required
                maxLength={60}
                hint="For example Passport or Court order."
              />
              <TextField
                control={form.control}
                name="documentLast4"
                label="Last four characters of its number"
                required
                maxLength={4}
                hint="Never the whole number."
              />
            </>
          )}
        </FormGrid>
        <Controller
          control={form.control}
          name="guardianIsAdult"
          render={({ field, fieldState }) => (
            <Field validationState={fieldState.error ? 'error' : 'none'} validationMessage={fieldState.error?.message}>
              <Checkbox
                checked={field.value}
                onChange={(_, data) => field.onChange(data.checked === true)}
                label="I checked the parent or guardian is an adult"
              />
            </Field>
          )}
        />
        <div>
          <Button type="submit" appearance="primary" icon={<SendRegular />} disabled={start.isPending}>
            {method === 'Otp' ? 'Send the code' : 'Record as verified'}
          </Button>
        </div>
      </div>
    </form>
  );
}

function ArtefactTable({ patientId, canLog }: { patientId: string; canLog: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const artefacts = useQuery(dpdpQueries.artefacts(patientId));
  const withdraw = useMutation({
    mutationFn: (id: string) => dpdpApi.withdrawArtefact(id),
    onSuccess: async () => {
      notify.success('Consent withdrawn', 'The consent manager is told.');
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.artefacts(patientId) });
    },
    onError: (error) => notify.error(error, 'Could not withdraw it'),
  });
  return (
    <div className={styles.stack}>
      <Caption1 className={styles.muted}>Consents received from consent managers</Caption1>
      <PlainTable
        ariaLabel="Consents received from consent managers"
        rows={artefacts.data ?? []}
        getRowId={(row) => row.id}
        columns={[
          { key: 'purpose', header: 'For', render: (r) => PURPOSE_LABELS[r.purpose] ?? r.purpose },
          { key: 'items', header: 'Covers', render: (r) => r.dataItems.join(', ') },
          { key: 'from', header: 'From', render: (r) => r.consentManagerName, priority: 'low' },
          { key: 'until', header: 'Valid until', render: (r) => r.validUntil.slice(0, 10), width: 120 },
          {
            key: 'status',
            header: 'Status',
            width: 120,
            render: (r) => (
              <StatusBadge status={r.inForce ? 'In force' : r.status} tone={r.inForce ? 'success' : 'subtle'} />
            ),
          },
          {
            key: 'action',
            header: 'Action',
            width: 120,
            render: (r) =>
              canLog && r.status === 'Active' ? (
                <Button size="small" disabled={withdraw.isPending} onClick={() => withdraw.mutate(r.id)}>
                  Withdraw
                </Button>
              ) : null,
          },
        ]}
      />
    </div>
  );
}
