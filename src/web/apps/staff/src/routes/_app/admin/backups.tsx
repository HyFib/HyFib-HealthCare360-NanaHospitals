import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DatabaseArrowUpRegular, KeyRegular } from '@fluentui/react-icons';
import { formatDateTime, humanize } from '@hc360/core';
import { DataTable, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '@/lib/api';
import { adminQueries, backupContents, backupKeyText, formatBytes } from '@/modules/admin/api';
import { RecoveryKeyDialog } from '@/modules/admin/RecoveryKeyDialog';

export const Route = createFileRoute('/_app/admin/backups')({
  component: BackupsPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Encrypted database backups of the server machine (SRS DED-16) and the offline recovery key that opens them. */
function BackupsPage() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const backups = useQuery(adminQueries.backups());
  const recoveryKey = useQuery(adminQueries.recoveryKey());
  const [keyOpen, setKeyOpen] = useState(false);
  const running = backups.data?.some((b) => b.status === 'Running') ?? false;

  const start = useMutation({
    mutationFn: () => api.post('/api/v1/admin/backups'),
    onSuccess: () => {
      notify.info('Backup started', 'It runs in the background. HealthCare360 stays available.');
      void queryClient.invalidateQueries({ queryKey: ['admin', 'backups'] });
    },
    onError: (error) => notify.error(error, 'Could not start the backup'),
  });

  return (
    <Page>
      <PageHeader
        title="Backups"
        subtitle="Encrypted copies of the database. Keep a copy on a second disk or NAS, and the recovery key offline."
        actions={
          <>
            <Button icon={<KeyRegular />} onClick={() => setKeyOpen(true)}>
              Recovery key
            </Button>
            <Button
              appearance="primary"
              icon={<DatabaseArrowUpRegular />}
              disabled={running || start.isPending}
              onClick={() => start.mutate()}
            >
              {running ? 'Backup running…' : 'Back up now'}
            </Button>
          </>
        }
      />
      <MessageBar intent="info" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>Restoring</MessageBarTitle>
          Each backup holds the database, the encryption key for identifiers and documents, and the stored documents. To
          restore, also on replacement hardware, run <Text font="monospace">Restore-HC360.ps1</Text> on the server with
          a backup file and the recovery key it opens with, which the list names. The deployment guide has the steps.
        </MessageBarBody>
      </MessageBar>
      <Section flush>
        <DataTable
          ariaLabel="Backups"
          rows={backups.data}
          loading={backups.isLoading}
          error={backups.error}
          onRetry={() => void backups.refetch()}
          getRowId={(b) => b.id}
          columns={[
            { key: 'started', header: 'Started', width: 170, render: (b) => formatDateTime(b.startedAt) },
            { key: 'kind', header: 'Type', width: 120, render: (b) => humanize(b.kind) },
            { key: 'status', header: 'Status', width: 120, render: (b) => <StatusBadge status={b.status} /> },
            { key: 'size', header: 'Size', width: 100, render: (b) => formatBytes(b.sizeBytes) },
            {
              key: 'contents',
              header: 'Contents',
              width: 250,
              render: (b) => (
                <span>
                  {backupContents(b)}
                  {b.status === 'Succeeded' && (
                    <Caption1 block className={styles.muted}>
                      {backupKeyText(b.recoveryKeyId, recoveryKey.data?.originalKeyId)}
                    </Caption1>
                  )}
                </span>
              ),
            },
            {
              key: 'checked',
              header: 'Checked',
              width: 170,
              priority: 'low',
              render: (b) => (b.verifiedAt ? formatDateTime(b.verifiedAt) : '—'),
            },
            {
              key: 'copy',
              header: 'Second copy',
              width: 120,
              priority: 'low',
              render: (b) => (b.status === 'Succeeded' ? (b.secondaryCopied ? 'Yes' : 'No') : '—'),
            },
            {
              key: 'file',
              header: 'File / error',
              width: 300,
              priority: 'low',
              render: (b) =>
                b.error ? (
                  <Caption1 style={{ color: tokens.colorPaletteRedForeground1 }}>{b.error}</Caption1>
                ) : (
                  <span>
                    <Text font="monospace">{b.fileName ?? '—'}</Text>
                    {b.sha256 && (
                      <Caption1 block className={styles.muted}>
                        SHA-256 {b.sha256.slice(0, 16)}…
                      </Caption1>
                    )}
                  </span>
                ),
            },
          ]}
          emptyTitle="No backups yet"
          emptyDescription="Run the first backup now, then check that it succeeds."
          size="small"
        />
      </Section>

      <RecoveryKeyDialog open={keyOpen} onClose={() => setKeyOpen(false)} />
    </Page>
  );
}
