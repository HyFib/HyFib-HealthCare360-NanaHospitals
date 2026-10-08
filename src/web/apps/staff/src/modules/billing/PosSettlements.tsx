import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Divider,
  Field,
  Input,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  ArrowDownloadRegular,
  ArrowUploadRegular,
  CheckmarkCircleRegular,
  DocumentRegular,
  MoreHorizontalRegular,
  ReceiptRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, saveFile, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useRef, useState, type ChangeEvent } from 'react';
import {
  matchText,
  POS_LINE_TYPE_LABEL,
  POS_MATCH_LABEL,
  posSettlementApi,
  posSettlementKeys,
  posSettlementQueries,
  references,
  type PosPayoutDto,
  type PosSettlementImportResult,
  type PosSettlementLineDto,
  type PosSettlementMatch,
} from './posSettlementApi';
import { ActionError, Amount, ReasonDialog } from './ui';

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  stack: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  amounts: { display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: '2px' },
  toolbar: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '8px', padding: '8px 16px 12px' },
  dialogBody: { display: 'flex', flexDirection: 'column', gap: '12px' },
  file: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '8px' },
  list: { margin: 0, paddingLeft: '20px' },
});

/** Money not credited is red; a chargeback or another amount amber; the rest neutral. */
const MATCH_TONE: Record<PosSettlementMatch, 'danger' | 'warning' | 'informative' | 'success' | 'subtle'> = {
  NoReceipt: 'danger',
  AmountDiffers: 'warning',
  NoRefund: 'warning',
  Review: 'warning',
  Matched: 'success',
  OtherCounter: 'subtle',
};

/** The accepted report files: CSV (or text) and Excel workbooks. */
export const REPORT_FILE_TYPES =
  '.csv,.txt,.xlsx,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

function useRefreshSettlements() {
  const queryClient = useQueryClient();
  return () => void queryClient.invalidateQueries({ queryKey: posSettlementKeys.all });
}

/**
 * Imports an acquirer's settlement report: the file, the acquirer's name and, only for a report without a settlement
 * date column, the day the bank was paid. Afterwards it shows what was imported, which headings were read and which
 * rows were left out and why. A training report of the simulator's sales can be downloaded to practise with.
 */
export function ImportReportDialog({
  open,
  defaultAcquirer,
  onClose,
}: {
  open: boolean;
  defaultAcquirer?: string;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useRefreshSettlements();
  const input = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [acquirer, setAcquirer] = useState(defaultAcquirer ?? '');
  const [settledOn, setSettledOn] = useState('');
  const [trainingDate, setTrainingDate] = useState(todayIso());
  const [result, setResult] = useState<PosSettlementImportResult | null>(null);
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setFile(null);
      setAcquirer(defaultAcquirer ?? '');
      setSettledOn('');
      setResult(null);
    }
  }

  const upload = useMutation({
    mutationFn: () => posSettlementApi.importReport(file!, acquirer.trim(), settledOn || null),
    onSuccess: (imported) => {
      setResult(imported);
      refresh();
    },
  });
  const training = useMutation({
    mutationFn: (date: string) => posSettlementApi.trainingFile(date),
    onSuccess: (download) =>
      saveFile(download.blob, download.fileName ?? `training-card-settlement-${trainingDate}.csv`),
    onError: (error) => notify.error(error, 'Could not make the training report'),
  });

  const choose = (event: ChangeEvent<HTMLInputElement>) => {
    setFile(event.target.files?.[0] ?? null);
    setResult(null);
    upload.reset();
    event.target.value = '';
  };

  const busy = upload.isPending;
  const close = () => {
    if (!busy) {
      upload.reset();
      onClose();
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && close()}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>Import a settlement report</DialogTitle>
          <DialogContent className={styles.dialogBody}>
            <Text>
              Import the card and UPI settlement report your acquirer or bank sends (its MIS), as CSV or Excel (.xlsx),
              at most 10 MB. HC360 finds the columns by their headings and matches each transaction with its receipt.
              Reports may overlap: a transaction already imported is not added twice.
            </Text>
            <div className={styles.file}>
              <Button icon={<DocumentRegular />} onClick={() => input.current?.click()} disabled={busy}>
                {file ? 'Choose another file' : 'Choose file'}
              </Button>
              {file && <Text>{file.name}</Text>}
              <input
                ref={input}
                type="file"
                accept={REPORT_FILE_TYPES}
                hidden
                tabIndex={-1}
                aria-label="Settlement report"
                onChange={choose}
              />
            </div>
            <Field label="Acquirer" hint="The bank or POS company the report comes from, for example HDFC Bank.">
              <Input value={acquirer} maxLength={64} onChange={(_, data) => setAcquirer(data.value)} />
            </Field>
            <Field
              label="Day the bank was paid"
              hint="Only for a report without a settlement date column. Otherwise each row's own date is used."
            >
              <Input type="date" value={settledOn} max={todayIso()} onChange={(_, data) => setSettledOn(data.value)} />
            </Field>
            <ActionError error={upload.error} title="Could not import the report" />
            {busy && <Spinner size="small" labelPosition="after" label="Reading and matching the report…" />}
            {result && <ImportResultView result={result} />}
            <Divider />
            <Caption1 className={styles.muted}>
              No acquirer report yet? Download a training report of the training terminal&apos;s sales of a day, paid
              the next day with a card charge, and import it to practise.
            </Caption1>
            <div className={styles.file}>
              <Input
                type="date"
                aria-label="Training report day"
                value={trainingDate}
                max={todayIso()}
                onChange={(_, data) => setTrainingDate(data.value)}
              />
              <Button
                icon={training.isPending ? <Spinner size="tiny" /> : <ArrowDownloadRegular />}
                disabled={!trainingDate || training.isPending}
                onClick={() => training.mutate(trainingDate)}
              >
                Download training report
              </Button>
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={close} disabled={busy}>
              {result ? 'Close' : 'Cancel'}
            </Button>
            {!result && (
              <Button
                appearance="primary"
                icon={busy ? <Spinner size="tiny" /> : <ArrowUploadRegular />}
                disabled={!file || !acquirer.trim() || busy}
                disabledFocusable={busy}
                onClick={() => upload.mutate()}
              >
                Import
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** What an import did: the message, the rows left out and why, and the headings HC360 read. */
export function ImportResultView({ result }: { result: PosSettlementImportResult }) {
  const styles = useStyles();
  return (
    <MessageBar intent={result.toLookAt > 0 ? 'warning' : 'success'} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Imported {result.import.fileName}</MessageBarTitle>
        {result.message}
        {result.problems.length > 0 && (
          <ul className={styles.list} aria-label="Rows left out">
            {result.problems.map((problem) => (
              <li key={problem}>{problem}</li>
            ))}
          </ul>
        )}
        <Caption1 block className={styles.muted}>
          Columns read: {result.columns.join(', ')}
        </Caption1>
      </MessageBarBody>
    </MessageBar>
  );
}

/** A settled transaction: its type, mode and references, and the acquirer and payout it came in. */
function TransactionCell({ line }: { line: PosSettlementLineDto }) {
  const styles = useStyles();
  return (
    <span className={styles.stack}>
      <Text>
        {POS_LINE_TYPE_LABEL[line.type]} by {line.mode === 'Upi' ? 'UPI' : 'card'}
        {line.transactionAt
          ? `, ${formatDateTime(line.transactionAt)}`
          : line.transactionOn
            ? `, ${formatDateOnly(line.transactionOn)}`
            : ''}
      </Text>
      <Caption1 className={styles.muted}>{references(line) || 'No reference in the report'}</Caption1>
      <Caption1 className={styles.muted}>
        {line.acquirer} · paid to the bank {formatDateOnly(line.settledOn)}
        {line.utr ? ` · UTR ${line.utr}` : ''}
      </Caption1>
    </span>
  );
}

function LineAmount({ line }: { line: PosSettlementLineDto }) {
  const styles = useStyles();
  return (
    <span className={styles.amounts}>
      <Amount value={line.type === 'Sale' ? line.amount : -line.amount} strong />
      {line.recordedAmount !== null && line.recordedAmount !== line.amount && (
        <Caption1 className={styles.muted}>
          HC360 <Amount value={line.recordedAmount} />
        </Caption1>
      )}
    </span>
  );
}

/**
 * The settled transactions that need an accountant, worst first: money without a receipt, another amount, a refund
 * HC360 does not know, a chargeback. Each can be marked reviewed with a note (audited); "Match again" looks for receipts
 * recorded since.
 */
export function PosToLookAt({ page, onPage }: { page: number; onPage: (page: number) => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const refresh = useRefreshSettlements();
  const restoreFocusTarget = useRestoreFocusTarget();
  const [reviewing, setReviewing] = useState<PosSettlementLineDto | null>(null);
  const list = useQuery(posSettlementQueries.lines({ attention: true, page }));

  const match = useMutation({
    mutationFn: () => posSettlementApi.match(),
    onSuccess: (result) => {
      notify.info('Matched again', result.message);
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not match again'),
  });
  const review = useMutation({
    mutationFn: ({ line, note }: { line: PosSettlementLineDto; note: string }) =>
      posSettlementApi.review(line.id, note, line.version),
    onSuccess: () => {
      notify.success('Marked reviewed', 'Your note is kept with the transaction.');
      setReviewing(null);
      refresh();
    },
  });

  return (
    <>
      <div className={styles.toolbar}>
        <Button
          icon={match.isPending ? <Spinner size="tiny" /> : <ArrowClockwiseRegular />}
          disabled={match.isPending}
          onClick={() => match.mutate()}
        >
          Match again
        </Button>
        <Caption1 className={styles.muted}>
          After recording a missing receipt, match again: transactions that did not match look for it.
        </Caption1>
      </div>
      <DataTable
        ariaLabel="Settled transactions to look at"
        rows={list.data?.items}
        loading={list.isLoading}
        error={list.error}
        onRetry={() => void list.refetch()}
        getRowId={(line) => line.id}
        columns={[
          {
            key: 'why',
            header: 'Why',
            // Narrow enough for a phone: the badges are short.
            width: 160,
            render: (line) => (
              <StatusBadge status={line.match} tone={MATCH_TONE[line.match]} label={POS_MATCH_LABEL[line.match]} />
            ),
          },
          { key: 'what', header: 'Transaction', render: (line) => <TransactionCell line={line} /> },
          {
            key: 'match',
            header: 'In HC360',
            priority: 'low',
            render: (line) => <Caption1>{matchText(line)}</Caption1>,
          },
          { key: 'amount', header: 'Amount', align: 'end', width: 120, render: (line) => <LineAmount line={line} /> },
        ]}
        rowActions={(line) => (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button
                size="small"
                appearance="subtle"
                icon={<MoreHorizontalRegular />}
                aria-label={`Actions for the transaction of ${formatDateOnly(line.settledOn)}`}
                {...restoreFocusTarget}
              />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem icon={<CheckmarkCircleRegular />} onClick={() => setReviewing(line)}>
                  Mark reviewed…
                </MenuItem>
                {line.billId && (
                  <MenuItem
                    icon={<ReceiptRegular />}
                    onClick={() => void navigate({ to: '/billing/bills/$billId', params: { billId: line.billId! } })}
                  >
                    Open the bill
                  </MenuItem>
                )}
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        emptyTitle="Nothing to look at"
        emptyDescription="Every settled card and UPI transaction imported matches HC360's receipts and refunds, or was reviewed."
        page={page}
        pageSize={25}
        totalCount={list.data?.totalCount}
        onPageChange={onPage}
        size="small"
      />
      <ReasonDialog
        open={reviewing !== null}
        title="Mark this transaction reviewed?"
        description={
          reviewing
            ? `${POS_MATCH_LABEL[reviewing.match]}: ${references(reviewing)}. Say what you found or did. Your note is kept with it.`
            : undefined
        }
        label="What you found or did"
        placeholder="For example: paid at the pharmacy counter; or the acquirer reversed the chargeback on 12 October"
        confirmLabel="Mark reviewed"
        maxLength={500}
        busy={review.isPending}
        error={review.error}
        onConfirm={(note) => reviewing && review.mutate({ line: reviewing, note })}
        onCancel={() => {
          setReviewing(null);
          review.reset();
        }}
      />
    </>
  );
}

/** Card and UPI receipts the acquirer has not settled after the expected days, oldest first. */
export function PosUnsettled({ settlementDays }: { settlementDays: number }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const list = useQuery(posSettlementQueries.unsettled());
  return (
    <DataTable
      ariaLabel="Card and UPI receipts not settled"
      rows={list.data}
      loading={list.isLoading}
      error={list.error}
      onRetry={() => void list.refetch()}
      getRowId={(receipt) => receipt.paymentId}
      onRowClick={(receipt) =>
        receipt.billId && void navigate({ to: '/billing/bills/$billId', params: { billId: receipt.billId } })
      }
      columns={[
        {
          key: 'receipt',
          header: 'Receipt',
          render: (receipt) => (
            <span className={styles.stack}>
              <Text weight="semibold">{receipt.receiptNumber}</Text>
              <Caption1 className={styles.muted}>{formatDateTime(receipt.receivedAt)}</Caption1>
            </span>
          ),
        },
        {
          key: 'how',
          header: 'Paid by',
          render: (receipt) => (
            <span className={styles.stack}>
              <Text>{receipt.mode === 'Upi' ? 'UPI' : 'Card'}</Text>
              <Caption1 className={styles.muted}>
                {[
                  receipt.reference && `Reference ${receipt.reference}`,
                  receipt.cardLast4 && `Card ending ${receipt.cardLast4}`,
                  receipt.terminalId && `Terminal ${receipt.terminalId}`,
                ]
                  .filter(Boolean)
                  .join(' · ') || 'No reference recorded'}
              </Caption1>
            </span>
          ),
        },
        {
          key: 'amount',
          header: 'Amount',
          align: 'end',
          width: 140,
          render: (r) => <Amount value={r.amount} strong />,
        },
        {
          key: 'waiting',
          header: 'Waiting',
          align: 'end',
          width: 110,
          render: (receipt) => `${receipt.daysWaiting} days`,
        },
      ]}
      emptyTitle="Nothing waiting"
      emptyDescription={`The acquirer's reports carry every card and UPI receipt older than ${settlementDays} days.`}
      size="small"
    />
  );
}

/**
 * The payouts by day: what reached the bank under each UTR, with the payments, refunds, charges and GST in it and its
 * settlement journal's number. Choose a payout to see its transactions.
 */
export function PosPayouts({
  from,
  to,
  onRange,
}: {
  from: string;
  to: string;
  onRange: (from: string, to: string) => void;
}) {
  const styles = useStyles();
  const [open, setOpen] = useState<PosPayoutDto | null>(null);
  const [page, setPage] = useState(1);
  const days = useQuery(posSettlementQueries.days(from, to));
  const payouts = (days.data ?? []).flatMap((day) => day.payouts);
  const lines = useQuery({
    ...posSettlementQueries.lines({ settledOn: open?.settledOn, utr: open?.utr ?? null, page }),
    enabled: open !== null,
  });

  return (
    <>
      <div style={{ padding: '8px 16px 12px' }}>
        <FilterBar>
          <Field label="Paid from">
            <Input type="date" value={from} max={to} onChange={(_, d) => d.value && onRange(d.value, to)} />
          </Field>
          <Field label="To">
            <Input type="date" value={to} onChange={(_, d) => d.value && onRange(from, d.value)} />
          </Field>
        </FilterBar>
      </div>
      <DataTable
        ariaLabel="Payouts by day"
        rows={payouts}
        loading={days.isLoading}
        error={days.error}
        onRetry={() => void days.refetch()}
        getRowId={(p) => `${p.settledOn}|${p.utr ?? ''}`}
        onRowClick={(p) => {
          setOpen(open?.settledOn === p.settledOn && open.utr === p.utr ? null : p);
          setPage(1);
        }}
        columns={[
          {
            key: 'payout',
            header: 'Payout',
            render: (p) => (
              <span className={styles.stack}>
                <Text weight="semibold">{formatDateOnly(p.settledOn)}</Text>
                <Caption1 className={styles.muted}>
                  {p.utr ? `UTR ${p.utr}` : 'No UTR in the report'} · {p.acquirers}
                </Caption1>
                <Caption1 className={styles.muted}>Tally journal {p.voucherNumber}</Caption1>
              </span>
            ),
          },
          { key: 'count', header: 'Transactions', align: 'end', width: 120, priority: 'low', render: (p) => p.lines },
          { key: 'sales', header: 'Payments', align: 'end', render: (p) => <Amount value={p.sales} /> },
          {
            key: 'refunds',
            header: 'Taken back',
            align: 'end',
            priority: 'low',
            render: (p) => <Amount value={p.refunds} />,
          },
          {
            key: 'charges',
            header: 'Charges',
            align: 'end',
            priority: 'low',
            render: (p) => <Amount value={p.charges} />,
          },
          { key: 'gst', header: 'GST', align: 'end', priority: 'low', render: (p) => <Amount value={p.tax} /> },
          { key: 'net', header: 'Paid to the bank', align: 'end', render: (p) => <Amount value={p.net} strong /> },
          {
            key: 'attention',
            header: 'To look at',
            align: 'end',
            width: 130,
            render: (p) =>
              p.toLookAt === 0 ? (
                <StatusBadge status="Matched" tone="success" label="All matched" />
              ) : (
                <StatusBadge status="Unmatched" tone="warning" label={`${p.toLookAt} to look at`} />
              ),
          },
        ]}
        emptyTitle="No payouts in this period"
        emptyDescription="Import the acquirer's settlement report to see what it paid to the bank."
        size="small"
      />
      {open && (
        <Section
          title={`Transactions of ${formatDateOnly(open.settledOn)}${open.utr ? `, UTR ${open.utr}` : ''}`}
          flush
        >
          <DataTable
            ariaLabel="Transactions of the payout"
            rows={lines.data?.items}
            loading={lines.isLoading}
            error={lines.error}
            onRetry={() => void lines.refetch()}
            getRowId={(line) => line.id}
            columns={[
              { key: 'what', header: 'Transaction', render: (line) => <TransactionCell line={line} /> },
              {
                key: 'match',
                header: 'In HC360',
                render: (line) => (
                  <span className={styles.stack}>
                    <StatusBadge
                      status={line.match}
                      tone={MATCH_TONE[line.match]}
                      label={POS_MATCH_LABEL[line.match]}
                    />
                    <Caption1 className={styles.muted}>
                      {line.reviewedAt
                        ? `Reviewed by ${line.reviewedByName ?? 'someone'}: ${line.reviewNote ?? ''}`
                        : matchText(line)}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'charges',
                header: 'Charges and GST',
                align: 'end',
                priority: 'low',
                render: (l) => <Amount value={l.charges + l.tax} />,
              },
              {
                key: 'amount',
                header: 'Amount',
                align: 'end',
                width: 140,
                render: (line) => <LineAmount line={line} />,
              },
            ]}
            emptyTitle="No transactions"
            page={page}
            pageSize={25}
            totalCount={lines.data?.totalCount}
            onPageChange={setPage}
            size="small"
          />
        </Section>
      )}
      <Caption1 block className={styles.muted} style={{ padding: '8px 16px 12px' }}>
        Charges are the acquirer&apos;s MDR without GST. Each payout is a journal in the Tally day book of its day: the
        bank for what was paid, the charges and input GST, and the gross out of the Card and UPI settlement ledgers.
      </Caption1>
    </>
  );
}

/** The reports imported, newest first: who imported what, and what was new. */
export function PosImports({ page, onPage }: { page: number; onPage: (page: number) => void }) {
  const styles = useStyles();
  const list = useQuery(posSettlementQueries.imports(page));
  return (
    <DataTable
      ariaLabel="Settlement reports imported"
      rows={list.data?.items}
      loading={list.isLoading}
      error={list.error}
      onRetry={() => void list.refetch()}
      getRowId={(i) => i.id}
      columns={[
        {
          key: 'file',
          header: 'Report',
          render: (i) => (
            <span className={styles.stack}>
              <Text weight="semibold">{i.fileName}</Text>
              <Caption1 className={styles.muted}>
                {i.acquirer}
                {i.firstSettledOn && i.lastSettledOn
                  ? ` · paid ${formatDateOnly(i.firstSettledOn)}${i.lastSettledOn !== i.firstSettledOn ? ` to ${formatDateOnly(i.lastSettledOn)}` : ''}`
                  : ''}
              </Caption1>
            </span>
          ),
        },
        {
          key: 'rows',
          header: 'Transactions',
          render: (i) => (
            <span className={styles.stack}>
              <Text>{i.added} new</Text>
              <Caption1 className={styles.muted}>
                {i.alreadyImported > 0 ? `${i.alreadyImported} already imported · ` : ''}
                {i.ignored > 0 ? `${i.ignored} rows left out` : 'no rows left out'}
              </Caption1>
            </span>
          ),
        },
        {
          key: 'when',
          header: 'Imported',
          width: 220,
          priority: 'low',
          render: (i) => `${formatDateTime(i.importedAt)}${i.importedByName ? ` by ${i.importedByName}` : ''}`,
        },
      ]}
      emptyTitle="No reports imported yet"
      emptyDescription="Import the acquirer's settlement report to reconcile card and UPI payments."
      page={page}
      pageSize={25}
      totalCount={list.data?.totalCount}
      onPageChange={onPage}
      size="small"
    />
  );
}
