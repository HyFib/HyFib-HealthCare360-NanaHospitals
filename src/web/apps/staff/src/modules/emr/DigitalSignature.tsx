import {
  Badge,
  Button,
  Caption1,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowDownloadRegular, CertificateRegular } from '@fluentui/react-icons';
import { bridge, formatDateTime, saveFile } from '@hc360/core';
import { Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { DocumentButtons } from '@/lib/DocumentButtons';
import { desktopSigningProblem, digitalSignatureBadge, signWithDsc, useIsPrescriber } from './dscSigning';
import {
  canIssueFinalCopy,
  prescriptionDocument,
  prescriptionQueries,
  prescriptionSignatureFiles,
  type DigitalSignatureState,
  type PrescriptionDto,
} from './prescriptionsApi';

export function DigitalSignatureBadge({ prescription }: { prescription: PrescriptionDto }) {
  const signature = useQuery({
    ...prescriptionQueries.signature(prescription.id),
    enabled: prescription.status !== 'Draft',
  });
  const badge = digitalSignatureBadge(signature.data);
  if (!badge) {
    return null;
  }
  return (
    <Badge appearance="tint" color={badge.color} icon={<CertificateRegular />}>
      {badge.label}
    </Badge>
  );
}

const useStyles = makeStyles({
  details: {
    display: 'grid',
    gridTemplateColumns: 'minmax(140px, max-content) 1fr',
    gap: '4px 16px',
    margin: '12px 0 0',
  },
  term: { color: tokens.colorNeutralForeground3 },
  value: { margin: 0, overflowWrap: 'anywhere' },
  mono: { fontFamily: tokens.fontFamilyMonospace, overflowWrap: 'anywhere' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', marginTop: '12px' },
});

/**
 * The prescription's DSC signature: its state as the server re-checked it, who signed with which certificate, the
 * SHA-256 of the signed bytes, and the .p7s with those bytes for an independent check. The prescribing doctor signs
 * here, in HC360 Desktop only (a browser cannot reach the DSC token); anyone else with `clinical.prescriptions.sign`
 * is told who must sign it (the server refuses them: `prescription.not_prescriber`).
 */
export function DigitalSignatureSection({
  prescription,
  canSign,
}: {
  prescription: PrescriptionDto;
  canSign: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const isPrescriber = useIsPrescriber(prescription);
  const signature = useQuery({
    ...prescriptionQueries.signature(prescription.id),
    enabled: prescription.status !== 'Draft',
  });

  const sign = useMutation({
    mutationFn: () => signWithDsc(prescription.id),
    onSuccess: (result) => {
      queryClient.setQueryData(prescriptionQueries.signature(prescription.id).queryKey, result);
      void queryClient.invalidateQueries({ queryKey: prescriptionQueries.detail(prescription.id).queryKey });
      notify.success('Signed with your DSC', result.message ?? undefined);
    },
    onError: (error) => {
      const problem = desktopSigningProblem(error);
      if (!problem) {
        notify.error(error, 'Could not sign with the DSC');
      } else if (problem.intent === 'info') {
        notify.info(problem.title, problem.body);
      } else {
        notify.warning(problem.title, problem.body);
      }
    },
  });
  const download = useMutation({
    mutationFn: async (path: string) => {
      const { blob, fileName } = await api.download(path);
      saveFile(blob, fileName ?? 'prescription-signature');
    },
    onError: (error) => notify.error(error, 'Could not download the file'),
  });

  const data = signature.data;
  if (!data || (data.state === 'NotSigned' && data.policy === 'Off')) {
    return null;
  }

  const files = prescriptionSignatureFiles(prescription.id);
  const unsigned = prescription.status === 'Signed' && data.state === 'NotSigned';
  const canSignHere = canSign && isPrescriber && unsigned;
  return (
    <Section
      title="Digital signature"
      description="The doctor's Digital Signature Certificate (DSC), under the IT Act."
    >
      {data.state === 'NotSigned' ? (
        <>
          <MessageBar intent={data.policy === 'Required' ? 'warning' : 'info'} layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Not signed with a DSC</MessageBarTitle>
              {data.policy === 'Required'
                ? 'This hospital prints, downloads and shares prescriptions only once the doctor has signed them with their DSC.'
                : 'The prescription is valid as signed in HC360; the doctor may also sign it with their DSC.'}
            </MessageBarBody>
          </MessageBar>
          {unsigned && !isPrescriber && (
            <Caption1 block className={styles.term} style={{ marginTop: 8 }}>
              Only {prescription.practitionerName ?? 'the prescribing doctor'}, who prescribed it, can sign it with
              their DSC{data.policy === 'Required' ? ', and it can be printed or given to the patient only then' : ''}.
            </Caption1>
          )}
          {canSignHere &&
            (bridge.isDesktop ? (
              <div className={styles.actions}>
                <Button
                  appearance="primary"
                  icon={<CertificateRegular />}
                  disabled={sign.isPending}
                  onClick={() => sign.mutate()}
                >
                  {sign.isPending ? 'Waiting for your DSC…' : 'Sign with DSC'}
                </Button>
              </div>
            ) : (
              <Caption1 block className={styles.term} style={{ marginTop: 8 }}>
                To sign with your DSC, open this prescription in HC360 Desktop with your DSC token inserted.
              </Caption1>
            ))}
        </>
      ) : (
        <>
          <MessageBar intent={VERDICT[data.state].intent} layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>{VERDICT[data.state].title}</MessageBarTitle>
              {data.message}
              {data.finalCopyAvailable === false &&
                ' This hospital requires a valid DSC signature, so the prescription is not printed or shared until this is resolved.'}
            </MessageBarBody>
          </MessageBar>
          <dl className={styles.details}>
            <Detail label="Signed by" value={data.signerName} />
            <Detail label="Signed on" value={data.signedAt ? formatDateTime(data.signedAt) : null} />
            <Detail label="Certificate serial" value={data.certificateSerialNumber} mono />
            <Detail label="Issued by" value={data.certificateIssuer} />
            <Detail label="Thumbprint" value={data.certificateThumbprint} mono />
            <Detail label="SHA-256 of the signed data" value={data.contentHash} mono />
            <Detail label="Certificate chain" value={data.chainStatus} />
          </dl>
          <div className={styles.actions}>
            <Button
              icon={<ArrowDownloadRegular />}
              disabled={download.isPending}
              onClick={() => download.mutate(files.signature)}
            >
              Signature (.p7s)
            </Button>
            <Button
              icon={<ArrowDownloadRegular />}
              disabled={download.isPending}
              onClick={() => download.mutate(files.content)}
            >
              Signed data (.json)
            </Button>
          </div>
        </>
      )}
    </Section>
  );
}

/**
 * What the doctor sees once the prescription is signed in HC360: print it or download the PDF — or, where the hospital
 * requires the doctor's DSC, sign it with the DSC first (the print and PDF would be refused until then).
 */
export function SignedPrescriptionNotice({
  prescription,
  onSignWithDsc,
  onDismiss,
}: {
  prescription: PrescriptionDto;
  /** Opens the prescription, where the DSC section signs it. */
  onSignWithDsc: () => void;
  onDismiss: () => void;
}) {
  const where = prescription.isInpatient ? '' : ' and on the patient portal';
  const another = (
    <Button appearance="subtle" onClick={onDismiss}>
      Write another prescription
    </Button>
  );
  if (canIssueFinalCopy(prescription)) {
    return (
      <MessageBar intent="success" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>Prescription {prescription.prescriptionNumber} signed</MessageBarTitle>
          It is in the pharmacy queue{where}. Print it or download the PDF for the patient if needed.
        </MessageBarBody>
        <MessageBarActions>
          <DocumentButtons path={prescriptionDocument(prescription.id)} printLabel="Print (A5)" />
          {another}
        </MessageBarActions>
      </MessageBar>
    );
  }
  return (
    <MessageBar intent="warning" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>
          Prescription {prescription.prescriptionNumber} signed: now sign it with your DSC
        </MessageBarTitle>
        It is in the pharmacy queue. This hospital prints, downloads and shares prescriptions only once the doctor has
        signed them with their DSC{prescription.isInpatient ? '' : ', and the patient is told then'}.
      </MessageBarBody>
      <MessageBarActions>
        <Button appearance="primary" icon={<CertificateRegular />} onClick={onSignWithDsc}>
          Sign with DSC…
        </Button>
        {another}
      </MessageBarActions>
    </MessageBar>
  );
}

/** How a signed prescription's signature reads, by its state as the server re-checked it. */
const VERDICT: Record<
  Exclude<DigitalSignatureState, 'NotSigned'>,
  { intent: 'success' | 'warning' | 'error'; title: string }
> = {
  Valid: { intent: 'success', title: 'Signed with a DSC' },
  Untrusted: { intent: 'warning', title: 'The certificate is not confirmed as a DSC' },
  Invalid: { intent: 'error', title: 'The DSC signature does not check out' },
};

function Detail({ label, value, mono }: { label: string; value: string | null; mono?: boolean }) {
  const styles = useStyles();
  if (!value) {
    return null;
  }
  return (
    <>
      <dt className={styles.term}>
        <Text size={200}>{label}</Text>
      </dt>
      <dd className={mergeClasses(styles.value, mono && styles.mono)}>
        <Text size={200}>{value}</Text>
      </dd>
    </>
  );
}
