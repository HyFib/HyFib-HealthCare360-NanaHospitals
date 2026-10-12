import {
  Button,
  Caption1,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Spinner,
  Subtitle2,
  Tab,
  TabList,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  DismissRegular,
  DocumentCheckmarkRegular,
  ScanTextRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { ConfirmDialog, DataTable, ErrorState, LoadingState, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { TALLY_EXPORT_STATUS_LABEL } from '../labels';
import {
  tallyApi,
  tallyKeys,
  tallyQueries,
  type TallyDayCheck,
  type TallyExportDto,
  type TallyVoucherDto,
} from '../tallyApi';
import { Amount, ActionError } from '../ui';
import {
  ANSWER_LABEL,
  canMarkImported,
  CHECK_LABEL,
  CHECK_TONE,
  EXPORT_STATUS_TONE,
  importQuestion,
  MATCH_LABEL,
  needsAttention,
  sendingSummary,
  VOUCHER_STATUS_LABEL,
  VOUCHER_STATUS_TONE,
  whatToDo,
} from './tallyView';

const PAGE_SIZE = 100;

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '20px', paddingBottom: '24px' },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  block: { display: 'flex', flexDirection: 'column', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
  lines: { width: '100%', borderCollapse: 'collapse' },
  cell: {
    padding: '6px 8px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    textAlign: 'start',
    verticalAlign: 'top',
  },
  num: { textAlign: 'end', whiteSpace: 'nowrap' },
  head: { color: tokens.colorNeutralForeground3, fontWeight: tokens.fontWeightRegular },
  differs: { backgroundColor: tokens.colorPaletteRedBackground1 },
  list: { margin: 0, paddingInlineStart: '20px' },
});

/** The day book compared with its records, and the last check against Tally. */
export function ReconciliationPanel({ check }: { check: TallyDayCheck }) {
  const styles = useStyles();
  const lines = check.lines.filter(
    (l) => !l.matches || l.records !== 0 || l.dayBook !== 0 || (l.recordsCount ?? 0) > 0,
  );
  return (
    <div className={styles.block}>
      <div className={styles.row}>
        <Subtitle2 as="h3">Records</Subtitle2>
        <StatusBadge status={check.records} tone={CHECK_TONE[check.records]} label={CHECK_LABEL[check.records]} />
      </div>
      <Text className={styles.muted}>{sendingSummary(check.sending)}</Text>
      {lines.length > 0 && (
        <table className={styles.lines} aria-label="The day book compared with the records">
          <thead>
            <tr>
              <th className={mergeClasses(styles.cell, styles.head)}>Figure</th>
              <th className={mergeClasses(styles.cell, styles.head, styles.num)}>Records</th>
              <th className={mergeClasses(styles.cell, styles.head, styles.num)}>Day book</th>
              <th className={mergeClasses(styles.cell, styles.head, styles.num)}>Difference</th>
            </tr>
          </thead>
          <tbody>
            {lines.map((line) => (
              <tr key={`${line.group}-${line.item}`} className={line.matches ? undefined : styles.differs}>
                <td className={styles.cell}>
                  <Text>
                    {line.group}: {line.item}
                  </Text>
                  {(line.recordsCount !== null || line.note) && (
                    <Caption1 block className={styles.muted}>
                      {line.recordsCount !== null &&
                        `${line.recordsCount} in the records, ${line.dayBookCount ?? 0} in the day book`}
                      {line.recordsCount !== null && line.note ? ' · ' : ''}
                      {line.note}
                    </Caption1>
                  )}
                </td>
                <td className={mergeClasses(styles.cell, styles.num)}>
                  {line.informational ? '—' : <Amount value={line.records} />}
                </td>
                <td className={mergeClasses(styles.cell, styles.num)}>
                  <Amount value={line.dayBook} />
                </td>
                <td className={mergeClasses(styles.cell, styles.num)}>
                  {line.informational ? (
                    <Text className={styles.muted}>Totals only</Text>
                  ) : line.matches ? (
                    '—'
                  ) : (
                    <Amount value={line.difference} tone="negative" />
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {check.notes.length > 0 && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <ul className={styles.list}>
              {check.notes.map((note) => (
                <li key={note}>{note}</li>
              ))}
            </ul>
          </MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.row}>
        <Subtitle2 as="h3">Tally</Subtitle2>
        <StatusBadge status={check.tally} tone={CHECK_TONE[check.tally]} label={CHECK_LABEL[check.tally]} />
        {check.tallyCheckedAt && (
          <Caption1 className={styles.muted}>Read back {formatDateTime(check.tallyCheckedAt)}</Caption1>
        )}
      </div>
      {check.tallyMessage && <Text className={styles.muted}>{check.tallyMessage}</Text>}
    </div>
  );
}

/** The day's vouchers with Tally's answer and what to do about each; "needs attention" first when any does. */
export function VoucherList({ vouchers }: { vouchers: TallyVoucherDto[] }) {
  const styles = useStyles();
  const attention = vouchers.filter(needsAttention);
  const [show, setShow] = useState<'attention' | 'all'>(attention.length > 0 ? 'attention' : 'all');
  const [page, setPage] = useState(1);
  const rows = show === 'attention' ? attention : vouchers;
  return (
    <div className={styles.block}>
      <div className={styles.row}>
        <Subtitle2 as="h3">Vouchers</Subtitle2>
        <TabList
          size="small"
          selectedValue={show}
          onTabSelect={(_, data) => {
            setShow(data.value === 'attention' ? 'attention' : 'all');
            setPage(1);
          }}
        >
          <Tab value="attention">Needs attention ({attention.length})</Tab>
          <Tab value="all">All ({vouchers.length})</Tab>
        </TabList>
      </div>
      <DataTable
        ariaLabel="Vouchers of the day"
        rows={rows.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE)}
        getRowId={(v) => v.key}
        page={page}
        pageSize={PAGE_SIZE}
        totalCount={rows.length}
        onPageChange={setPage}
        size="small"
        emptyTitle={show === 'attention' ? 'Nothing needs attention' : 'No vouchers'}
        emptyDescription={show === 'attention' ? 'Tally holds every voucher of this day as HC360 has it.' : undefined}
        columns={[
          { key: 'type', header: 'Type', width: 110, render: (v) => v.voucherType },
          {
            key: 'number',
            header: 'Number',
            render: (v) => (
              <>
                <Text weight="semibold">{v.number}</Text>
                {v.tallyNumber && v.tallyNumber !== v.number && (
                  <Caption1 block className={styles.muted}>
                    Tally number {v.tallyNumber}
                  </Caption1>
                )}
              </>
            ),
          },
          { key: 'amount', header: 'Amount', align: 'end', width: 130, render: (v) => <Amount value={v.amount} /> },
          {
            key: 'status',
            header: 'Status',
            width: 170,
            render: (v) => (
              <StatusBadge
                status={v.status}
                tone={VOUCHER_STATUS_TONE[v.status]}
                label={VOUCHER_STATUS_LABEL[v.status]}
              />
            ),
          },
          {
            key: 'tally',
            header: "Tally's answer",
            priority: 'low',
            render: (v) => (
              <>
                <Text>
                  {ANSWER_LABEL[v.answer]}
                  {v.match !== 'NotChecked' && ` · ${MATCH_LABEL[v.match]}`}
                </Text>
                {(v.answerMessage ?? v.tallyDetail) && (
                  <Caption1 block className={styles.muted}>
                    {v.answerMessage ?? v.tallyDetail}
                  </Caption1>
                )}
                {whatToDo(v) && (
                  <Caption1 block>
                    <strong>What to do:</strong> {whatToDo(v)}
                  </Caption1>
                )}
              </>
            ),
          },
        ]}
      />
    </div>
  );
}

/**
 * One day of the Tally day book: where the posting stands, the reconciliation with the records and with Tally, and
 * every voucher with Tally's answer. Actions: post again, download the file, mark it imported, and run the checks.
 */
export function TallyDayDrawer({
  date,
  day,
  onClose,
  onPost,
  onDownload,
  downloading,
}: {
  date: string;
  /** The day's posting record; undefined when the day has not been posted. */
  day: TallyExportDto | undefined;
  onClose: () => void;
  onPost: () => void;
  onDownload: () => void;
  downloading: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const check = useQuery(tallyQueries.dayCheck(date));
  const vouchers = useQuery(tallyQueries.dayVouchers(date));
  const [importing, setImporting] = useState(false);
  // The confirmations these buttons open return focus to them, inside the drawer, not to the page behind it.
  const restoreFocusTarget = useRestoreFocusTarget();

  const refresh = () => void queryClient.invalidateQueries({ queryKey: tallyKeys.all });
  const run = useMutation({
    mutationFn: (againstTally: boolean) => tallyApi.check(date, againstTally),
    onSuccess: (result, againstTally) => {
      queryClient.setQueryData(tallyQueries.dayCheck(date).queryKey, result);
      refresh();
      notify.success(
        againstTally ? 'Checked against Tally' : 'Checked against the records',
        againstTally ? CHECK_LABEL[result.tally] : CHECK_LABEL[result.records],
      );
    },
  });
  const markImported = useMutation({
    mutationFn: () => tallyApi.markImported(date),
    onSuccess: () => {
      setImporting(false);
      refresh();
      notify.success('Marked imported', `The day book of ${formatDateOnly(date)} is recorded as imported into Tally.`);
    },
  });
  const question = importQuestion(formatDateOnly(date), vouchers.data?.vouchers ?? []);

  return (
    <OverlayDrawer
      open
      position="end"
      size="large"
      onOpenChange={(_, data) => !data.open && !run.isPending && onClose()}
    >
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} onClick={onClose} />}
        >
          Day book of {formatDateOnly(date)}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.body}>
          <div className={styles.block}>
            <div className={styles.row}>
              {day ? (
                <StatusBadge
                  status={day.status}
                  tone={EXPORT_STATUS_TONE[day.status]}
                  label={TALLY_EXPORT_STATUS_LABEL[day.status]}
                />
              ) : (
                <StatusBadge status="NotPosted" tone="subtle" label="Not posted" />
              )}
              {day?.importedAt && (
                <Caption1 className={styles.muted}>
                  Imported {formatDateTime(day.importedAt)}
                  {day.importedByName ? ` by ${day.importedByName}` : ''}
                </Caption1>
              )}
            </div>
            {day?.message && <Text>{day.message}</Text>}
            <div className={styles.row}>
              <Button icon={<ArrowSyncRegular />} onClick={onPost} {...restoreFocusTarget}>
                Post to Tally
              </Button>
              <Button
                icon={downloading ? <Spinner size="tiny" aria-hidden /> : <ArrowDownloadRegular />}
                disabled={downloading}
                // Busy buttons here stay focusable: a disabled button drops focus out of the drawer.
                disabledFocusable={downloading}
                onClick={onDownload}
              >
                Download file
              </Button>
              {canMarkImported(day) && (
                <Button icon={<DocumentCheckmarkRegular />} onClick={() => setImporting(true)} {...restoreFocusTarget}>
                  Mark imported
                </Button>
              )}
              <Button
                icon={
                  run.isPending && run.variables === false ? (
                    <Spinner size="tiny" aria-hidden />
                  ) : (
                    <CheckmarkCircleRegular />
                  )
                }
                disabled={run.isPending}
                disabledFocusable={run.isPending}
                onClick={() => run.mutate(false)}
              >
                Check records
              </Button>
              <Button
                icon={
                  run.isPending && run.variables === true ? <Spinner size="tiny" aria-hidden /> : <ScanTextRegular />
                }
                disabled={run.isPending || !check.data?.canCheckTally}
                disabledFocusable={run.isPending}
                title={check.data?.canCheckTally ? undefined : 'Post the day to Tally first; Tally must be set up.'}
                onClick={() => run.mutate(true)}
              >
                Check against Tally
              </Button>
            </div>
            <ActionError error={run.error} title="The check did not run" />
          </div>

          {check.isLoading ? (
            <LoadingState label="Comparing the day book with the records…" />
          ) : check.error ? (
            <ErrorState error={check.error} onRetry={() => void check.refetch()} />
          ) : check.data ? (
            <ReconciliationPanel check={check.data} />
          ) : null}

          {vouchers.isLoading ? (
            <LoadingState label="Loading the vouchers…" />
          ) : vouchers.error ? (
            <ErrorState error={vouchers.error} onRetry={() => void vouchers.refetch()} />
          ) : vouchers.data ? (
            <VoucherList key={vouchers.dataUpdatedAt} vouchers={vouchers.data.vouchers} />
          ) : null}
        </div>
      </DrawerBody>
      <ConfirmDialog
        open={importing}
        title={question.title}
        confirmLabel="Mark imported"
        busy={markImported.isPending}
        onConfirm={() => markImported.mutate()}
        onCancel={() => {
          setImporting(false);
          markImported.reset();
        }}
      >
        <div className={styles.block}>
          <Text>{question.body}</Text>
          {question.deleteFirst.length > 0 && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Delete these vouchers in Tally first</MessageBarTitle>
                HC360 sent them before, but they are no longer in its day book:
                <ul className={styles.list}>
                  {question.deleteFirst.map((item) => (
                    <li key={item}>{item}</li>
                  ))}
                </ul>
              </MessageBarBody>
            </MessageBar>
          )}
          <ActionError error={markImported.error} title="The day was not marked imported" />
        </div>
      </ConfirmDialog>
    </OverlayDrawer>
  );
}
