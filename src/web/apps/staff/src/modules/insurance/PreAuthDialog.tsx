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
  Select,
  Spinner,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { formatDate, formatDateOnly, isApiError } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { ipQueries } from '@/modules/billing/ipApi';
import { amountInputText, parseRupees } from '@/modules/billing/money';
import { ActionError, AmountField } from '@/modules/billing/ui';
import { patientQueries } from '@/modules/patients/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { insuranceApi, insuranceKeys, insuranceQueries, type PreAuthDetail } from './api';
import { Attachments } from './Attachments';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 640px)': { gridTemplateColumns: '1fr' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Cashless pre-authorisation request (INS-04): the policy, the admission, the estimate, codes and clinical summary, and
 * supporting documents. Saved as a draft; submitting it to the insurer is a separate step.
 */
export function PreAuthDialog({
  preAuth,
  patient: presetPatient,
  onClose,
  onSaved,
}: {
  preAuth: PreAuthDetail | null;
  patient?: PickedPatient | null;
  onClose: () => void;
  onSaved: (id: string) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canSeeAdmissions = usePermission(['billing.ip.manage', 'billing.charges.read']);
  const [patient, setPatient] = useState<PickedPatient | null>(
    preAuth
      ? { id: preAuth.patientId, uhid: preAuth.uhid ?? '', fullName: preAuth.patientName ?? '' }
      : (presetPatient ?? null),
  );
  const [policyId, setPolicyId] = useState(preAuth?.policyId ?? '');
  const [admissionId, setAdmissionId] = useState(preAuth?.admissionId ?? '');
  const [amount, setAmount] = useState(preAuth ? amountInputText(preAuth.requestedAmount) : '');
  const [stay, setStay] = useState(preAuth?.expectedStayDays ? String(preAuth.expectedStayDays) : '');
  const [diagnosis, setDiagnosis] = useState(preAuth?.diagnosisCodes ?? '');
  const [procedures, setProcedures] = useState(preAuth?.procedureCodes ?? '');
  const [summary, setSummary] = useState(preAuth?.clinicalSummary ?? '');
  const [documents, setDocuments] = useState<string[]>(preAuth?.documentFileIds ?? []);
  // The version the fields were filled from: the pre-authorisation page reads it again in the background, and a newer
  // version must not be sent with these values (REL-04).
  const [loadedVersion] = useState(preAuth?.version ?? null);

  const policies = useQuery({ ...insuranceQueries.policies(patient?.id ?? ''), enabled: patient !== null });
  const patientDetail = useQuery({
    ...patientQueries.detail(patient?.id ?? ''),
    enabled: patient !== null && canSeeAdmissions,
  });
  const admissions = useQuery({
    ...ipQueries.accounts({ search: patientDetail.data?.uhid }),
    enabled: canSeeAdmissions && Boolean(patientDetail.data?.uhid),
  });
  const patientAdmissions = (admissions.data?.items ?? []).filter((a) => a.patientId === patient?.id);

  const parsedAmount = amount.trim() ? parseRupees(amount, { allowZero: true }) : null;
  const stayDays = stay.trim() ? Number(stay) : null;
  const stayOk = stayDays === null || (Number.isInteger(stayDays) && stayDays >= 1 && stayDays <= 365);
  const valid = patient !== null && policyId !== '' && (parsedAmount === null || parsedAmount.ok) && stayOk;

  const save = useMutation({
    mutationFn: async () => {
      const request = {
        policyId,
        admissionId: admissionId || null,
        visitId: preAuth?.visitId ?? null,
        requestedAmount: parsedAmount?.ok ? parsedAmount.rupees : 0,
        diagnosisCodes: diagnosis.trim() || null,
        procedureCodes: procedures.trim() || null,
        clinicalSummary: summary.trim() || null,
        expectedStayDays: stayDays,
        documentFileIds: documents,
        version: loadedVersion,
      };
      if (preAuth) {
        await insuranceApi.updatePreAuth(preAuth.id, request);
        return preAuth.id;
      }
      return (await insuranceApi.createPreAuth(request)).id;
    },
    onSuccess: (id) => {
      notify.success(
        preAuth ? 'Pre-authorisation saved' : 'Pre-authorisation drafted',
        'Submit it to the insurer when the details are complete.',
      );
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.preAuths });
      onSaved(id);
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'concurrency') {
        // Someone else changed it meanwhile: opened again, the form shows their version.
        void queryClient.invalidateQueries({ queryKey: insuranceKeys.preAuths });
      }
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({
      patient,
      policyId,
      admissionId,
      amount,
      stay,
      diagnosis,
      procedures,
      summary,
      documents,
    }),
    busy: save.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760, width: '100%' }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              {preAuth ? `Pre-authorisation ${preAuth.number || '(draft)'}` : 'New pre-authorisation'}
            </DialogTitle>
            <DialogContent className={styles.body}>
              {!preAuth && !presetPatient ? (
                <PatientPicker
                  value={patient}
                  onChange={(p) => {
                    setPatient(p);
                    setPolicyId('');
                    setAdmissionId('');
                  }}
                  required
                />
              ) : (
                <Caption1>
                  {patient?.fullName} · UHID {patient?.uhid}
                </Caption1>
              )}
              {patient && (
                <div className={styles.grid}>
                  <Field
                    label="Policy"
                    required
                    hint={
                      preAuth
                        ? 'To use another policy, create a new pre-authorisation.'
                        : policies.data && policies.data.length === 0
                          ? 'No active policy: record it on the Policies screen first.'
                          : undefined
                    }
                  >
                    <Select value={policyId} onChange={(_, d) => setPolicyId(d.value)} disabled={Boolean(preAuth)}>
                      <option value="">Choose…</option>
                      {(policies.data ?? []).map((p) => (
                        <option key={p.id} value={p.id}>
                          {p.payerName} · {p.policyNumberMasked}
                          {p.tpaName ? ` · TPA ${p.tpaName}` : ''} · to {formatDateOnly(p.validTo)}
                          {p.isValidToday ? '' : ' (not valid today)'}
                        </option>
                      ))}
                    </Select>
                  </Field>
                  <Field label="Admission" hint={canSeeAdmissions ? 'Leave empty for a planned admission' : undefined}>
                    <Select value={admissionId} onChange={(_, d) => setAdmissionId(d.value)}>
                      <option value="">Planned / not admitted yet</option>
                      {preAuth?.admissionId &&
                        !patientAdmissions.some((a) => a.admissionId === preAuth.admissionId) && (
                          <option value={preAuth.admissionId}>Linked admission</option>
                        )}
                      {patientAdmissions.map((a) => (
                        <option key={a.admissionId} value={a.admissionId}>
                          {a.admissionNumber} · admitted {formatDate(a.admittedAt)}
                        </option>
                      ))}
                    </Select>
                  </Field>
                  <AmountField
                    label="Estimated cost"
                    required
                    value={amount}
                    onChange={setAmount}
                    error={parsedAmount && !parsedAmount.ok ? parsedAmount.error : null}
                    hint="What you ask the insurer to approve"
                    autoFocus={Boolean(preAuth)}
                  />
                  <Field
                    label="Expected stay (days)"
                    validationMessage={stayOk ? undefined : 'Between 1 and 365 days'}
                    validationState={stayOk ? 'none' : 'error'}
                  >
                    <Input value={stay} onChange={(_, d) => setStay(d.value.replace(/\D/g, ''))} inputMode="numeric" />
                  </Field>
                </div>
              )}
              <Field label="Diagnosis (ICD-10)" hint="Codes with descriptions, e.g. K35.8 Acute appendicitis">
                <Textarea value={diagnosis} onChange={(_, d) => setDiagnosis(d.value)} maxLength={1000} rows={2} />
              </Field>
              <Field label="Planned procedures" hint="e.g. 0DTJ4ZZ Laparoscopic appendicectomy">
                <Textarea value={procedures} onChange={(_, d) => setProcedures(d.value)} maxLength={1000} rows={2} />
              </Field>
              <Field label="Clinical summary" hint="Presenting complaints, findings and why admission is needed">
                <Textarea value={summary} onChange={(_, d) => setSummary(d.value)} maxLength={4000} rows={4} />
              </Field>
              {patient && (
                <Field label="Supporting documents" hint="Doctor's note, investigation reports, ID and policy card">
                  <Attachments fileIds={documents} patientId={patient.id} onChange={setDocuments} />
                </Field>
              )}
              {parsedAmount?.ok && parsedAmount.rupees === 0 && (
                <MessageBar intent="info">
                  <MessageBarBody>
                    You can save a draft without an estimate, but it must be filled in before the request is submitted.
                  </MessageBarBody>
                </MessageBar>
              )}
              <Caption1 className={styles.muted}>
                Saved as a draft. Nothing is sent to the insurer until you submit it.
              </Caption1>
              <ActionError error={save.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {preAuth ? 'Save' : 'Save draft'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
