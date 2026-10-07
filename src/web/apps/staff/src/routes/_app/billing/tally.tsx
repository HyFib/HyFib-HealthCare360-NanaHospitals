import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  mergeClasses,
  Spinner,
  Tab,
  TabList,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  ArrowSyncRegular,
  BookRegular,
  MoreHorizontalRegular,
  OpenRegular,
  SendRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  ErrorState,
  FilterBar,
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
import { TALLY_EXPORT_STATUS_LABEL } from '@/modules/billing/labels';
import { fromPaise, toPaise } from '@/modules/billing/money';
import { dateParam } from '@/modules/billing/searchParams';
import { TallyDayDrawer } from '@/modules/billing/tally/TallyDay';
import { TallyLedgerSettings } from '@/modules/billing/tally/TallyLedgerForm';
import { CHECK_LABEL, CHECK_TONE, confirmedSummary, EXPORT_STATUS_TONE } from '@/modules/billing/tally/tallyView';
import {
  tallyApi,
  tallyKeys,
  tallyQueries,
  type AccountingVoucher,
  type TallyExportDto,
} from '@/modules/billing/tallyApi';
import { ActionError, Amount, NoAccess } from '@/modules/billing/ui';

type TallyView = 'exports' | 'daybook' | 'ledgers';

interface TallySearch {
  view?: TallyView;
  date?: string;
  /** The day open in the drawer. */
  day?: string;
}

export const Route = createFileRoute('/_app/billing/tally')({
  validateSearch: (s: Record<string, unknown>): TallySearch => ({
    view: s.view === 'daybook' || s.view === 'ledgers' ? s.view : undefined,
    date: dateParam(s.date),
    day: dateParam(s.day),
  }),
  component: TallyPage,
});

const MAX_DAYS = 31;

const useStyles = makeStyles({
  voucher: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '10px 12px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  entries: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 130px 130px', gap: '2px 12px' },
  head: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  num: { textAlign: 'end' },
  checks: { display: 'flex', gap: '4px', flexWrap: 'wrap' },
  ledgers: { padding: '8px 16px 16px' },
});

/** Saves the Tally import file of a date (for Tally on a computer the server cannot reach). */
async function download(date: string): Promise<void> {
  const file = await tallyApi.exportFile(date);
  const extension = file.blob.type.includes('xml') ? 'xml' : file.blob.type.includes('json') ? 'json' : 'txt';
  const url = URL.createObjectURL(file.blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = file.fileName ?? `Tally-day-book-${date}.${extension}`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

function daysBetween(from: string, to: string): number {
  return Math.round((Date.parse(to) - Date.parse(from)) / 86_400_000);
}

/**
 * Tally day book (INT-TALLY): the posting log with each day's reconciliation, re-posting, the import file for Tally
 * the server cannot reach, a preview of the vouchers, and the ledger names the day book posts to. A day opens in a
 * panel with every voucher and what Tally answered for it.
 */
function TallyPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canExport = usePermission('billing.tally.export');
  const canConfigure = usePermission('billing.tally.configure');
  const styles = useStyles();
  const view = search.view ?? 'exports';

  if (!canExport && !canConfigure) {
    return (
      <Page>
        <PageHeader title="Tally export" />
        <NoAccess what="the Tally export" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Tally export"
        subtitle="The day book of bills, receipts, refunds, credit notes, doctor fees, pharmacy and canteen, posted to Tally every night and checked against it"
        icon={<BookRegular />}
      />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={canExport ? view : 'ledgers'}
            onTabSelect={(_, d) =>
              void navigate({
                search: (prev) => ({ ...prev, view: d.value === 'exports' ? undefined : (d.value as TallyView) }),
              })
            }
          >
            {canExport && <Tab value="exports">Posting log</Tab>}
            {canExport && <Tab value="daybook">Day book preview</Tab>}
            <Tab value="ledgers">Ledger names</Tab>
          </TabList>
        </div>
        {!canExport || view === 'ledgers' ? (
          <div className={styles.ledgers}>
            <TallyLedgerSettings canEdit={canConfigure} />
          </div>
        ) : view === 'exports' ? (
          <ExportsView
            openDay={search.day}
            onOpenDay={(day) => void navigate({ search: (prev) => ({ ...prev, day }) })}
          />
        ) : (
          <DayBookView
            date={search.date ?? todayIso(-1)}
            onDate={(date) => void navigate({ search: (prev) => ({ ...prev, date }) })}
          />
        )}
      </Section>
    </Page>
  );
}

function CheckBadges({ day }: { day: TallyExportDto }) {
  const styles = useStyles();
  return (
    <div className={styles.checks}>
      <StatusBadge
        status={day.recordsCheck}
        tone={CHECK_TONE[day.recordsCheck]}
        label={`Records: ${CHECK_LABEL[day.recordsCheck].toLowerCase()}`}
      />
      <StatusBadge
        status={day.tallyCheck}
        tone={CHECK_TONE[day.tallyCheck]}
        label={`Tally: ${CHECK_LABEL[day.tallyCheck].toLowerCase()}`}
      />
    </div>
  );
}

function ExportsView({
  openDay,
  onOpenDay,
}: {
  openDay: string | undefined;
  onOpenDay: (day: string | undefined) => void;
}) {
  const notify = useNotify();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const queryClient = useQueryClient();
  const [from, setFrom] = useState(todayIso(-30));
  const [to, setTo] = useState(todayIso());
  const [postFrom, setPostFrom] = useState(todayIso(-1));
  const [postTo, setPostTo] = useState('');
  const [confirming, setConfirming] = useState<{ from: string; to?: string } | null>(null);
  const [downloading, setDownloading] = useState<string | null>(null);
  const exports = useQuery(tallyQueries.exports(from, to));
  const rangeProblem =
    postTo && postFrom
      ? daysBetween(postFrom, postTo) < 0
        ? 'The last day must not be before the first.'
        : daysBetween(postFrom, postTo) >= MAX_DAYS
          ? `Post at most ${MAX_DAYS} days at a time.`
          : null
      : null;

  const queue = useMutation({
    mutationFn: (range: { from: string; to?: string }) => tallyApi.queue(range.from, range.to),
    onSuccess: (_, range) => {
      notify.success(
        'Day book queued',
        range.to
          ? `The days from ${formatDateOnly(range.from)} to ${formatDateOnly(range.to)} will be posted to Tally in a moment.`
          : `${formatDateOnly(range.from)} will be posted to Tally in a moment.`,
      );
      setConfirming(null);
      void queryClient.invalidateQueries({ queryKey: tallyKeys.all });
    },
  });

  const save = async (date: string) => {
    setDownloading(date);
    try {
      await download(date);
      void queryClient.invalidateQueries({ queryKey: tallyKeys.all });
    } catch (error) {
      notify.error(error, 'Could not download the day book');
    } finally {
      setDownloading(null);
    }
  };

  return (
    <>
      <div style={{ padding: '8px 16px 12px' }}>
        <FilterBar>
          <Field label="From">
            <Input type="date" value={from} max={to} onChange={(_, d) => setFrom(d.value || todayIso(-30))} />
          </Field>
          <Field label="To">
            <Input type="date" value={to} max={todayIso()} onChange={(_, d) => setTo(d.value || todayIso())} />
          </Field>
          <Field label="Post the day book of">
            <Input type="date" value={postFrom} max={todayIso()} onChange={(_, d) => setPostFrom(d.value)} />
          </Field>
          <Field
            label="Up to (optional)"
            validationState={rangeProblem ? 'error' : 'none'}
            validationMessage={rangeProblem ?? undefined}
          >
            <Input type="date" value={postTo} min={postFrom} max={todayIso()} onChange={(_, d) => setPostTo(d.value)} />
          </Field>
          <Button
            appearance="primary"
            icon={<SendRegular />}
            disabled={!postFrom || !!rangeProblem}
            onClick={() => setConfirming({ from: postFrom, to: postTo && postTo !== postFrom ? postTo : undefined })}
          >
            Post to Tally
          </Button>
          <Button
            icon={downloading === postFrom ? <Spinner size="tiny" /> : <ArrowDownloadRegular />}
            disabled={!postFrom || downloading !== null}
            onClick={() => void save(postFrom)}
          >
            Download file
          </Button>
        </FilterBar>
      </div>
      <DataTable
        ariaLabel="Tally posting log"
        rows={exports.data}
        loading={exports.isLoading}
        error={exports.error}
        onRetry={() => void exports.refetch()}
        getRowId={(e) => e.id}
        onRowClick={(e) => onOpenDay(e.date)}
        columns={[
          {
            key: 'date',
            header: 'Day',
            width: 130,
            render: (e) => <Text weight="semibold">{formatDateOnly(e.date)}</Text>,
          },
          {
            key: 'status',
            header: 'Status',
            width: 190,
            render: (e) => (
              <StatusBadge
                status={e.status}
                tone={EXPORT_STATUS_TONE[e.status]}
                label={TALLY_EXPORT_STATUS_LABEL[e.status]}
              />
            ),
          },
          { key: 'vouchers', header: 'Vouchers', width: 150, render: (e) => confirmedSummary(e) },
          { key: 'checks', header: 'Reconciliation', width: 260, render: (e) => <CheckBadges day={e} /> },
          {
            key: 'attempt',
            header: 'Last attempt',
            priority: 'low',
            render: (e) => (e.lastAttemptAt ? `${formatDateTime(e.lastAttemptAt)} (${e.attempts})` : '—'),
          },
          {
            key: 'message',
            header: 'Message',
            priority: 'low',
            render: (e) => e.message ?? (e.postedAt ? `Posted ${formatDateTime(e.postedAt)}` : '—'),
          },
        ]}
        rowActions={(e) => (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button
                size="small"
                appearance="subtle"
                icon={<MoreHorizontalRegular />}
                aria-label={`Actions for ${formatDateOnly(e.date)}`}
                {...restoreFocusTarget}
              />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem icon={<OpenRegular />} onClick={() => onOpenDay(e.date)}>
                  Open the day
                </MenuItem>
                <MenuItem icon={<ArrowSyncRegular />} onClick={() => setConfirming({ from: e.date })}>
                  Post again
                </MenuItem>
                <MenuItem icon={<ArrowDownloadRegular />} onClick={() => void save(e.date)}>
                  Download the import file
                </MenuItem>
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        emptyTitle="Nothing posted in this period"
        emptyDescription="The previous day is queued automatically every night after 00:30."
        size="small"
      />
      {openDay && (
        <TallyDayDrawer
          date={openDay}
          day={exports.data?.find((e) => e.date === openDay)}
          onClose={() => onOpenDay(undefined)}
          onPost={() => setConfirming({ from: openDay })}
          onDownload={() => void save(openDay)}
          downloading={downloading === openDay}
        />
      )}
      <ConfirmDialog
        open={confirming !== null}
        title={
          confirming?.to
            ? `Post the day books of ${formatDateOnly(confirming.from)} to ${formatDateOnly(confirming.to)}?`
            : `Post the day book of ${confirming ? formatDateOnly(confirming.from) : ''}?`
        }
        confirmLabel="Post to Tally"
        busy={queue.isPending}
        onConfirm={() => confirming && queue.mutate(confirming)}
        onCancel={() => {
          setConfirming(null);
          queue.reset();
        }}
      >
        Vouchers already in Tally are updated, not duplicated. When Tally is not set up, the day waits for its file to
        be imported.
        <ActionError error={queue.error} />
      </ConfirmDialog>
    </>
  );
}

function sumEntries(voucher: AccountingVoucher, debit: boolean): number {
  return fromPaise(
    voucher.entries.filter((e) => e.isDebit === debit).reduce((total, e) => total + toPaise(e.amount), 0n),
  );
}

function DayBookView({ date, onDate }: { date: string; onDate: (date: string) => void }) {
  const styles = useStyles();
  const vouchers = useQuery(tallyQueries.dayBook(date));
  const all = vouchers.data ?? [];
  const debit = fromPaise(all.reduce((total, v) => total + toPaise(sumEntries(v, true)), 0n));
  const credit = fromPaise(all.reduce((total, v) => total + toPaise(sumEntries(v, false)), 0n));
  const unbalanced = all.filter((v) => toPaise(sumEntries(v, true)) !== toPaise(sumEntries(v, false)));

  return (
    <>
      <div style={{ padding: '8px 16px 12px' }}>
        <FilterBar>
          <Field label="Day">
            <Input type="date" value={date} max={todayIso()} onChange={(_, d) => d.value && onDate(d.value)} />
          </Field>
          {vouchers.data && (
            <Caption1 className={styles.muted}>
              {all.length} voucher{all.length === 1 ? '' : 's'} · debit <Amount value={debit} /> · credit{' '}
              <Amount value={credit} />
              {unbalanced.length > 0 ? ` · ${unbalanced.length} unbalanced` : ' · balanced'}
            </Caption1>
          )}
        </FilterBar>
      </div>
      {vouchers.isLoading ? (
        <LoadingState label="Building the day book…" />
      ) : vouchers.error ? (
        <ErrorState error={vouchers.error} onRetry={() => void vouchers.refetch()} />
      ) : all.length === 0 ? (
        <Text className={styles.muted} style={{ padding: 16, display: 'block' }}>
          Nothing was billed, received or paid on this day.
        </Text>
      ) : (
        <div role="list" aria-label="Vouchers">
          {all.map((v) => (
            <div key={`${v.voucherType}-${v.number}`} className={styles.voucher} role="listitem">
              <div className={styles.head}>
                <Badge appearance="tint">{v.voucherType}</Badge>
                <Text weight="semibold">{v.number}</Text>
                <Caption1 className={styles.muted}>
                  {v.narration}
                  {v.remoteId ? ` · ${v.remoteId}` : ''}
                </Caption1>
              </div>
              <div className={styles.entries}>
                <Caption1 className={styles.muted}>Ledger</Caption1>
                <Caption1 className={mergeClasses(styles.muted, styles.num)}>Debit</Caption1>
                <Caption1 className={mergeClasses(styles.muted, styles.num)}>Credit</Caption1>
                {v.entries.map((entry, index) => (
                  <div key={`${entry.ledgerName}-${index}`} style={{ display: 'contents' }}>
                    <Text>
                      {entry.ledgerName}
                      {entry.costCentre ? <Caption1 className={styles.muted}> · {entry.costCentre}</Caption1> : null}
                    </Text>
                    <span className={styles.num}>{entry.isDebit ? <Amount value={entry.amount} /> : ''}</span>
                    <span className={styles.num}>{entry.isDebit ? '' : <Amount value={entry.amount} />}</span>
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>
      )}
    </>
  );
}
