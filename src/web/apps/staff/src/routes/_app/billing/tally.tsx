import { Badge, Button, Caption1, Field, Input, makeStyles, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, mergeClasses, Spinner, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { ArrowDownloadRegular, ArrowSyncRegular, BookRegular, MoreHorizontalRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import { ConfirmDialog, DataTable, ErrorState, FilterBar, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { TALLY_EXPORT_STATUS_LABEL, type TallyExportStatus } from '@/modules/billing/labels';
import { fromPaise, toPaise } from '@/modules/billing/money';
import { dateParam } from '@/modules/billing/searchParams';
import { tallyApi, tallyQueries, type AccountingVoucher } from '@/modules/billing/tallyApi';
import { ActionError, Amount, NoAccess } from '@/modules/billing/ui';

interface TallySearch {
  view?: 'exports' | 'daybook';
  date?: string;
}

export const Route = createFileRoute('/_app/billing/tally')({
  validateSearch: (s: Record<string, unknown>): TallySearch => ({ view: s.view === 'daybook' ? 'daybook' : undefined, date: dateParam(s.date) }),
  component: TallyPage,
});

const useStyles = makeStyles({
  voucher: { display: 'flex', flexDirection: 'column', gap: '4px', padding: '10px 12px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  entries: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 130px 130px', gap: '2px 12px' },
  head: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  num: { textAlign: 'end' },
});

const STATUS_TONE: Record<TallyExportStatus, 'success' | 'warning' | 'danger' | 'informative'> = { Posted: 'success', Queued: 'warning', NotConfigured: 'informative', Failed: 'danger' };

/** Saves the Tally import file of a date (used when Tally cannot be reached from the server). */
async function download(date: string): Promise<void> {
  const blob = await tallyApi.exportFile(date);
  const extension = blob.type.includes('xml') ? 'xml' : blob.type.includes('json') ? 'json' : 'txt';
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = `Tally-day-book-${date}.${extension}`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

/** Tally day book (INT-TALLY): the posting log per day, re-posting, the import file, and a preview of the vouchers. */
function TallyPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canExport = usePermission('billing.tally.export');
  const view = search.view ?? 'exports';

  if (!canExport) {
    return (
      <Page>
        <PageHeader title="Tally export" />
        <NoAccess what="the Tally export" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader title="Tally export" subtitle="The day book of bills, receipts, refunds, credit notes and doctor fees, posted to Tally every night" icon={<BookRegular />} />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList selectedValue={view} onTabSelect={(_, d) => void navigate({ search: (prev) => ({ ...prev, view: d.value === 'daybook' ? 'daybook' : undefined }) })}>
            <Tab value="exports">Posting log</Tab>
            <Tab value="daybook">Day book preview</Tab>
          </TabList>
        </div>
        {view === 'exports' ? <ExportsView /> : <DayBookView date={search.date ?? todayIso(-1)} onDate={(date) => void navigate({ search: (prev) => ({ ...prev, date }) })} />}
      </Section>
    </Page>
  );
}

function ExportsView() {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [from, setFrom] = useState(todayIso(-30));
  const [to, setTo] = useState(todayIso());
  const [postDate, setPostDate] = useState(todayIso(-1));
  const [confirming, setConfirming] = useState<string | null>(null);
  const [downloading, setDownloading] = useState<string | null>(null);
  const exports = useQuery(tallyQueries.exports(from, to));

  const queue = useMutation({
    mutationFn: (date: string) => tallyApi.queue(date),
    onSuccess: (_, date) => {
      notify.success('Day book queued', `${formatDateOnly(date)} will be posted to Tally in a moment.`);
      setConfirming(null);
      void queryClient.invalidateQueries({ queryKey: ['billing', 'tally'] });
    },
  });

  const save = async (date: string) => {
    setDownloading(date);
    try {
      await download(date);
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
            <Input type="date" value={postDate} max={todayIso()} onChange={(_, d) => setPostDate(d.value)} />
          </Field>
          <Button appearance="primary" icon={<SendRegular />} disabled={!postDate} onClick={() => setConfirming(postDate)}>
            Post to Tally
          </Button>
          <Button icon={downloading === postDate ? <Spinner size="tiny" /> : <ArrowDownloadRegular />} disabled={!postDate || downloading !== null} onClick={() => void save(postDate)}>
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
        columns={[
          { key: 'date', header: 'Day', width: 130, render: (e) => <Text weight="semibold">{formatDateOnly(e.date)}</Text> },
          { key: 'status', header: 'Status', width: 170, render: (e) => <StatusBadge status={e.status} tone={STATUS_TONE[e.status]} label={TALLY_EXPORT_STATUS_LABEL[e.status]} /> },
          { key: 'vouchers', header: 'Vouchers', align: 'end', render: (e) => e.voucherCount },
          { key: 'result', header: 'Created / altered / errors', priority: 'low', render: (e) => `${e.createdCount} / ${e.alteredCount} / ${e.errorCount}` },
          { key: 'attempt', header: 'Last attempt', priority: 'low', render: (e) => (e.lastAttemptAt ? `${formatDateTime(e.lastAttemptAt)} (${e.attempts})` : '—') },
          { key: 'message', header: 'Message', priority: 'low', render: (e) => e.message ?? (e.postedAt ? `Posted ${formatDateTime(e.postedAt)}` : '—') },
        ]}
        rowActions={(e) => (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button size="small" appearance="subtle" icon={<MoreHorizontalRegular />} aria-label={`Actions for ${formatDateOnly(e.date)}`} />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem icon={<ArrowSyncRegular />} onClick={() => setConfirming(e.date)}>
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
      <ConfirmDialog
        open={confirming !== null}
        title={`Post the day book of ${confirming ? formatDateOnly(confirming) : ''}?`}
        confirmLabel="Post to Tally"
        busy={queue.isPending}
        onConfirm={() => confirming && queue.mutate(confirming)}
        onCancel={() => {
          setConfirming(null);
          queue.reset();
        }}
      >
        Vouchers already in Tally are updated, not duplicated. When no Tally connection is configured, the day book stays ready to download.
        <ActionError error={queue.error} />
      </ConfirmDialog>
    </>
  );
}

function sumEntries(voucher: AccountingVoucher, debit: boolean): number {
  return fromPaise(voucher.entries.filter((e) => e.isDebit === debit).reduce((total, e) => total + toPaise(e.amount), 0n));
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
              {all.length} voucher{all.length === 1 ? '' : 's'} · debit <Amount value={debit} /> · credit <Amount value={credit} />
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
                  {v.reference ? ` · ${v.reference}` : ''}
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
