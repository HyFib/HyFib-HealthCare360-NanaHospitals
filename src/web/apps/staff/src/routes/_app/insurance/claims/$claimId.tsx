import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  ChatHelpRegular,
  CheckmarkRegular,
  DismissRegular,
  DocumentCheckmarkRegular,
  MoneyRegular,
  ReceiptRegular,
  SaveRegular,
  SendRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, isApiError } from '@hc360/core';
import {
  DataTable,
  EmptyState,
  ErrorState,
  LoadingState,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { BillingPatientBanner } from '@/modules/billing/BillingPatientBanner';
import { ActionError, Amount, Detail, DetailGrid, NoAccess, ReasonDialog } from '@/modules/billing/ui';
import { insuranceApi, insuranceKeys, insuranceQueries, type ClaimDetail } from '@/modules/insurance/api';
import { Attachments } from '@/modules/insurance/Attachments';
import { ApproveClaimDialog, SettleClaimDialog } from '@/modules/insurance/ClaimDialogs';
import { CLAIM_STATUS_LABEL, claimTone } from '@/modules/insurance/labels';

export const Route = createFileRoute('/_app/insurance/claims/$claimId')({
  component: ClaimPage,
});

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 800px)': { gridTemplateColumns: '1fr' },
  },
  thread: { display: 'flex', flexDirection: 'column', gap: '10px' },
  bubble: { padding: '10px 12px', borderRadius: tokens.borderRadiusLarge, maxWidth: '720px', whiteSpace: 'pre-wrap' },
  insurer: { backgroundColor: tokens.colorPaletteMarigoldBackground1, alignSelf: 'flex-start' },
  hospital: { backgroundColor: tokens.colorBrandBackground2, alignSelf: 'flex-end' },
  muted: { color: tokens.colorNeutralForeground3 },
});

type Action = 'query' | 'answer' | 'approve' | 'settle' | 'reject' | null;

/** One claim (INS-05/06, MB-03): coding and documents, the insurer's questions, approval, settlements and deductions. */
function ClaimPage() {
  const styles = useStyles();
  const { claimId } = Route.useParams();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canClaims = usePermission('insurance.claims.manage');
  const [action, setAction] = useState<Action>(null);
  const claim = useQuery({ ...insuranceQueries.claim(claimId), enabled: canClaims });

  const text = useMutation({
    mutationFn: ({ kind, value }: { kind: 'query' | 'answer' | 'reject'; value: string }) =>
      kind === 'query'
        ? insuranceApi.claimQuery(claimId, value)
        : kind === 'answer'
          ? insuranceApi.claimAnswer(claimId, value)
          : insuranceApi.rejectClaim(claimId, value),
    onSuccess: (_, { kind }) => {
      notify.success(
        kind === 'query' ? 'Insurer’s question recorded' : kind === 'answer' ? 'Answer recorded' : 'Rejection recorded',
      );
      setAction(null);
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.all });
    },
  });

  if (!canClaims) {
    return (
      <Page>
        <PageHeader title="Claim" />
        <NoAccess what="insurance claims" />
      </Page>
    );
  }
  if (claim.isLoading) {
    return <LoadingState />;
  }
  if (claim.error || !claim.data) {
    return (
      <Page maxWidth={900}>
        <PageHeader
          title="Claim"
          breadcrumbs={[
            { label: 'Claims', onClick: () => void navigate({ to: '/insurance/claims' }) },
            { label: 'Claim' },
          ]}
        />
        {isApiError(claim.error) && claim.error.status === 404 ? (
          <EmptyState title="This claim was not found" />
        ) : (
          <ErrorState error={claim.error} onRetry={() => void claim.refetch()} />
        )}
      </Page>
    );
  }

  const c = claim.data;
  const closed = c.status === 'Settled' || c.status === 'Rejected';
  const canSettle = c.status === 'Submitted' || c.status === 'Approved' || c.status === 'PartiallySettled';

  return (
    <Page>
      <PageHeader
        title={`Claim ${c.claimNumber}`}
        icon={<DocumentCheckmarkRegular />}
        subtitle={
          <>
            <StatusBadge status={c.status} tone={claimTone(c.status)} label={CLAIM_STATUS_LABEL[c.status]} />{' '}
            {c.payerName} · submitted {formatDateTime(c.submittedAt)}
          </>
        }
        breadcrumbs={[
          { label: 'Claims', onClick: () => void navigate({ to: '/insurance/claims' }) },
          { label: c.claimNumber },
        ]}
        actions={
          <>
            <Button
              icon={<ReceiptRegular />}
              onClick={() => void navigate({ to: '/billing/bills/$billId', params: { billId: c.billId } })}
            >
              Bill {c.billNumber}
            </Button>
            {(c.status === 'Submitted' || c.status === 'Approved') && (
              <Button icon={<ChatHelpRegular />} onClick={() => setAction('query')}>
                Insurer asked a question
              </Button>
            )}
            {c.status === 'Query' && (
              <Button appearance="primary" icon={<SendRegular />} onClick={() => setAction('answer')}>
                Answer the question
              </Button>
            )}
            {(c.status === 'Submitted' || c.status === 'Query') && (
              <Button icon={<CheckmarkRegular />} onClick={() => setAction('approve')}>
                Approved
              </Button>
            )}
            {!closed && c.status !== 'PartiallySettled' && (
              <Button icon={<DismissRegular />} onClick={() => setAction('reject')}>
                Rejected
              </Button>
            )}
            {canSettle && (
              <Button appearance="primary" icon={<MoneyRegular />} onClick={() => setAction('settle')}>
                Record payment
              </Button>
            )}
          </>
        }
      />
      <BillingPatientBanner patientId={c.patientId} showDues={false} />
      {c.status === 'Rejected' && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Rejected</MessageBarTitle>
            {c.rejectionReason} The unpaid balance was written off as a deduction.
          </MessageBarBody>
        </MessageBar>
      )}
      <Section title="Amounts">
        <DetailGrid>
          <Detail label="Claimed">
            <Amount value={c.claimedAmount} size="large" />
          </Detail>
          <Detail label="Approved">
            {c.approvedAmount !== null ? <Amount value={c.approvedAmount} /> : 'Not yet'}
          </Detail>
          <Detail label="Received">
            <Amount value={c.settledAmount} tone="positive" />
          </Detail>
          <Detail label="Deducted">
            <Amount value={c.deductionAmount} tone={c.deductionAmount > 0 ? 'negative' : undefined} />
          </Detail>
          <Detail label="Outstanding">
            <Amount value={c.outstanding} size="large" tone={c.outstanding > 0 && !closed ? 'negative' : 'muted'} />
          </Detail>
          <Detail label="Last UTR">{c.utrNumber}</Detail>
        </DetailGrid>
      </Section>

      {(c.queryText || c.queryResponse) && (
        <Section title="Exchange with the insurer" description="The latest question and answer">
          <div className={styles.thread}>
            {c.queryText && (
              <div className={mergeClasses(styles.bubble, styles.insurer)}>
                <Caption1 block className={styles.muted}>
                  Insurer asked
                </Caption1>
                {c.queryText}
              </div>
            )}
            {c.queryResponse && (
              <div className={mergeClasses(styles.bubble, styles.hospital)}>
                <Caption1 block className={styles.muted}>
                  Hospital answered
                </Caption1>
                {c.queryResponse}
              </div>
            )}
          </div>
        </Section>
      )}

      <CodingSection claim={c} key={c.version} />

      {c.settlements.length > 0 && (
        <Section title="Payments and deductions" flush>
          <DataTable
            ariaLabel="Settlements"
            rows={c.settlements}
            getRowId={(s) => s.id}
            size="small"
            columns={[
              { key: 'on', header: 'Settled on', render: (s) => formatDateOnly(s.settledOn) },
              { key: 'amount', header: 'Received', align: 'end', render: (s) => <Amount value={s.amount} strong /> },
              {
                key: 'deducted',
                header: 'Deducted',
                align: 'end',
                render: (s) => <Amount value={s.deductionAmount} />,
              },
              { key: 'utr', header: 'UTR', render: (s) => s.utrNumber ?? '—' },
              { key: 'reference', header: 'Reference', priority: 'low', render: (s) => s.reference ?? '—' },
            ]}
          />
          {c.deductions.length > 0 && (
            <DataTable
              ariaLabel="Deductions"
              rows={c.deductions}
              getRowId={(d) => d.id}
              size="small"
              columns={[
                { key: 'reason', header: 'Deduction', render: (d) => d.reason },
                {
                  key: 'amount',
                  header: 'Amount',
                  align: 'end',
                  render: (d) => <Amount value={d.amount} tone="negative" />,
                },
              ]}
            />
          )}
        </Section>
      )}

      <ReasonDialog
        open={action === 'query' || action === 'answer'}
        title={action === 'query' ? 'What did the insurer ask?' : 'Answer to the insurer'}
        description={action === 'answer' && c.queryText ? `They asked: “${c.queryText}”` : undefined}
        label={action === 'query' ? 'Question' : 'Answer'}
        minLength={2}
        maxLength={2000}
        confirmLabel={action === 'query' ? 'Record question' : 'Record answer'}
        busy={text.isPending}
        error={text.error}
        onConfirm={(value) => text.mutate({ kind: action === 'query' ? 'query' : 'answer', value })}
        onCancel={() => setAction(null)}
      />
      <ReasonDialog
        open={action === 'reject'}
        title="Insurer rejected the claim?"
        description="A rejection writes off the whole unpaid claim as a deduction, in the day book and the receivables. It cannot be undone."
        label="Reason given by the insurer"
        maxLength={1000}
        confirmLabel="Record rejection"
        destructive
        busy={text.isPending}
        error={text.error}
        onConfirm={(value) => text.mutate({ kind: 'reject', value })}
        onCancel={() => setAction(null)}
      />
      {action === 'approve' && <ApproveClaimDialog claim={c} onClose={() => setAction(null)} />}
      {action === 'settle' && <SettleClaimDialog claim={c} onClose={() => setAction(null)} />}
    </Page>
  );
}

/** Claim coding (MB-03): ICD-10 diagnoses and procedures as text, the documents sent and the insurer's claim number. */
function CodingSection({ claim }: { claim: ClaimDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const closed = claim.status === 'Settled' || claim.status === 'Rejected';
  const [diagnosis, setDiagnosis] = useState(claim.diagnosisCodes ?? '');
  const [procedures, setProcedures] = useState(claim.procedureCodes ?? '');
  const [reference, setReference] = useState(claim.payerClaimReference ?? '');
  const [documents, setDocuments] = useState<string[]>(claim.documentFileIds);
  const changed =
    diagnosis !== (claim.diagnosisCodes ?? '') ||
    procedures !== (claim.procedureCodes ?? '') ||
    reference !== (claim.payerClaimReference ?? '') ||
    documents.join(',') !== claim.documentFileIds.join(',');

  const save = useMutation({
    mutationFn: () =>
      insuranceApi.updateClaimCoding(claim.id, {
        diagnosisCodes: diagnosis.trim() || null,
        procedureCodes: procedures.trim() || null,
        documentFileIds: documents,
        payerClaimReference: reference.trim() || null,
        version: claim.version,
      }),
    onSuccess: () => {
      notify.success('Coding saved');
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.claims });
    },
  });

  return (
    <Section
      title="Coding and documents"
      actions={
        !closed && (
          <Button
            appearance="primary"
            icon={save.isPending ? <Spinner size="tiny" /> : <SaveRegular />}
            disabled={!changed || save.isPending}
            onClick={() => save.mutate()}
          >
            Save coding
          </Button>
        )
      }
    >
      <div className={styles.form}>
        <div className={styles.grid}>
          <Field label="Diagnosis (ICD-10)">
            <Textarea
              value={diagnosis}
              onChange={(_, d) => setDiagnosis(d.value)}
              maxLength={1000}
              rows={3}
              disabled={closed}
            />
          </Field>
          <Field label="Procedures (ICD-10-PCS)">
            <Textarea
              value={procedures}
              onChange={(_, d) => setProcedures(d.value)}
              maxLength={1000}
              rows={3}
              disabled={closed}
            />
          </Field>
        </div>
        <Field label="Insurer / TPA claim number" style={{ maxWidth: 360 }}>
          <Input value={reference} onChange={(_, d) => setReference(d.value)} maxLength={64} disabled={closed} />
        </Field>
        <Field label="Documents">
          <Attachments fileIds={documents} patientId={claim.patientId} onChange={closed ? undefined : setDocuments} />
        </Field>
        <ActionError
          error={save.error}
          onReload={() => void queryClient.invalidateQueries({ queryKey: insuranceKeys.claims })}
        />
      </div>
    </Section>
  );
}
