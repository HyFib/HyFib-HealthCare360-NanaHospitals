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
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { FingerprintRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { bridge, formatDateTime, trainingCapture, type AadhaarCaptureSetup } from '@hc360/core';
import { CloseRequestScope, DataTable, EmptyState, StatusBadge, useCloseRequests, useUnsavedChanges } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  identityCheckApi,
  patientKeys,
  patientQueries,
  type IdentityCheckDto,
  type PatientDetail,
  type VerifyIdentityRequest,
} from './api';
import {
  aadhaarNumberKind,
  captureProblem,
  captureText,
  deviceText,
  missingStep,
  numberMismatch,
  numberText,
  OUTCOME,
  referenceText,
  verifyProblem,
} from './identityCheck';

const useStyles = makeStyles({
  intro: { display: 'flex', flexDirection: 'column', gap: '8px', marginBottom: '12px', maxWidth: '72ch' },
  toolbar: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', marginBottom: '12px' },
  cell: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    paddingBlock: '6px',
    whiteSpace: 'normal',
    overflowWrap: 'anywhere',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  capture: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
});

type CheckedPatient = Pick<PatientDetail, 'id' | 'fullName' | 'aadhaarLast4' | 'mergedIntoPatientId'>;

/**
 * Checks of the patient's identity with their Aadhaar fingerprint (SRS INT-BIO, optional patient identity
 * verification). Staff with `patients.identity.verify`, at an HC360 Desktop workstation with a UIDAI-certified reader,
 * take the patient's Aadhaar number (or Virtual ID) and consent, capture a finger, and the server asks UIDAI through the
 * hospital's AUA whether it is the holder's. Each answer stays here without the number. Until an administrator sets up
 * Aadhaar authentication the panel says so; in training, a simulator answers instead of UIDAI.
 */
export function IdentityChecksPanel({ patient }: { patient: CheckedPatient }) {
  const styles = useStyles();
  const canRead = usePermission('patients.read');
  const canVerify = usePermission('patients.identity.verify');
  const checks = useQuery({ ...patientQueries.identityChecks(patient.id), enabled: canRead });
  const setup = useQuery({ ...patientQueries.identitySetup(), enabled: canVerify });
  const [checking, setChecking] = useState(false);

  if (!canRead) {
    return (
      <EmptyState
        title="Identity checks are not available to you"
        description="Ask your administrator for access to patient records."
        icon={<ShieldKeyholeRegular />}
      />
    );
  }

  const ready = setup.data && setup.data.provider !== null && !setup.data.problem;
  return (
    <>
      <div className={styles.intro}>
        <Caption1 className={styles.muted}>
          With the patient’s consent, UIDAI confirms that their fingerprint is that of the Aadhaar number they give.
          Only the answer and the number’s last 4 digits are kept; the fingerprint goes to UIDAI encrypted, and is not
          kept.
        </Caption1>
        {canVerify && setup.data?.problem && (
          <MessageBar intent="info" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Fingerprint checks are not available</MessageBarTitle>
              {setup.data.problem}
            </MessageBarBody>
          </MessageBar>
        )}
        {canVerify && ready && setup.data.training && (
          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Training</MessageBarTitle>A simulator answers instead of UIDAI: no fingerprint is really
              checked. Do not rely on these checks.
            </MessageBarBody>
          </MessageBar>
        )}
      </div>
      {canVerify && ready && !patient.mergedIntoPatientId && (
        <div className={styles.toolbar}>
          <Button appearance="primary" icon={<FingerprintRegular />} onClick={() => setChecking(true)}>
            Check identity with fingerprint
          </Button>
        </div>
      )}
      <DataTable
        ariaLabel="Identity checks"
        rows={checks.data}
        loading={checks.isLoading}
        error={checks.error}
        onRetry={() => void checks.refetch()}
        getRowId={(c) => c.id}
        size="small"
        columns={[
          {
            key: 'when',
            header: 'Checked',
            render: (c) => (
              <span className={styles.cell}>
                <Text>{formatDateTime(c.checkedAt)}</Text>
                {c.checkedByName && <Caption1 className={styles.muted}>by {c.checkedByName}</Caption1>}
              </span>
            ),
          },
          { key: 'outcome', header: 'Result', render: (c) => <Outcome check={c} /> },
          {
            key: 'number',
            header: 'Number',
            render: (c) => (
              <span className={styles.cell}>
                <Text>{numberText(c)}</Text>
                {deviceText({ providerId: c.deviceProviderId, modelId: c.deviceModelId }) && (
                  <Caption1 className={styles.muted}>
                    {deviceText({ providerId: c.deviceProviderId, modelId: c.deviceModelId })}
                  </Caption1>
                )}
              </span>
            ),
          },
          { key: 'reference', header: 'Reference', render: (c) => <Caption1>{referenceText(c)}</Caption1> },
        ]}
        emptyTitle="No identity checks"
        emptyDescription={
          canVerify && ready
            ? 'Check the patient’s identity with their Aadhaar fingerprint when they agree to it.'
            : 'Checks of the patient’s identity with their Aadhaar fingerprint appear here.'
        }
      />
      {ready && (
        <IdentityCheckDialog patient={patient} setup={setup.data} open={checking} onClose={() => setChecking(false)} />
      )}
    </>
  );
}

function Outcome({ check }: { check: IdentityCheckDto }) {
  const styles = useStyles();
  const outcome = OUTCOME[check.outcome];
  return (
    <span className={styles.cell}>
      <StatusBadge status={check.outcome} tone={outcome.tone} label={outcome.label} />
      <Caption1 className={styles.muted}>{check.message}</Caption1>
    </span>
  );
}

function IdentityCheckDialog({
  patient,
  setup,
  open,
  onClose,
}: {
  patient: CheckedPatient;
  setup: AadhaarCaptureSetup;
  open: boolean;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <CloseRequestScope value={closing}>
          {open && <IdentityCheckForm patient={patient} setup={setup} onClose={onClose} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

interface Capture {
  pidData: string;
  text: string;
}

/** The check, from the number to UIDAI's answer. The dialog mounts it each time it opens, so it always starts empty. */
export function IdentityCheckForm({
  patient,
  setup,
  onClose,
}: {
  patient: CheckedPatient;
  setup: AadhaarCaptureSetup;
  onClose: () => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [aadhaarNumber, setAadhaarNumber] = useState('');
  const [consent, setConsent] = useState(false);
  const [capture, setCapture] = useState<Capture | null>(null);
  const [result, setResult] = useState<IdentityCheckDto | null>(null);

  const capturing = useMutation({
    mutationFn: () => bridge.captureFingerprint(setup),
    onMutate: () => setCapture(null),
    onSuccess: (captured) => setCapture({ pidData: captured.pidData, text: captureText(captured) }),
  });

  const verify = useMutation({
    mutationFn: (request: VerifyIdentityRequest) => identityCheckApi.verify(patient.id, request),
    onSuccess: (check) => {
      // UIDAI has seen this capture: a new check needs a new finger.
      setResult(check);
      setCapture(null);
      void queryClient.invalidateQueries({ queryKey: patientKeys.identityChecks(patient.id) });
      if (check.linkedAadhaar) {
        void queryClient.invalidateQueries({ queryKey: patientKeys.detail(patient.id) });
      }
    },
  });

  const busy = capturing.isPending || verify.isPending;
  const guard = useUnsavedChanges({ dirty: aadhaarNumber.trim() !== '' && result === null, busy, onClose });
  const missing = missingStep({ aadhaarNumber, consent, pidData: capture?.pidData ?? null });
  const mismatch = numberMismatch(aadhaarNumber.trim(), patient.aadhaarLast4);
  const numberInvalid = aadhaarNumber.trim() !== '' && aadhaarNumberKind(aadhaarNumber.trim()) === null;

  const takeTrainingCapture = (matches: boolean) => {
    capturing.reset();
    setCapture({
      pidData: trainingCapture(matches),
      text: matches ? 'Training capture: a finger that matches' : 'Training capture: a finger that does not match',
    });
  };

  const startAgain = () => {
    setResult(null);
    setConsent(false);
    verify.reset();
    capturing.reset();
  };

  if (result) {
    const outcome = OUTCOME[result.outcome];
    return (
      <DialogBody>
        <DialogTitle>{outcome.label}</DialogTitle>
        <DialogContent className={styles.form}>
          <MessageBar
            intent={result.outcome === 'Verified' ? 'success' : result.outcome === 'NotVerified' ? 'error' : 'warning'}
            layout="multiline"
            politeness="assertive"
          >
            <MessageBarBody>
              <MessageBarTitle>{outcome.label}</MessageBarTitle>
              {result.message}
            </MessageBarBody>
          </MessageBar>
          <Text>{numberText(result)}</Text>
          <Caption1 className={styles.muted}>{referenceText(result)}</Caption1>
        </DialogContent>
        <DialogActions>
          {result.outcome !== 'Verified' && <Button onClick={startAgain}>Check again</Button>}
          <Button appearance="primary" onClick={onClose}>
            Close
          </Button>
        </DialogActions>
      </DialogBody>
    );
  }

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (!missing && capture) {
          verify.mutate({ aadhaarNumber: aadhaarNumber.replace(/\D/g, ''), pidData: capture.pidData, consent });
        }
      }}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Check identity with fingerprint</DialogTitle>
        <DialogContent className={styles.form}>
          <Text block>
            UIDAI confirms whether the finger is that of {patient.fullName}’s Aadhaar number. It answers yes or no; it
            sends no details about the patient.
          </Text>
          <Field
            label="Aadhaar number or Virtual ID"
            required
            hint={
              patient.aadhaarLast4
                ? `The record has an Aadhaar number ending in ${patient.aadhaarLast4}.`
                : 'If UIDAI confirms the number, it is linked to the record (only its last 4 digits are kept).'
            }
            validationState={numberInvalid || mismatch ? 'error' : 'none'}
            validationMessage={
              numberInvalid ? 'Enter the 12-digit Aadhaar number or the 16-digit Virtual ID.' : (mismatch ?? undefined)
            }
          >
            <Input
              value={aadhaarNumber}
              onChange={(_, data) => setAadhaarNumber(data.value)}
              inputMode="numeric"
              autoComplete="off"
              maxLength={19}
              disabled={busy}
            />
          </Field>
          <Field required>
            <Checkbox
              checked={consent}
              disabled={busy}
              onChange={(_, data) => setConsent(data.checked === true)}
              label="The patient agreed to Aadhaar authentication. I told them it only confirms their identity for the hospital’s records, that their fingerprint goes to UIDAI encrypted and is not kept, and that they may show another identity document instead."
            />
          </Field>
          <div className={styles.capture}>
            <Button
              icon={capturing.isPending ? <Spinner size="tiny" aria-hidden /> : <FingerprintRegular />}
              disabled={busy}
              onClick={() => capturing.mutate()}
            >
              {capture ? 'Capture again' : 'Capture fingerprint'}
            </Button>
            {setup.training && (
              <>
                <Button appearance="subtle" disabled={busy} onClick={() => takeTrainingCapture(true)}>
                  Training: a finger that matches
                </Button>
                <Button appearance="subtle" disabled={busy} onClick={() => takeTrainingCapture(false)}>
                  Training: one that does not
                </Button>
              </>
            )}
          </div>
          {capturing.isPending && (
            <Text role="status">
              Ask the patient to place a finger on the reader. It waits {setup.timeoutSeconds} seconds.
            </Text>
          )}
          {capture && <Text role="status">{capture.text}</Text>}
          {capturing.error && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>The fingerprint was not captured</MessageBarTitle>
                {captureProblem(capturing.error, setup)}
              </MessageBarBody>
            </MessageBar>
          )}
          {verify.error && (
            <MessageBar intent="error" layout="multiline" politeness="assertive">
              <MessageBarBody>
                <MessageBarTitle>The identity was not checked</MessageBarTitle>
                {verifyProblem(verify.error)}
              </MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={missing !== null || busy}
            title={missing ?? undefined}
            icon={verify.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {setup.training ? 'Check (training)' : 'Check with UIDAI'}
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
