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
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  BedRegular,
  ChatHelpRegular,
  CheckmarkRegular,
  DismissRegular,
  EditRegular,
  HeartPulseRegular,
  SendRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatInr, isApiError } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  EmptyState,
  ErrorState,
  LoadingState,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import { BillingPatientBanner } from '@/modules/billing/BillingPatientBanner';
import type { IpAccountDetail } from '@/modules/billing/ipApi';
import { amountInputText, parseRupees } from '@/modules/billing/money';
import { ActionError, Amount, AmountField, Detail, DetailGrid, NoAccess, ReasonDialog } from '@/modules/billing/ui';
import {
  insuranceApi,
  insuranceKeys,
  insuranceQueries,
  type EnhancementDto,
  type PreAuthDetail,
} from '@/modules/insurance/api';
import { Attachments } from '@/modules/insurance/Attachments';
import { ENHANCEMENT_STATUS_LABEL, PREAUTH_STATUS_LABEL, preAuthTone } from '@/modules/insurance/labels';
import { PolicyNumberReveal } from '@/modules/insurance/PolicyNumberReveal';
import { PreAuthDialog } from '@/modules/insurance/PreAuthDialog';

export const Route = createFileRoute('/_app/insurance/preauths/$preAuthId')({
  component: PreAuthPage,
});

const useStyles = makeStyles({
  thread: { display: 'flex', flexDirection: 'column', gap: '10px' },
  bubble: { padding: '10px 12px', borderRadius: tokens.borderRadiusLarge, maxWidth: '720px', whiteSpace: 'pre-wrap' },
  insurer: { backgroundColor: tokens.colorPaletteMarigoldBackground1, alignSelf: 'flex-start' },
  hospital: { backgroundColor: tokens.colorBrandBackground2, alignSelf: 'flex-end' },
  muted: { color: tokens.colorNeutralForeground3 },
  pre: { whiteSpace: 'pre-wrap' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

type Action =
  | 'edit'
  | 'submit'
  | 'query'
  | 'answer'
  | 'approve'
  | 'reject'
  | 'enhance'
  | 'final'
  | { respond: EnhancementDto }
  | null;

/** One pre-authorisation: the request, the exchange with the insurer, approvals, enhancements and the final approval. */
function PreAuthPage() {
  const styles = useStyles();
  const { preAuthId } = Route.useParams();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRead = usePermission(['insurance.preauth.manage', 'insurance.claims.manage', 'billing.ip.manage']);
  const canManage = usePermission('insurance.preauth.manage');
  const [action, setAction] = useState<Action>(null);
  const detail = useQuery({ ...insuranceQueries.preAuth(preAuthId), enabled: canRead });

  const done = (message: string) => {
    notify.success(message);
    setAction(null);
    void queryClient.invalidateQueries({ queryKey: insuranceKeys.preAuths });
    void queryClient.invalidateQueries({ queryKey: ['billing', 'ip'] });
  };

  const run = useMutation({
    mutationFn: async (step: { kind: 'submit' } | { kind: 'query' | 'answer' | 'reject'; text: string }) => {
      if (step.kind === 'submit') {
        return insuranceApi.submitPreAuth(preAuthId);
      }
      if (step.kind === 'query') {
        return insuranceApi.preAuthQuery(preAuthId, step.text);
      }
      if (step.kind === 'answer') {
        return insuranceApi.preAuthAnswer(preAuthId, step.text);
      }
      return insuranceApi.rejectPreAuth(preAuthId, step.text);
    },
    onSuccess: (_, step) =>
      done(
        step.kind === 'submit'
          ? 'Submitted to the insurer'
          : step.kind === 'query'
            ? 'Insurer’s question recorded'
            : step.kind === 'answer'
              ? 'Answer recorded'
              : 'Rejection recorded',
      ),
  });

  const openAccount = async (admissionId: string) => {
    try {
      const account = await api.get<IpAccountDetail>(`/api/v1/billing/ip-accounts/by-admission/${admissionId}`);
      void navigate({ to: '/billing/ip/$accountId', params: { accountId: account.id } });
    } catch (error) {
      notify.error(error, 'Could not open the IP account');
    }
  };

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Pre-authorisation" />
        <NoAccess what="pre-authorisations" />
      </Page>
    );
  }
  if (detail.isLoading) {
    return <LoadingState />;
  }
  if (detail.error || !detail.data) {
    return (
      <Page maxWidth={900}>
        <PageHeader
          title="Pre-authorisation"
          breadcrumbs={[
            { label: 'Pre-authorisations', onClick: () => void navigate({ to: '/insurance/preauths' }) },
            { label: 'Request' },
          ]}
        />
        {isApiError(detail.error) && detail.error.status === 404 ? (
          <EmptyState title="This pre-authorisation was not found" />
        ) : (
          <ErrorState error={detail.error} onRetry={() => void detail.refetch()} />
        )}
      </Page>
    );
  }

  const p = detail.data;
  const approved = p.status === 'Approved' || p.status === 'PartiallyApproved';
  const pendingEnhancement = p.enhancements.some((e) => e.status === 'Pending');
  const title = p.number ? `Pre-authorisation ${p.number}` : 'Draft pre-authorisation';

  return (
    <Page>
      <PageHeader
        title={title}
        icon={<HeartPulseRegular />}
        subtitle={
          <>
            <StatusBadge status={p.status} tone={preAuthTone(p.status)} label={PREAUTH_STATUS_LABEL[p.status]} />{' '}
            {p.payerName}
            {p.tpaName ? ` via ${p.tpaName}` : ''}
          </>
        }
        breadcrumbs={[
          { label: 'Pre-authorisations', onClick: () => void navigate({ to: '/insurance/preauths' }) },
          { label: p.number || 'Draft' },
        ]}
        actions={
          canManage && (
            <>
              {(p.status === 'Draft' || p.status === 'Query') && (
                <Button icon={<EditRegular />} onClick={() => setAction('edit')}>
                  Edit
                </Button>
              )}
              {p.status === 'Draft' && (
                <Button appearance="primary" icon={<SendRegular />} onClick={() => setAction('submit')}>
                  Submit to insurer
                </Button>
              )}
              {p.status === 'Submitted' && (
                <Button icon={<ChatHelpRegular />} onClick={() => setAction('query')}>
                  Insurer asked a question
                </Button>
              )}
              {p.status === 'Query' && (
                <Button appearance="primary" icon={<SendRegular />} onClick={() => setAction('answer')}>
                  Answer the question
                </Button>
              )}
              {(p.status === 'Submitted' || p.status === 'Query') && (
                <>
                  <Button icon={<DismissRegular />} onClick={() => setAction('reject')}>
                    Rejected
                  </Button>
                  <Button
                    appearance={p.status === 'Submitted' ? 'primary' : 'secondary'}
                    icon={<CheckmarkRegular />}
                    onClick={() => setAction('approve')}
                  >
                    Approved
                  </Button>
                </>
              )}
              {approved && p.finalApprovedAmount === null && !pendingEnhancement && (
                <Button icon={<AddRegular />} onClick={() => setAction('enhance')}>
                  Ask for more (enhancement)
                </Button>
              )}
              {approved && (
                <Button appearance="primary" icon={<CheckmarkRegular />} onClick={() => setAction('final')}>
                  {p.finalApprovedAmount === null ? 'Final approval' : 'Change final approval'}
                </Button>
              )}
            </>
          )
        }
      />
      <BillingPatientBanner patientId={p.patientId} showDues={false} />
      {p.status === 'Rejected' && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Rejected {formatDateTime(p.rejectedAt)}</MessageBarTitle>
            {p.rejectionReason}
          </MessageBarBody>
        </MessageBar>
      )}
      <Section title="Amounts">
        <DetailGrid>
          <Detail label="Requested">
            <Amount value={p.requestedAmount} size="large" />
          </Detail>
          <Detail label="Initially approved">{p.approvedAmount > 0 ? <Amount value={p.approvedAmount} /> : '—'}</Detail>
          <Detail label="Final approval">
            {p.finalApprovedAmount !== null ? <Amount value={p.finalApprovedAmount} /> : '—'}
          </Detail>
          <Detail label="Approved in effect">
            <Amount
              value={p.effectiveApprovedAmount}
              size="large"
              tone={p.effectiveApprovedAmount > 0 ? 'positive' : 'muted'}
            />
          </Detail>
          <Detail label="Approval reference">{p.approvalReference}</Detail>
          <Detail label="Policy">
            <PolicyNumberReveal policyId={p.policyId} masked={p.policyNumberMasked ?? '—'} />
          </Detail>
          <Detail label="Submitted">{p.submittedAt ? formatDateTime(p.submittedAt) : 'Not yet'}</Detail>
          <Detail label="Admission">
            {p.admissionId ? (
              <Button size="small" icon={<BedRegular />} onClick={() => void openAccount(p.admissionId!)}>
                Open IP account
              </Button>
            ) : (
              'Planned'
            )}
          </Detail>
        </DetailGrid>
      </Section>

      {(p.queryText || p.queryResponse) && (
        <Section title="Exchange with the insurer" description="The latest question and answer">
          <div className={styles.thread}>
            {p.queryText && (
              <div className={mergeClasses(styles.bubble, styles.insurer)}>
                <Caption1 block className={styles.muted}>
                  Insurer asked
                </Caption1>
                {p.queryText}
              </div>
            )}
            {p.queryResponse && (
              <div className={mergeClasses(styles.bubble, styles.hospital)}>
                <Caption1 block className={styles.muted}>
                  Hospital answered
                </Caption1>
                {p.queryResponse}
              </div>
            )}
          </div>
        </Section>
      )}

      <Section title="Clinical details">
        <DetailGrid minColumnWidth={260}>
          <Detail label="Diagnosis (ICD-10)">
            {p.diagnosisCodes && <span className={styles.pre}>{p.diagnosisCodes}</span>}
          </Detail>
          <Detail label="Procedures">
            {p.procedureCodes && <span className={styles.pre}>{p.procedureCodes}</span>}
          </Detail>
          <Detail label="Expected stay">{p.expectedStayDays ? `${p.expectedStayDays} days` : null}</Detail>
        </DetailGrid>
        {p.clinicalSummary && (
          <Text block className={styles.pre} style={{ marginTop: 12 }}>
            {p.clinicalSummary}
          </Text>
        )}
        <div style={{ marginTop: 12 }}>
          <Attachments fileIds={p.documentFileIds} patientId={p.patientId} />
        </div>
      </Section>

      {(approved || p.enhancements.length > 0) && (
        <Section title="Enhancements" description="Extra approval asked when the stay costs more than estimated" flush>
          <DataTable
            ariaLabel="Enhancements"
            rows={p.enhancements}
            getRowId={(e) => e.id}
            size="small"
            columns={[
              { key: 'asked', header: 'Asked', render: (e) => formatDateTime(e.requestedAt) },
              {
                key: 'amount',
                header: 'Amount asked',
                align: 'end',
                render: (e) => <Amount value={e.requestedAmount} />,
              },
              { key: 'reason', header: 'Reason', render: (e) => e.reason },
              {
                key: 'approved',
                header: 'Approved',
                align: 'end',
                render: (e) => (e.status === 'Approved' ? <Amount value={e.approvedAmount} strong /> : '—'),
              },
              {
                key: 'status',
                header: 'Status',
                render: (e) => <StatusBadge status={e.status} label={ENHANCEMENT_STATUS_LABEL[e.status]} />,
              },
            ]}
            rowActions={
              canManage
                ? (e) =>
                    e.status === 'Pending' ? (
                      <Button size="small" onClick={() => setAction({ respond: e })}>
                        Record reply
                      </Button>
                    ) : null
                : undefined
            }
            emptyTitle="No enhancement asked"
          />
        </Section>
      )}

      {action === 'edit' && (
        <PreAuthDialog preAuth={p} onClose={() => setAction(null)} onSaved={() => done('Pre-authorisation saved')} />
      )}
      <ConfirmDialog
        open={action === 'submit'}
        title="Submit this request to the insurer?"
        confirmLabel="Submit"
        busy={run.isPending}
        onConfirm={() => run.mutate({ kind: 'submit' })}
        onCancel={() => {
          setAction(null);
          run.reset();
        }}
      >
        <Text block>
          A number is allocated and the request can no longer be edited unless the insurer asks a question. Estimate:{' '}
          <Amount value={p.requestedAmount} strong />.
        </Text>
        <ActionError error={run.error} />
      </ConfirmDialog>
      <ReasonDialog
        open={action === 'query' || action === 'answer'}
        title={action === 'query' ? 'What did the insurer ask?' : 'Answer to the insurer'}
        description={action === 'answer' && p.queryText ? `They asked: “${p.queryText}”` : undefined}
        label={action === 'query' ? 'Question' : 'Answer'}
        minLength={2}
        maxLength={2000}
        confirmLabel={action === 'query' ? 'Record question' : 'Record answer'}
        busy={run.isPending}
        error={run.error}
        onConfirm={(text) => run.mutate({ kind: action === 'query' ? 'query' : 'answer', text })}
        onCancel={() => setAction(null)}
      />
      <ReasonDialog
        open={action === 'reject'}
        title="Insurer rejected the request?"
        description="Record the reason given by the insurer. The patient then pays as self-pay unless a new request is approved."
        label="Reason given"
        maxLength={1000}
        confirmLabel="Record rejection"
        destructive
        busy={run.isPending}
        error={run.error}
        onConfirm={(text) => run.mutate({ kind: 'reject', text })}
        onCancel={() => setAction(null)}
      />
      {action === 'approve' && (
        <AmountReferenceDialog
          preAuth={p}
          mode="approve"
          onClose={() => setAction(null)}
          onDone={() => done('Approval recorded')}
        />
      )}
      {action === 'final' && (
        <AmountReferenceDialog
          preAuth={p}
          mode="final"
          onClose={() => setAction(null)}
          onDone={() => done('Final approval recorded')}
        />
      )}
      {action === 'enhance' && (
        <EnhancementDialog preAuth={p} onClose={() => setAction(null)} onDone={() => done('Enhancement asked')} />
      )}
      {action !== null && typeof action === 'object' && (
        <RespondEnhancementDialog
          preAuth={p}
          enhancement={action.respond}
          onClose={() => setAction(null)}
          onDone={() => done('Insurer’s reply recorded')}
        />
      )}
    </Page>
  );
}

/** Initial approval (approved amount + reference) or the final approval against the final bill. */
function AmountReferenceDialog({
  preAuth,
  mode,
  onClose,
  onDone,
}: {
  preAuth: PreAuthDetail;
  mode: 'approve' | 'final';
  onClose: () => void;
  onDone: () => void;
}) {
  const styles = useStyles();
  const initial =
    mode === 'approve' ? preAuth.requestedAmount : (preAuth.finalApprovedAmount ?? preAuth.effectiveApprovedAmount);
  const [amount, setAmount] = useState(amountInputText(initial));
  const [reference, setReference] = useState(mode === 'final' ? (preAuth.approvalReference ?? '') : '');
  const parsed = parseRupees(amount, { allowZero: mode === 'final' });
  const save = useMutation({
    mutationFn: () =>
      mode === 'approve'
        ? insuranceApi.approvePreAuth(preAuth.id, parsed.ok ? parsed.rupees : 0, reference.trim() || null)
        : insuranceApi.finalApproval(preAuth.id, parsed.ok ? parsed.rupees : 0, reference.trim() || null),
    onSuccess: onDone,
  });
  const guard = useUnsavedChanges({ dirty: useValuesChanged({ amount, reference }), busy: save.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (parsed.ok && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>{mode === 'approve' ? 'Insurer approved' : 'Final approval'}</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                {mode === 'approve'
                  ? `Requested ${formatInr(preAuth.requestedAmount)}. An approval below the request is recorded as partly approved.`
                  : 'The amount the insurer approved against the final bill. It replaces the running approvals and caps the insurer share.'}
              </Text>
              <AmountField
                label="Approved amount"
                required
                value={amount}
                onChange={setAmount}
                error={amount && !parsed.ok ? parsed.error : null}
                autoFocus
              />
              <Field label="Approval reference" hint="From the insurer's letter or portal">
                <Input value={reference} onChange={(_, d) => setReference(d.value)} maxLength={64} />
              </Field>
              <ActionError error={save.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!parsed.ok || save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

function EnhancementDialog({
  preAuth,
  onClose,
  onDone,
}: {
  preAuth: PreAuthDetail;
  onClose: () => void;
  onDone: () => void;
}) {
  const styles = useStyles();
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const parsed = parseRupees(amount);
  const valid = parsed.ok && reason.trim().length >= 5;
  const save = useMutation({
    mutationFn: () => insuranceApi.requestEnhancement(preAuth.id, parsed.ok ? parsed.rupees : 0, reason.trim()),
    onSuccess: onDone,
  });
  const guard = useUnsavedChanges({ dirty: useValuesChanged({ amount, reason }), busy: save.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Ask the insurer for more</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                Approved so far: <Amount value={preAuth.effectiveApprovedAmount} strong />. Enter the additional amount
                needed.
              </Text>
              <AmountField
                label="Additional amount"
                required
                value={amount}
                onChange={setAmount}
                error={amount && !parsed.ok ? parsed.error : null}
                autoFocus
              />
              <Field label="Reason" required hint="e.g. Stay extended by 3 days for post-operative infection">
                <Input value={reason} onChange={(_, d) => setReason(d.value)} maxLength={500} />
              </Field>
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
                Ask for enhancement
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

function RespondEnhancementDialog({
  preAuth,
  enhancement,
  onClose,
  onDone,
}: {
  preAuth: PreAuthDetail;
  enhancement: EnhancementDto;
  onClose: () => void;
  onDone: () => void;
}) {
  const styles = useStyles();
  const [decision, setDecision] = useState<'approved' | 'rejected'>('approved');
  const [amount, setAmount] = useState(amountInputText(enhancement.requestedAmount));
  const [reference, setReference] = useState('');
  const parsed = parseRupees(amount);
  const valid = decision === 'rejected' || parsed.ok;
  const save = useMutation({
    mutationFn: () =>
      insuranceApi.respondEnhancement(
        preAuth.id,
        enhancement.id,
        decision === 'approved',
        decision === 'approved' && parsed.ok ? parsed.rupees : 0,
        reference.trim() || null,
      ),
    onSuccess: onDone,
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ decision, amount, reference }),
    busy: save.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Insurer&apos;s reply to the enhancement</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                Asked for <Amount value={enhancement.requestedAmount} strong />: {enhancement.reason}
              </Text>
              <RadioGroup
                layout="horizontal"
                value={decision}
                onChange={(_, d) => setDecision(d.value as 'approved' | 'rejected')}
              >
                <Radio value="approved" label="Approved" />
                <Radio value="rejected" label="Rejected" />
              </RadioGroup>
              {decision === 'approved' && (
                <AmountField
                  label="Approved amount"
                  required
                  value={amount}
                  onChange={setAmount}
                  error={amount && !parsed.ok ? parsed.error : null}
                  autoFocus
                />
              )}
              <Field label="Reference">
                <Input value={reference} onChange={(_, d) => setReference(d.value)} maxLength={64} />
              </Field>
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
                Record reply
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
