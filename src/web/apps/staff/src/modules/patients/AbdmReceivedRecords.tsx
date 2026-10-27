import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import { formatDate, formatDateTime } from '@hc360/core';
import { DataTable } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { api } from '@/lib/api';
import {
  type AbdmReceivedRecord,
  type AbdmReceivedRecordView,
  type BundleAttachment,
  bundleSummary,
  receivedRecordQueries,
} from './abdmReceivedRecords';

const useStyles = makeStyles({
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
  muted: { color: tokens.colorNeutralForeground3 },
  list: { display: 'flex', flexDirection: 'column', gap: '8px', margin: 0, paddingInlineStart: 0, listStyle: 'none' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** The records other providers sent for the patient through ABDM, while the patient's consents last. */
export function AbdmReceivedRecords({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const records = useQuery(receivedRecordQueries.list(patientId));
  const [open, setOpen] = useState<AbdmReceivedRecord | null>(null);

  return (
    <>
      <DataTable
        ariaLabel="Records received through ABDM"
        rows={records.data}
        loading={records.isLoading}
        error={records.error}
        onRetry={() => void records.refetch()}
        getRowId={(r) => r.id}
        onRowClick={(r) => (r.error ? undefined : setOpen(r))}
        columns={[
          {
            key: 'record',
            header: 'Record',
            render: (r) => (
              <div className={styles.cell}>
                <Text>{r.title ?? 'Record that could not be read'}</Text>
                <Caption1 className={styles.muted}>
                  {r.hipName ?? 'Another provider'}
                  {r.recordDate ? ` · ${formatDate(r.recordDate)}` : ''}
                </Caption1>
                {r.error && <Caption1 className={styles.muted}>{r.error}</Caption1>}
              </div>
            ),
          },
          { key: 'received', header: 'Received', width: 160, render: (r) => formatDateTime(r.receivedAt) },
        ]}
        emptyTitle="No records have been received through ABDM"
        size="small"
      />
      {open && <ReceivedRecordDialog patientId={patientId} record={open} onClose={() => setOpen(null)} />}
    </>
  );
}

function ReceivedRecordDialog({
  patientId,
  record,
  onClose,
}: {
  patientId: string;
  record: AbdmReceivedRecord;
  onClose: () => void;
}) {
  const styles = useStyles();
  const view = useQuery({
    queryKey: ['abdm', 'received-records', patientId, record.id],
    queryFn: ({ signal }) =>
      api.get<AbdmReceivedRecordView>(`/api/v1/abdm/patients/${patientId}/received-records/${record.id}`, { signal }),
    // Each opening is audited; the content is not kept in the cache once the dialog closes.
    gcTime: 0,
  });
  const summary = useMemo(() => bundleSummary(view.data?.bundle ?? null), [view.data]);
  const [file, setFile] = useState<{ url: string; title: string } | null>(null);

  useEffect(
    () => () => {
      if (file) {
        URL.revokeObjectURL(file.url);
      }
    },
    [file],
  );

  const openFile = (a: BundleAttachment) => {
    const bytes = Uint8Array.from(atob(a.data), (c) => c.charCodeAt(0));
    setFile({ url: URL.createObjectURL(new Blob([bytes], { type: a.contentType })), title: a.title });
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{record.title ?? 'Record'}</DialogTitle>
          <DialogContent className={styles.body}>
            <Caption1 className={styles.muted}>
              From {record.hipName ?? 'another provider'} through ABDM
              {record.recordDate ? `, ${formatDate(record.recordDate)}` : ''}
              {view.data ? `. Kept until ${formatDate(view.data.eraseAt)}.` : ''}
            </Caption1>
            {!record.checksumMatches && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  The provider's checksum did not match this record. Its encryption proves it was not altered; check
                  with the provider if anything looks wrong.
                </MessageBarBody>
              </MessageBar>
            )}
            {view.isLoading && <Spinner size="small" label="Opening the record" />}
            {view.error && (
              <MessageBar intent="error">
                <MessageBarBody>The record could not be opened.</MessageBarBody>
              </MessageBar>
            )}
            {view.data && summary.items.length === 0 && summary.attachments.length === 0 && (
              <Text>The record has no clinical entries HC360 can show.</Text>
            )}
            {summary.items.length > 0 && (
              <ul className={styles.list} aria-label="Entries in the record">
                {summary.items.map((item, i) => (
                  <li key={i}>
                    <Subtitle2>{item.label}</Subtitle2>
                    <Caption1 className={styles.muted}>
                      {' '}
                      {item.type}
                      {item.detail ? ` · ${item.detail}` : ''}
                    </Caption1>
                  </li>
                ))}
              </ul>
            )}
            {summary.attachments.map((a, i) => (
              <div key={i}>
                <Button onClick={() => openFile(a)}>Open {a.title}</Button>
              </div>
            ))}
            {file && (
              <a href={file.url} download={file.title} target="_blank" rel="noreferrer">
                Download {file.title}
              </a>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
