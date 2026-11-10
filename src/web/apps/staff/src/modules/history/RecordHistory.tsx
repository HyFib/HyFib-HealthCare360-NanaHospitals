import {
  Body1Strong,
  Button,
  Caption1,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  OverlayDrawer,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular, HistoryRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { ErrorState, LoadingState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { historyQueries, type HistoryEntry, type HistoryRecordType } from './api';

const useStyles = makeStyles({
  list: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '16px' },
  entry: { display: 'flex', flexDirection: 'column', gap: '4px' },
  meta: { color: tokens.colorNeutralForeground3 },
  changes: { margin: 0, paddingInlineStart: '16px', display: 'flex', flexDirection: 'column', gap: '2px' },
  value: { overflowWrap: 'anywhere' },
  pager: { display: 'flex', gap: '8px', alignItems: 'center', justifyContent: 'space-between', marginTop: '16px' },
});

/**
 * The "History" button of a record's page: opens who changed the record and its parts (a report's results, a bill's
 * receipts), when, each field from what to what, and the approvals asked for on it, from the audit trail. The server
 * shows it only to staff who may read the record, by the rules that open the record.
 */
export function HistoryButton({ type, id }: { type: HistoryRecordType; id: string }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button icon={<HistoryRegular />} onClick={() => setOpen(true)}>
        History
      </Button>
      {open && <RecordHistoryDrawer type={type} id={id} onClose={() => setOpen(false)} />}
    </>
  );
}

export function RecordHistoryDrawer({
  type,
  id,
  onClose,
}: {
  type: HistoryRecordType;
  id: string;
  onClose: () => void;
}) {
  const styles = useStyles();
  const [page, setPage] = useState(1);
  const history = useQuery(historyQueries.record(type, id, page));
  const entries = history.data?.entries;
  const pages = entries ? Math.max(1, Math.ceil(entries.totalCount / entries.pageSize)) : 1;

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, data) => !data.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} onClick={onClose} />}
        >
          {history.data ? `History of ${history.data.title}` : 'History'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        {history.isPending ? (
          <LoadingState label="Loading the history" />
        ) : history.isError ? (
          <ErrorState error={history.error} onRetry={() => void history.refetch()} />
        ) : entries && entries.items.length === 0 ? (
          <Text>Nothing has been recorded about this record yet.</Text>
        ) : (
          <>
            <ol className={styles.list} aria-label="Changes, newest first">
              {entries?.items.map((entry) => (
                <Entry key={entry.id} entry={entry} />
              ))}
            </ol>
            {pages > 1 && (
              <div className={styles.pager}>
                <Button disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
                  Newer
                </Button>
                <Caption1>
                  Page {page} of {pages}
                </Caption1>
                <Button disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>
                  Older
                </Button>
              </div>
            )}
          </>
        )}
      </DrawerBody>
    </OverlayDrawer>
  );
}

function Entry({ entry }: { entry: HistoryEntry }) {
  const styles = useStyles();
  // An entry about a part of the record (a result, a receipt, an approval) says which.
  const heading = entry.part ? `${entry.part} — ${entry.what}` : entry.what;
  return (
    <li className={styles.entry}>
      <Body1Strong>{heading}</Body1Strong>
      <Caption1 className={styles.meta}>
        {formatDateTime(entry.at)} · {entry.by ?? 'The system'}
      </Caption1>
      {entry.changes.length > 0 && (
        <ul className={styles.changes} aria-label={`${heading}, by field`}>
          {entry.changes.map((change) => (
            <li key={change.field} className={styles.value}>
              <Text weight="semibold">{change.field}</Text>: {change.from ?? '—'} → {change.to ?? '—'}
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}
