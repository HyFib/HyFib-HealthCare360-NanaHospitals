import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  ProgressBar,
  SearchBox,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DocumentArrowLeftRegular, PrintRegular, SaveRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, formatRelative, isApiError, useDraft, useHotkey } from '@hc360/core';
import {
  ConfirmDialog,
  ErrorState,
  LEAVE_KEEPING_DRAFT,
  LoadingState,
  Page,
  PageHeader,
  Section,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import {
  inventoryApi,
  inventoryKeys,
  inventoryQueries,
  type CountDetail,
  type CountLineDto,
} from '@/modules/inventory/api';
import { formatQty, parseNumber } from '@/modules/inventory/format';
import { itemTypeLabel } from '@/modules/inventory/labels';
import { LineTable } from '@/modules/inventory/LineTable';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { printCountSheet } from '@/modules/inventory/print';
import { Detail, DetailGrid, DocStatus, ExpiryText } from '@/modules/inventory/StockBits';
import { countVariance, summarizeCount, varianceValue } from '@/modules/inventory/stockMath';

export const Route = createFileRoute('/_app/inventory/counts/$countId')({
  component: CountSheetPage,
});

const useStyles = makeStyles({
  summary: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))',
    gap: '12px 24px',
    alignItems: 'end',
  },
  short: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  more: { color: tokens.colorPaletteGreenForeground1, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  toolbar: { display: 'flex', gap: '12px', alignItems: 'end', flexWrap: 'wrap', padding: '0 16px 12px' },
});

function CountSheetPage() {
  const { countId } = Route.useParams();
  const count = useQuery(inventoryQueries.count(countId));
  if (count.isLoading) {
    return <LoadingState />;
  }
  if (!count.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={count.error} onRetry={() => void count.refetch()} />
      </Page>
    );
  }
  return <CountSheet key={count.data.id} count={count.data} />;
}

interface Entry {
  counted: string;
  remarks: string;
}

function serverEntry(line: CountLineDto): Entry {
  return { counted: line.countedQuantity === null ? '' : String(line.countedQuantity), remarks: line.remarks ?? '' };
}

interface SavedCount {
  lineId: string;
  countedQuantity: number;
  remarks: string | null;
}

/**
 * Whether the sheet read back after this user's save holds exactly the counts shown, with the ones that save wrote
 * (blank remarks leave a line's remarks as they were). If it does not, someone else saved counts in between.
 */
function holdsCounts(fresh: CountDetail, shown: CountLineDto[], saved: SavedCount[]): boolean {
  const written = new Map(saved.map((s) => [s.lineId, s]));
  return (
    fresh.lines.length === shown.length &&
    shown.every((line) => {
      const now = fresh.lines.find((l) => l.id === line.id);
      const own = written.get(line.id);
      return (
        now !== undefined &&
        now.countedQuantity === (own ? own.countedQuantity : line.countedQuantity) &&
        now.remarks === (own?.remarks ?? line.remarks)
      );
    })
  );
}

/** The count sheet changed after this user's own counts were saved on it: nothing typed is left to keep. */
class ChangedAfterSave extends Error {}

/** Count sheet: type what is on the shelf; differences are worked out as you go. Ctrl+S saves. */
function CountSheet({ count }: { count: CountDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const canCount = usePermission(S.counts);
  const editable = canCount && count.status === 'InProgress';
  // Only lines typed on this computer are kept locally; everything else always shows the server's latest counts.
  const [edits, setEdits] = useState<Record<string, Entry>>({});
  // The sheet as it was when the first unsaved count was typed: the save is refused if counts were saved on it since
  // (REL-04).
  const [baseVersion, setBaseVersion] = useState<number | null>(null);
  const [filter, setFilter] = useState('');
  const [onlyOpen, setOnlyOpen] = useState(false);
  const [confirm, setConfirm] = useState<'submit' | 'cancel' | null>(null);
  const entryOf = (line: CountLineDto): Entry => edits[line.id] ?? serverEntry(line);
  const changed = count.lines.filter((l) => {
    const now = edits[l.id];
    const before = serverEntry(l);
    return (
      now !== undefined &&
      (now.counted.trim() !== before.counted.trim() || now.remarks.trim() !== before.remarks.trim())
    );
  });
  const dirty = changed.length > 0;
  const draft = useDraft<Record<string, Entry>>(editable ? `stock-count:${count.id}` : null, edits, {
    enabled: editable && dirty,
  });
  const [savedDraft, setSavedDraft] = useState(() => draft.restore());

  const parsed = count.lines.map((l) => ({ line: l, counted: parseNumber(entryOf(l).counted) }));
  const invalid = parsed.filter((p) => p.counted !== null && p.counted < 0);
  const summary = summarizeCount(
    parsed.map((p) => ({
      lineId: p.line.id,
      systemQuantity: p.line.systemQuantity,
      unitCost: p.line.unitCost,
      counted: p.counted,
    })),
  );
  const term = filter.trim().toLowerCase();
  const visible = parsed.filter(
    (p) =>
      (!term || p.line.itemName.toLowerCase().includes(term) || p.line.batchNumber.toLowerCase().includes(term)) &&
      (!onlyOpen || p.counted === null),
  );

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.counts });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.adjustments });
  };

  const saveLines = async (): Promise<SavedCount[]> => {
    const lines = changed
      .map((l) => ({ line: l, counted: parseNumber(entryOf(l).counted) }))
      .filter((c): c is { line: CountLineDto; counted: number } => c.counted !== null)
      .map((c) => ({ lineId: c.line.id, countedQuantity: c.counted, remarks: entryOf(c.line).remarks.trim() || null }));
    if (lines.length > 0) {
      await inventoryApi.recordCounts(count.id, baseVersion ?? count.version, lines);
    }
    return lines;
  };

  /**
   * Someone else saved counts on the sheet meanwhile: it is shown as it is now, and counts typed here that were not
   * saved wait as a draft.
   */
  const conflicted = (error: unknown): boolean => {
    const saved = error instanceof ChangedAfterSave;
    if (!saved && !(isApiError(error) && error.code === 'concurrency')) {
      return false;
    }
    const kept = !saved && changed.length > 0;
    if (kept) {
      setSavedDraft({ value: edits, savedAt: Date.now() });
    }
    setEdits({});
    setBaseVersion(null);
    notify.warning(
      'The count sheet changed meanwhile',
      kept
        ? 'It has been reloaded with the counts saved since. Your counts are kept: restore them to save them on it.'
        : 'It has been reloaded with the counts saved since. Check them, then submit the count again.',
    );
    refresh();
    return true;
  };

  const save = useMutation({
    mutationFn: saveLines,
    onSuccess: ({ length: n }) => {
      draft.clear();
      setEdits({});
      setBaseVersion(null);
      notify.success(n > 0 ? `${n} count${n === 1 ? '' : 's'} saved` : 'Nothing new to save');
      refresh();
    },
    onError: (error) => {
      if (!conflicted(error)) {
        notify.error(error, 'The counts were not saved');
      }
    },
  });

  const submit = useMutation({
    mutationFn: async () => {
      const lines = await saveLines();
      if (lines.length === 0) {
        // Submitted on the sheet as shown: counts saved on it since are refused (REL-04).
        return inventoryApi.submitCount(count.id, count.version);
      }
      // The counts typed here are on the sheet now.
      draft.clear();
      setEdits({});
      setBaseVersion(null);
      try {
        // Their save does not return the version it gave the sheet, so the sheet is read again. That is the version to
        // submit on only while it holds exactly what was shown with these counts: otherwise someone else saved counts
        // in between, and the count is not submitted until the user has seen them.
        const fresh = await queryClient.fetchQuery({ ...inventoryQueries.count(count.id), staleTime: 0 });
        if (!holdsCounts(fresh, count.lines, lines)) {
          throw new ChangedAfterSave();
        }
        return await inventoryApi.submitCount(count.id, fresh.version);
      } catch (error) {
        throw isApiError(error) && error.code === 'concurrency' ? new ChangedAfterSave() : error;
      }
    },
    onSuccess: (result) => {
      draft.clear();
      setEdits({});
      setBaseVersion(null);
      setConfirm(null);
      refresh();
      if (result.adjustmentId) {
        notify.success('Count submitted', 'The differences are waiting for approval as one adjustment.');
        void navigate({ to: '/inventory/adjustments', search: { id: result.adjustmentId } });
      } else {
        notify.success('Count submitted', 'Every counted batch matched the books. Nothing to adjust.');
      }
    },
    onError: (error) => {
      setConfirm(null);
      if (!conflicted(error)) {
        refresh();
        notify.error(error, 'The count was not submitted');
      }
    },
  });

  const cancel = useMutation({
    mutationFn: () => inventoryApi.cancelCount(count.id),
    onSuccess: () => {
      draft.clear();
      setConfirm(null);
      refresh();
      notify.success('Count cancelled');
    },
    onError: (error) => {
      setConfirm(null);
      notify.error(error, 'Could not cancel the count');
    },
  });

  useHotkey('mod+s', () => editable && dirty && invalid.length === 0 && !save.isPending && save.mutate(), {
    allowInInputs: true,
  });

  const setEntry = (line: CountLineDto, patch: Partial<Entry>) => {
    setBaseVersion((base) => base ?? count.version);
    setEdits((prev) => ({ ...prev, [line.id]: { ...(prev[line.id] ?? serverEntry(line)), ...patch } }));
  };

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({
    dirty: editable && dirty,
    busy: save.isPending || submit.isPending || cancel.isPending,
    leaveQuestion: LEAVE_KEEPING_DRAFT,
  });

  return (
    <Page>
      <PageHeader
        title={`Stock count ${count.number}`}
        subtitle={`${count.storeName}${count.itemTypeFilter ? ` · ${itemTypeLabel[count.itemTypeFilter]} only` : ''} · started ${formatDateTime(count.startedAt)}`}
        breadcrumbs={[
          { label: 'Stock counts', onClick: () => void navigate({ to: '/inventory/counts' }) },
          { label: count.number },
        ]}
        actions={
          <>
            <Button
              icon={<PrintRegular />}
              onClick={() =>
                void printCountSheet(count, letterhead).catch((error: unknown) =>
                  notify.error(error, 'Printing failed'),
                )
              }
            >
              Print blank sheet
            </Button>
            {editable && (
              <>
                <Button onClick={() => setConfirm('cancel')}>Cancel count</Button>
                <Button
                  icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
                  disabled={!dirty || invalid.length > 0 || save.isPending}
                  onClick={() => save.mutate()}
                >
                  Save (Ctrl+S)
                </Button>
                <Button
                  appearance="primary"
                  icon={<SendRegular />}
                  disabled={summary.counted === 0 || invalid.length > 0 || submit.isPending}
                  onClick={() => setConfirm('submit')}
                >
                  Submit count
                </Button>
              </>
            )}
            {count.adjustmentId && (
              <Button
                appearance="primary"
                onClick={() => void navigate({ to: '/inventory/adjustments', search: { id: count.adjustmentId! } })}
              >
                Open the adjustment
              </Button>
            )}
          </>
        }
      />

      {savedDraft && editable && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>
            Unsaved counts from {formatRelative(new Date(savedDraft.savedAt).toISOString())} were found on this
            computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button
              onClick={() => {
                // Put back on the sheet as shown now: that is what they are saved on.
                setBaseVersion((base) => base ?? count.version);
                setEdits({ ...savedDraft.value, ...edits });
                setSavedDraft(null);
              }}
            >
              Restore them
            </Button>
            <Button
              appearance="subtle"
              onClick={() => {
                draft.clear();
                setSavedDraft(null);
              }}
            >
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <Section>
        <div className={styles.summary}>
          <Detail label="Status">
            <DocStatus status={count.status} label={count.status === 'InProgress' ? 'Counting' : undefined} />
          </Detail>
          <div>
            <Caption1 className={styles.muted}>
              Counted {summary.counted} of {summary.total} batches
            </Caption1>
            <ProgressBar
              value={summary.total ? summary.counted / summary.total : 0}
              thickness="large"
              aria-label="Count progress"
            />
          </div>
          <Detail label="Batches with a difference">{summary.withDifference}</Detail>
          <Detail label="Short (at cost)">
            <span className={summary.shortValue > 0 ? styles.short : undefined}>{formatInr(summary.shortValue)}</span>
          </Detail>
          <Detail label="Net difference (at cost)">
            <span className={summary.netValue < 0 ? styles.short : summary.netValue > 0 ? styles.more : undefined}>
              {formatInr(summary.netValue)}
            </span>
          </Detail>
        </div>
        {count.remarks && (
          <DetailGrid>
            <Detail label="Remarks">{count.remarks}</Detail>
          </DetailGrid>
        )}
      </Section>

      <Section flush>
        <div className={styles.toolbar}>
          <Field label="Find" style={{ flex: '1 1 260px' }}>
            <SearchBox value={filter} onChange={(_, d) => setFilter(d.value)} placeholder="Item or batch" />
          </Field>
          <Switch checked={onlyOpen} onChange={(_, d) => setOnlyOpen(d.checked)} label="Only batches not counted yet" />
          {dirty && (
            <Caption1>
              {changed.length} unsaved change{changed.length === 1 ? '' : 's'}
            </Caption1>
          )}
        </div>
        <LineTable
          ariaLabel="Count sheet"
          rows={visible}
          getRowId={(p) => p.line.id}
          isFlagged={(p) => p.counted !== null && p.counted < 0}
          columns={[
            { key: 'item', header: 'Item', render: (p) => <Text weight="semibold">{p.line.itemName}</Text> },
            { key: 'batch', header: 'Batch', render: (p) => <Text font="monospace">{p.line.batchNumber}</Text> },
            { key: 'exp', header: 'Expiry', render: (p) => <ExpiryText date={p.line.expiryDate} /> },
            { key: 'book', header: 'Book qty', align: 'end', render: (p) => formatQty(p.line.systemQuantity) },
            {
              key: 'counted',
              header: 'Counted',
              width: 130,
              render: (p) =>
                editable ? (
                  <Input
                    type="number"
                    min={0}
                    aria-label={`Counted quantity of ${p.line.itemName} batch ${p.line.batchNumber}`}
                    value={entryOf(p.line).counted}
                    onChange={(_, d) => setEntry(p.line, { counted: d.value })}
                  />
                ) : p.line.countedQuantity === null ? (
                  '—'
                ) : (
                  formatQty(p.line.countedQuantity)
                ),
            },
            {
              key: 'diff',
              header: 'Difference',
              align: 'end',
              render: (p) => {
                if (p.counted === null) {
                  return <Caption1 className={styles.muted}>—</Caption1>;
                }
                const variance = countVariance(p.line.systemQuantity, p.counted);
                return (
                  <span className={variance < 0 ? styles.short : variance > 0 ? styles.more : undefined}>
                    {variance > 0 ? '+' : ''}
                    {formatQty(variance)}
                    {variance !== 0 && <Caption1 block>{formatInr(varianceValue(variance, p.line.unitCost))}</Caption1>}
                  </span>
                );
              },
            },
            {
              key: 'remarks',
              header: 'Remarks',
              priority: 'low',
              render: (p) =>
                editable ? (
                  <Input
                    value={entryOf(p.line).remarks}
                    maxLength={300}
                    aria-label={`Remarks for ${p.line.itemName}`}
                    onChange={(_, d) => setEntry(p.line, { remarks: d.value })}
                    placeholder="e.g. damaged pack"
                  />
                ) : (
                  (p.line.remarks ?? '—')
                ),
            },
          ]}
        />
      </Section>

      <ConfirmDialog
        open={confirm === 'submit'}
        title="Submit this count?"
        confirmLabel="Submit"
        busy={submit.isPending}
        onConfirm={() => submit.mutate()}
        onCancel={() => setConfirm(null)}
      >
        {summary.counted < summary.total
          ? `${summary.total - summary.counted} batches are not counted and will be left out. `
          : ''}
        The counts are compared with the books now. Differences become one adjustment that another person must approve;
        stock does not change until then.
      </ConfirmDialog>
      <ConfirmDialog
        open={confirm === 'cancel'}
        title="Cancel this count?"
        confirmLabel="Cancel count"
        destructive
        busy={cancel.isPending}
        onConfirm={() => cancel.mutate()}
        onCancel={() => setConfirm(null)}
      >
        The counts typed so far are discarded and nothing is adjusted.
      </ConfirmDialog>
    </Page>
  );
}
