import {
  Button,
  Caption1,
  CounterBadge,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  mergeClasses,
  OverlayDrawer,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AlertRegular, CheckmarkRegular, DismissRegular } from '@fluentui/react-icons';
import { bridge, formatRelative, useRealtimeEvent, useSession, type PagedResult } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '@/lib/api';

export interface UserNotification {
  id: string;
  title: string;
  body: string | null;
  link: string | null;
  category: string;
  severity: string;
  createdAt: string;
  readAt: string | null;
}

const useStyles = makeStyles({
  item: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: 'none',
    background: 'transparent',
    textAlign: 'start',
    cursor: 'pointer',
    width: '100%',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  unread: { borderLeft: `3px solid ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorNeutralBackground2 },
  critical: { borderLeft: `3px solid ${tokens.colorPaletteRedBorder2}` },
  muted: { color: tokens.colorNeutralForeground3 },
  list: { display: 'flex', flexDirection: 'column', gap: '4px' },
  anchor: { position: 'relative', display: 'inline-flex' },
  badge: { position: 'absolute', top: '2px', right: '2px', pointerEvents: 'none' },
});

/** Bell with unread count; opens the user's notifications (lab critical values, approvals, alerts…). */
export function NotificationsButton() {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const userId = useSession((s) => s.user?.id);
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const unread = useQuery({
    queryKey: ['notifications', 'unread-count'],
    queryFn: () => api.get<{ count: number }>('/api/v1/notifications/unread-count'),
    refetchInterval: 120_000,
  });

  const list = useQuery({
    queryKey: ['notifications', 'list'],
    queryFn: () => api.get<PagedResult<UserNotification>>('/api/v1/notifications', { query: { pageSize: 30 } }),
    enabled: open,
  });

  useRealtimeEvent<UserNotification>(userId ? `user:${userId}` : null, 'notification.new', (notification) => {
    void queryClient.invalidateQueries({ queryKey: ['notifications'] });
    if (bridge.isDesktop && document.visibilityState !== 'visible') {
      void bridge.notify(notification.title, notification.body ?? '').catch(() => undefined);
    }
  });

  const markRead = useMutation({
    mutationFn: (id: string) => api.post(`/api/v1/notifications/${id}/read`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['notifications'] }),
  });

  const markAll = useMutation({
    mutationFn: () => api.post('/api/v1/notifications/read-all'),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['notifications'] }),
  });

  const count = unread.data?.count ?? 0;

  const openItem = (item: UserNotification) => {
    if (!item.readAt) {
      markRead.mutate(item.id);
    }
    if (item.link?.startsWith('/')) {
      setOpen(false);
      void navigate({ href: item.link });
    }
  };

  return (
    <>
      <span className={styles.anchor}>
        <Button
          appearance="subtle"
          icon={<AlertRegular />}
          aria-label={count > 0 ? `Notifications, ${count} unread` : 'Notifications'}
          onClick={() => setOpen(true)}
        />
        {count > 0 && (
          <CounterBadge className={styles.badge} count={count} size="small" color="danger" overflowCount={99} />
        )}
      </span>
      <OverlayDrawer open={open} onOpenChange={(_, data) => setOpen(data.open)} position="end" size="small">
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <>
                <Button
                  appearance="subtle"
                  icon={<CheckmarkRegular />}
                  onClick={() => markAll.mutate()}
                  disabled={count === 0}
                >
                  Mark all read
                </Button>
                <Button
                  appearance="subtle"
                  icon={<DismissRegular />}
                  aria-label="Close"
                  onClick={() => setOpen(false)}
                />
              </>
            }
          >
            Notifications
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          {list.isLoading && <LoadingState />}
          {list.error && <ErrorState error={list.error} onRetry={() => void list.refetch()} />}
          {list.data && list.data.items.length === 0 && (
            <EmptyState title="You're all caught up" description="New alerts and updates will appear here." />
          )}
          <div className={styles.list}>
            {list.data?.items.map((item) => (
              <button
                key={item.id}
                type="button"
                className={mergeClasses(
                  styles.item,
                  !item.readAt && styles.unread,
                  item.severity === 'Critical' && styles.critical,
                )}
                onClick={() => openItem(item)}
              >
                <Text weight={item.readAt ? 'regular' : 'semibold'}>{item.title}</Text>
                {item.body && <Caption1>{item.body}</Caption1>}
                <Caption1 className={styles.muted}>{formatRelative(item.createdAt)}</Caption1>
              </button>
            ))}
          </div>
        </DrawerBody>
      </OverlayDrawer>
    </>
  );
}
