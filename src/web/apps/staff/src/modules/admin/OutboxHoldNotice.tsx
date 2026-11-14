import {
  Button,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
} from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { ConfirmDialog, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { adminQueries } from './api';
import { heldCount, heldSince, releaseOutbox, releasedText, setByRestore, type OutboxHold } from './outboxHold';

const useStyles = makeStyles({
  notice: { marginBottom: '12px' },
});

/** The notice's title: how many messages are held, queued before when. */
function title(hold: OutboxHold, held: number): string {
  const since = heldSince(hold);
  if (!since) {
    return 'Outgoing messages are held';
  }
  const before = setByRestore(hold)
    ? `queued before the restore at ${formatDateTime(since)}`
    : `queued before ${formatDateTime(since)}`;
  return `${heldCount(held)} ${before} ${held === 1 ? 'is' : 'are'} held`;
}

/**
 * Outgoing messages held after a restore, on System health: how many, where to check them, and, for those who manage
 * the server (`admin.system.manage`), the release. A restored database holds again what was waiting when the backup was
 * made, and some of it may have gone out since, so those messages do not go until someone has checked and released them.
 * Messages queued since go as usual, and so do alerts in the app.
 */
export function OutboxHoldNotice({ hold, held }: { hold: OutboxHold; held: number }) {
  const styles = useStyles();
  const canRelease = usePermission('admin.system.manage');
  const canReadLog = usePermission('admin.notifications.manage');
  const [confirming, setConfirming] = useState(false);
  const notify = useNotify();
  const queryClient = useQueryClient();
  const refresh = () => void queryClient.invalidateQueries({ queryKey: adminQueries.health().queryKey });
  const release = useMutation({
    mutationFn: releaseOutbox,
    onSuccess: (released) => {
      setConfirming(false);
      notify.success('Outgoing messages released', releasedText(released));
      refresh();
    },
    onError: (error) => {
      setConfirming(false);
      notify.error(error, 'The messages were not released');
      refresh();
    },
  });

  const known = heldSince(hold) !== null;
  return (
    <>
      <MessageBar
        intent={setByRestore(hold) && known ? 'warning' : 'error'}
        layout="multiline"
        className={styles.notice}
      >
        <MessageBarBody>
          <MessageBarTitle>{title(hold, held)}</MessageBarTitle>
          {known
            ? 'They do not go to the internet (SMS, WhatsApp, e-mail) until they are released; messages queued since go as usual.'
            : 'The hold cannot be read, so no message goes to the internet (SMS, WhatsApp, e-mail) until it is released.'}
          {hold.reason && !setByRestore(hold) ? ` ${hold.reason}.` : ''}
          {hold.backupTakenAt
            ? ` The backup was made ${formatDateTime(hold.backupTakenAt)}: messages waiting then may have gone out since.`
            : ''}{' '}
          {canReadLog ? (
            <Link to="/admin/notification-logs" search={{ status: 'Queued' }}>
              Check the queued messages
            </Link>
          ) : (
            'Check the queued messages in the notification log.'
          )}
        </MessageBarBody>
        {canRelease && (
          <MessageBarActions>
            <Button onClick={() => setConfirming(true)}>Release</Button>
          </MessageBarActions>
        )}
      </MessageBar>
      <ConfirmDialog
        open={confirming}
        title="Release the held messages?"
        confirmLabel="Release"
        busy={release.isPending}
        onConfirm={() => release.mutate()}
        onCancel={() => setConfirming(false)}
      >
        Every held message goes as soon as its provider answers, including any that went out after the backup was made.
        Release them once you have checked the queued messages. The release is recorded in the audit trail.
      </ConfirmDialog>
    </>
  );
}
