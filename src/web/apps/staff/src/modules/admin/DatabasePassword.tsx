import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { KeyResetRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { ConfirmDialog, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { adminQueries, changeDatabasePassword, type DatabasePasswordStatus } from './api';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '6px', marginTop: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** When the password was last changed, in words. */
function lastChangedText(status: DatabasePasswordStatus): string {
  if (!status.lastChangedAt) {
    return 'Not changed since installation';
  }
  return `Changed ${formatDateTime(status.lastChangedAt)}${status.lastChangedBy ? ` by ${status.lastChangedBy}` : ''}`;
}

/**
 * The database password of System health (SRS SEC-07): when it was last changed, and, for holders of
 * `admin.system.manage`, a new random password that nobody sees, set on the database and in the protected settings file
 * at once. When the server cannot change it (no Windows, or the connection is set elsewhere), it says why.
 */
export function DatabasePassword() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('admin.system.manage');
  const status = useQuery(adminQueries.databasePassword());
  const [confirming, setConfirming] = useState(false);

  const change = useMutation({
    mutationFn: changeDatabasePassword,
    onSuccess: () => {
      setConfirming(false);
      notify.success('Database password changed', 'HC360 signs in to the database with the new password from now on.');
      void queryClient.invalidateQueries({ queryKey: adminQueries.databasePassword().queryKey });
    },
    onError: (error) => {
      setConfirming(false);
      notify.error(error, 'The database password was not changed');
    },
  });

  if (!status.data) {
    return null;
  }

  const s = status.data;
  return (
    <div className={styles.root}>
      <Text>
        Password of <Text weight="semibold">{s.username}</Text>: {lastChangedText(s)}
      </Text>
      {s.canChange ? (
        canManage && (
          <Button icon={<KeyResetRegular />} onClick={() => setConfirming(true)} disabled={change.isPending}>
            Change password
          </Button>
        )
      ) : (
        <Caption1 className={styles.muted}>{s.reason}</Caption1>
      )}
      <ConfirmDialog
        open={confirming}
        title="Change the database password?"
        confirmLabel="Change password"
        busy={change.isPending}
        onConfirm={() => change.mutate()}
        onCancel={() => setConfirming(false)}
      >
        <Text block>
          HC360 gives its database account, {s.username}, a new random password, keeps it protected in the settings file
          on this server, and uses it at once. Nobody sees the new password, and screens keep working.
        </Text>
        <Text block className={styles.muted}>
          Change it when someone who knew the old one leaves, and at least once a year.
        </Text>
      </ConfirmDialog>
    </div>
  );
}
