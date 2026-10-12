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
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  SearchBox,
  Spinner,
  Tab,
  TabList,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  ArrowSyncRegular,
  ArrowUndoRegular,
  CheckmarkCircleRegular,
  CheckmarkRegular,
  DeleteRegular,
  LockClosedRegular,
  MoneyRegular,
  PaymentRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, todayIso, useDebouncedValue } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  ErrorState,
  KpiCard,
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
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries, type PayrollProcessResult, type PayrollRunDto } from '@/modules/hr/api';
import { HR_PERMISSIONS as P, PAYROLL_VIEWERS } from '@/modules/hr/labels';
import { runActions, runStepIndex, runStepLabel, RUN_STEPS, type RunAction } from '@/modules/hr/payroll';
import { PayrollInputsPanel } from '@/modules/hr/PayrollInputs';
import { PayslipDialog } from '@/modules/hr/PayslipView';
import { monthLabel } from '@/modules/hr/shared/dates';
import { saveBlob } from '@/modules/hr/shared/files';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { FilterPad, KpiGrid, Toolbar } from '@/modules/hr/shared/layout';

type TabId = 'payslips' | 'inputs';

export const Route = createFileRoute('/_app/hr/payroll/runs/$runId')({
  validateSearch: (s: Record<string, unknown>): { tab?: TabId; errorsOnly?: boolean } => ({
    tab: s.tab === 'inputs' ? 'inputs' : undefined,
    errorsOnly: s.errorsOnly === true ? true : undefined,
  }),
  component: () => (
    <RequirePermission permission={PAYROLL_VIEWERS} what="payroll">
      <RunPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  steps: { display: 'flex', gap: '4px', flexWrap: 'wrap', alignItems: 'center' },
  step: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 10px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  done: { backgroundColor: tokens.colorPaletteGreenBackground1, color: tokens.colorPaletteGreenForeground1 },
  current: {
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundOnBrand,
    fontWeight: tokens.fontWeightSemibold,
  },
  arrow: { color: tokens.colorNeutralForeground4 },
  muted: { color: tokens.colorNeutralForeground3 },
});

type Pending = 'approve' | 'lock' | 'delete' | 'bankFile' | 'reopen' | 'markPaid' | null;

function RunPage() {
  const { runId } = Route.useParams();
  const run = useQuery(hrQueries.payrollRun(runId));
  if (run.isLoading) {
    return <LoadingState />;
  }
  if (run.error || !run.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={run.error} onRetry={() => void run.refetch()} />
      </Page>
    );
  }
  return <RunView run={run.data} />;
}

function RunView({ run }: { run: PayrollRunDto }) {
  const styles = useStyles();
  const navigate = useNavigate({ from: Route.fullPath });
  const search = Route.useSearch();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canProcess = usePermission(P.payrollProcess);
  const canApprove = usePermission(P.payrollApprove);
  const actions = runActions(run.status, { process: canProcess, approve: canApprove });
  const has = (action: RunAction) => actions.includes(action);
  const [pending, setPending] = useState<Pending>(null);
  const [result, setResult] = useState<PayrollProcessResult | null>(null);
  const [busy, setBusy] = useState(false);
  const title = `Payroll · ${monthLabel(run.year, run.month)}`;
  const refresh = () => void queryClient.invalidateQueries({ queryKey: hrKeys.payroll });

  const act = async (
    label: string,
    work: () => Promise<unknown>,
    success: string,
    detail?: string,
  ): Promise<boolean> => {
    setBusy(true);
    try {
      await work();
      notify.success(success, detail);
      setPending(null);
      refresh();
      return true;
    } catch (error) {
      notify.error(error, `Could not ${label}`);
      refresh();
      return false;
    } finally {
      setBusy(false);
    }
  };

  const process = useMutation({
    mutationFn: () => hrApi.processRun(run.id),
    onSuccess: (outcome) => {
      setResult(outcome);
      notify.success(
        'Payroll processed',
        `${outcome.payslips} payslip(s)${outcome.payslipsWithErrors ? `, ${outcome.payslipsWithErrors} with errors` : ''}.`,
      );
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not process the payroll'),
  });

  const downloadBankFile = async () => {
    setBusy(true);
    try {
      const blob = await hrApi.bankTransferFile(run.id);
      saveBlob(blob, `bank-transfer-${run.year}-${String(run.month).padStart(2, '0')}.csv`);
      notify.success('Bank file downloaded', 'Upload it to the bank portal, then delete it from this computer.');
      setPending(null);
    } catch (error) {
      notify.error(error, 'Could not create the bank file');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Page>
      <PageHeader
        title={title}
        subtitle={
          run.notes ?? 'Maker-checker: the person who approves must be different from the person who processed.'
        }
        icon={<MoneyRegular />}
        breadcrumbs={[
          { label: 'Payroll', onClick: () => void navigate({ to: '/hr/payroll', search: { year: run.year } }) },
          { label: monthLabel(run.year, run.month) },
        ]}
        actions={
          <>
            {has('delete') && (
              <Button appearance="subtle" icon={<DeleteRegular />} onClick={() => setPending('delete')}>
                Delete
              </Button>
            )}
            {has('reopen') && (
              <Button icon={<ArrowUndoRegular />} onClick={() => setPending('reopen')}>
                Reopen
              </Button>
            )}
            {has('bankFile') && (
              <Button icon={<ArrowDownloadRegular />} onClick={() => setPending('bankFile')}>
                Bank transfer file
              </Button>
            )}
            {has('process') && (
              <Button
                appearance={run.status === 'Draft' ? 'primary' : 'secondary'}
                icon={process.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowSyncRegular />}
                disabled={process.isPending}
                onClick={() => process.mutate()}
              >
                {run.status === 'Draft' ? 'Process payroll' : 'Process again'}
              </Button>
            )}
            {has('approve') && (
              <Tooltip
                content={
                  run.payslipsWithErrors > 0
                    ? 'Fix the payslips with errors first'
                    : 'Approve after checking the payslips'
                }
                relationship="description"
              >
                <Button
                  appearance="primary"
                  icon={<CheckmarkRegular />}
                  disabled={run.payslipsWithErrors > 0}
                  onClick={() => setPending('approve')}
                >
                  Approve
                </Button>
              </Tooltip>
            )}
            {has('markPaid') && (
              <Button appearance="primary" icon={<PaymentRegular />} onClick={() => setPending('markPaid')}>
                Mark as paid
              </Button>
            )}
            {has('lock') && (
              <Button appearance="primary" icon={<LockClosedRegular />} onClick={() => setPending('lock')}>
                Lock payroll
              </Button>
            )}
          </>
        }
      />

      <div className={styles.steps} aria-label={`Payroll stage: ${runStepLabel[run.status]}`}>
        {RUN_STEPS.map((step, index) => {
          const current = runStepIndex(run.status);
          return (
            <span key={step} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
              <span
                className={mergeClasses(
                  styles.step,
                  index < current && styles.done,
                  index === current && styles.current,
                )}
              >
                {index < current && <CheckmarkCircleRegular aria-hidden />}
                {runStepLabel[step]}
              </span>
              {index < RUN_STEPS.length - 1 && <span className={styles.arrow}>›</span>}
            </span>
          );
        })}
      </div>

      {result && (
        <MessageBar intent={result.payslipsWithErrors > 0 ? 'warning' : 'success'} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              Processed {result.payslips} payslip(s){result.skipped ? `, skipped ${result.skipped}` : ''}
              {result.payslipsWithErrors ? `, ${result.payslipsWithErrors} with errors` : ''}
            </MessageBarTitle>
            {result.messages.length > 0 && (
              <ul style={{ margin: '4px 0 0', paddingLeft: 18, maxHeight: 180, overflowY: 'auto' }}>
                {result.messages.map((message) => (
                  <li key={message}>{message}</li>
                ))}
              </ul>
            )}
          </MessageBarBody>
        </MessageBar>
      )}
      {run.payslipsWithErrors > 0 && !result && (
        <MessageBar intent="error">
          <MessageBarBody>
            <MessageBarTitle>{run.payslipsWithErrors} payslip(s) have errors</MessageBarTitle>
            Correct the inputs or salary structures and process again. Approval is blocked until then.
          </MessageBarBody>
        </MessageBar>
      )}

      <KpiGrid>
        <KpiCard label="Employees" value={run.employeeCount} />
        <KpiCard label="Gross pay" value={formatInr(run.totalGross, { whole: true })} />
        <KpiCard label="Deductions" value={formatInr(run.totalDeductions, { whole: true })} />
        <KpiCard label="Net pay" value={formatInr(run.totalNet, { whole: true })} />
        <KpiCard
          label="Progress"
          value={<StatusBadge status={run.status} />}
          hint={[
            run.processedAt ? `Processed ${formatDateTime(run.processedAt)}` : '',
            run.approvedAt ? `approved ${formatDateTime(run.approvedAt)}` : '',
            run.paidOn
              ? `paid ${formatDateOnly(run.paidOn)}${run.paymentReference ? ` (${run.paymentReference})` : ''}`
              : '',
          ]
            .filter(Boolean)
            .join(' · ')}
        />
      </KpiGrid>

      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={search.tab ?? 'payslips'}
            onTabSelect={(_, d) =>
              void navigate({
                search: (prev) => ({ ...prev, tab: d.value === 'inputs' ? 'inputs' : undefined }),
                replace: true,
              })
            }
          >
            <Tab value="payslips">Payslips</Tab>
            <Tab value="inputs">Inputs</Tab>
          </TabList>
        </div>
        <div style={{ paddingTop: 12 }}>
          {search.tab === 'inputs' ? (
            <PayrollInputsPanelWrap run={run} editable={has('editInputs')} />
          ) : (
            <Payslips run={run} />
          )}
        </div>
      </Section>

      <ConfirmDialog
        open={pending === 'approve'}
        title={`Approve payroll for ${monthLabel(run.year, run.month)}?`}
        confirmLabel="Approve payroll"
        busy={busy}
        onConfirm={() =>
          void act(
            'approve the payroll',
            () => hrApi.approveRun(run.id),
            'Payroll approved',
            'Payslips are now visible to employees.',
          )
        }
        onCancel={() => setPending(null)}
      >
        Net pay of {formatInr(run.totalNet)} for {run.employeeCount} employee(s). Employees can see their payslips once
        approved. You must not be the person who processed this payroll.
      </ConfirmDialog>
      <ConfirmDialog
        open={pending === 'lock'}
        title="Lock this payroll?"
        confirmLabel="Lock payroll"
        destructive
        busy={busy}
        onConfirm={() =>
          void act(
            'lock the payroll',
            () => hrApi.lockRun(run.id),
            'Payroll locked',
            "The month's attendance is locked too.",
          )
        }
        onCancel={() => setPending(null)}
      >
        Locking closes the month for good: the payroll and the month's attendance can no longer be changed.
      </ConfirmDialog>
      <ConfirmDialog
        open={pending === 'delete'}
        title="Delete this payroll?"
        confirmLabel="Delete"
        destructive
        busy={busy}
        onConfirm={() =>
          void act('delete the payroll', () => hrApi.deleteRun(run.id), 'Payroll deleted').then((ok) => {
            if (ok) {
              void navigate({ to: '/hr/payroll', search: { year: run.year } });
            }
          })
        }
        onCancel={() => setPending(null)}
      >
        The payslips and manual inputs of {monthLabel(run.year, run.month)} are removed. Deductions posted by other
        modules are kept and come back when the payroll is created again.
      </ConfirmDialog>
      <ConfirmDialog
        open={pending === 'bankFile'}
        title="Download the bank transfer file?"
        confirmLabel="Download"
        busy={busy}
        onConfirm={() => void downloadBankFile()}
        onCancel={() => setPending(null)}
      >
        The file contains full bank account numbers. The download is recorded in the audit trail under your name. Keep
        it on this computer only until it is uploaded to the bank, then delete it.
      </ConfirmDialog>
      <ReasonDialog
        open={pending === 'reopen'}
        title="Reopen this payroll?"
        description="It goes back to Processed so that corrections can be made. Employees stop seeing these payslips until it is approved again."
        label="Reason"
        confirmLabel="Reopen"
        busy={busy}
        onConfirm={(reason) =>
          void act('reopen the payroll', () => hrApi.reopenRun(run.id, reason), 'Payroll reopened')
        }
        onCancel={() => setPending(null)}
      />
      {pending === 'markPaid' && (
        <MarkPaidDialog
          open
          busy={busy}
          onCancel={() => setPending(null)}
          onConfirm={(paidOn, reference) =>
            void act(
              'mark the payroll paid',
              () => hrApi.markRunPaid(run.id, paidOn, reference),
              'Payroll marked as paid',
              'Employees are notified that the salary is credited.',
            )
          }
        />
      )}
    </Page>
  );
}

function PayrollInputsPanelWrap({ run, editable }: { run: PayrollRunDto; editable: boolean }) {
  return (
    <div style={{ padding: '0 16px 16px' }}>
      {!editable && run.status !== 'Draft' && run.status !== 'Processed' && (
        <MessageBar intent="info" style={{ marginBottom: 12 }}>
          <MessageBarBody>Inputs cannot be changed after approval. Reopen the payroll to correct them.</MessageBarBody>
        </MessageBar>
      )}
      <PayrollInputsPanel runId={run.id} editable={editable} />
    </div>
  );
}

function Payslips({ run }: { run: PayrollRunDto }) {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<string | null>(null);
  const payslips = useQuery(
    hrQueries.payslips(run.id, { search: debounced || undefined, errorsOnly: search.errorsOnly, page }),
  );

  return (
    <>
      <FilterPad>
        <Toolbar spaced={false}>
          <Field label="Search" style={{ flex: '1 1 260px' }}>
            <SearchBox
              value={term}
              onChange={(_, d) => {
                setTerm(d.value);
                setPage(1);
              }}
              placeholder="Employee name or code"
            />
          </Field>
          <Checkbox
            style={{ alignSelf: 'flex-end' }}
            checked={Boolean(search.errorsOnly)}
            label={`Only payslips with errors (${run.payslipsWithErrors})`}
            onChange={(_, d) => {
              setPage(1);
              void navigate({
                search: (prev) => ({ ...prev, errorsOnly: d.checked ? true : undefined }),
                replace: true,
              });
            }}
          />
        </Toolbar>
      </FilterPad>
      <DataTable
        ariaLabel="Payslips"
        rows={payslips.data?.items}
        loading={payslips.isLoading}
        error={payslips.error}
        onRetry={() => void payslips.refetch()}
        getRowId={(p) => p.id}
        onRowClick={(p) => setOpen(p.id)}
        columns={[
          {
            key: 'employee',
            header: 'Employee',
            render: (p) => (
              <span>
                <Text weight="semibold">{p.employeeName}</Text> <Caption1>{p.employeeCode}</Caption1>
                {p.departmentName && <Caption1 block>{p.departmentName}</Caption1>}
              </span>
            ),
          },
          { key: 'days', header: 'Paid days', align: 'end', width: 90, render: (p) => p.payableDays },
          { key: 'lop', header: 'Loss of pay', align: 'end', width: 100, render: (p) => (p.lopDays ? p.lopDays : '—') },
          { key: 'gross', header: 'Gross', align: 'end', priority: 'low', render: (p) => formatInr(p.grossEarnings) },
          {
            key: 'deductions',
            header: 'Deductions',
            align: 'end',
            priority: 'low',
            render: (p) => formatInr(p.totalDeductions),
          },
          {
            key: 'net',
            header: 'Net pay',
            align: 'end',
            render: (p) => <Text weight="semibold">{formatInr(p.netPay)}</Text>,
          },
          {
            key: 'errors',
            header: '',
            width: 110,
            render: (p) => (p.hasErrors ? <StatusBadge status="Failed" label="Has errors" /> : null),
          },
        ]}
        emptyTitle={
          run.status === 'Draft'
            ? 'Not processed yet'
            : search.errorsOnly
              ? 'No payslips with errors'
              : 'No payslips match'
        }
        emptyDescription={run.status === 'Draft' ? 'Add the inputs, then press "Process payroll".' : undefined}
        page={page}
        pageSize={50}
        totalCount={payslips.data?.totalCount}
        onPageChange={setPage}
        size="small"
      />
      <PayslipDialog payslipId={open} onClose={() => setOpen(null)} />
    </>
  );
}

function MarkPaidDialog({
  open,
  busy,
  onCancel,
  onConfirm,
}: {
  open: boolean;
  busy: boolean;
  onCancel: () => void;
  onConfirm: (paidOn: string, reference: string) => void;
}) {
  const [paidOn, setPaidOn] = useState(todayIso());
  const [reference, setReference] = useState('');
  const valid = Boolean(paidOn) && reference.trim().length > 0;
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ paidOn, reference }, open),
    busy,
    open,
    onClose: onCancel,
  });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid) {
              onConfirm(paidOn, reference.trim());
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Mark salary as paid</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Field label="Paid on" required>
                <Input type="date" value={paidOn} max={todayIso()} onChange={(_, d) => setPaidOn(d.value)} />
              </Field>
              <Field label="Bank reference" required hint="Batch or UTR number from the bank's confirmation.">
                <Input value={reference} onChange={(_, d) => setReference(d.value)} maxLength={100} autoFocus />
              </Field>
              <Caption1>Every employee receives a "salary credited" notification.</Caption1>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={busy}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || busy}
                icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Mark as paid
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
