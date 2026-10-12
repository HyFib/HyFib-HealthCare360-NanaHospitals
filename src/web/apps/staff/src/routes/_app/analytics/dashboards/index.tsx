import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Spinner,
  Subtitle2,
  Switch,
  Text,
  Textarea,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  BoardRegular,
  CopyRegular,
  DeleteRegular,
  EditRegular,
  MoreHorizontalRegular,
  OpenRegular,
} from '@fluentui/react-icons';
import { formatRelative } from '@hc360/core';
import { ConfirmDialog, EmptyState, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { analyticsApi, analyticsKeys, analyticsQueries, type DashboardListItem } from '@/modules/analytics/api';
import { ANALYTICS_PERMISSIONS as A } from '@/modules/analytics/labels';

export const Route = createFileRoute('/_app/analytics/dashboards/')({
  component: DashboardsPage,
});

const useStyles = makeStyles({
  sections: { display: 'flex', flexDirection: 'column', gap: '16px' },
  grid: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))' },
  card: {
    position: 'relative',
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '14px 14px 12px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    minWidth: 0,
    ':hover': { border: `1px solid ${tokens.colorBrandStroke2}`, boxShadow: tokens.shadow4 },
  },
  head: { display: 'flex', alignItems: 'flex-start', gap: '10px' },
  icon: { color: tokens.colorBrandForeground1, fontSize: '24px', display: 'flex', flexShrink: 0, paddingTop: '2px' },
  titleLink: {
    flex: 1,
    minWidth: 0,
    color: tokens.colorNeutralForeground1,
    textDecoration: 'none',
    '::after': {
      content: '""',
      position: 'absolute',
      top: 0,
      right: 0,
      bottom: 0,
      left: 0,
      borderRadius: tokens.borderRadiusLarge,
    },
    ':focus-visible': { outline: 'none' },
    ':focus-visible::after': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  menu: { position: 'relative', zIndex: 1 },
  description: {
    color: tokens.colorNeutralForeground2,
    display: '-webkit-box',
    WebkitLineClamp: 2,
    WebkitBoxOrient: 'vertical',
    overflow: 'hidden',
    minHeight: '2.6em',
  },
  meta: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap', color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** BI-02: saved dashboards — hospital templates, the user's own and those colleagues shared. */
function DashboardsPage() {
  const styles = useStyles();
  // Closing a dialog opened from a dashboard's menu returns focus to that menu's button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission(A.dashboardsManage);
  const dashboards = useQuery(analyticsQueries.dashboards());
  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<DashboardListItem | null>(null);

  const copy = useMutation({
    mutationFn: (board: DashboardListItem) => analyticsApi.copyDashboard(board.id),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.dashboards });
      notify.success('Copy created', 'Change it as you like; the original stays as it was.');
      void navigate({
        to: '/analytics/dashboards/$dashboardId',
        params: { dashboardId: created.id },
        search: { edit: true },
      });
    },
    onError: (error) => notify.error(error, 'Could not copy the dashboard'),
  });

  const remove = useMutation({
    mutationFn: (board: DashboardListItem) => analyticsApi.deleteDashboard(board.id, board.version),
    onSuccess: () => {
      setDeleting(null);
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.dashboards });
      notify.success('Dashboard deleted');
    },
    onError: (error) => {
      setDeleting(null);
      notify.error(error, 'Could not delete the dashboard');
    },
  });

  if (dashboards.isLoading) {
    return <LoadingState label="Loading dashboards…" />;
  }
  if (dashboards.error || !dashboards.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState
          error={dashboards.error}
          onRetry={() => void dashboards.refetch()}
          title="Could not load dashboards"
        />
      </Page>
    );
  }

  const all = dashboards.data;
  const groups = [
    {
      title: 'Hospital templates',
      description: 'Ready-made views for common reviews. Make a copy to adapt one.',
      items: all.filter((d) => d.isTemplate),
    },
    {
      title: 'My dashboards',
      description: 'Only you can change these; share them to let colleagues view them.',
      items: all.filter((d) => d.isMine && !d.isTemplate),
    },
    {
      title: 'Shared with me',
      description: 'Dashboards colleagues have shared.',
      items: all.filter((d) => !d.isMine && !d.isTemplate),
    },
  ].filter((group) => group.items.length > 0);

  return (
    <Page>
      <PageHeader
        title="Dashboards"
        icon={<BoardRegular />}
        subtitle="Your favourite reports side by side, always showing current figures"
        breadcrumbs={[
          { label: 'Analytics', onClick: () => void navigate({ to: '/analytics' }) },
          { label: 'Dashboards' },
        ]}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
              New dashboard
            </Button>
          )
        }
      />

      {groups.length === 0 ? (
        <Section>
          <EmptyState
            title="No dashboards yet"
            description={
              canManage
                ? 'Create one and add tiles from any report you can open.'
                : 'Ask your MIS team to share a dashboard with you.'
            }
            action={
              canManage ? (
                <Button icon={<AddRegular />} onClick={() => setCreating(true)}>
                  Create a dashboard
                </Button>
              ) : undefined
            }
          />
        </Section>
      ) : (
        <div className={styles.sections}>
          {groups.map((group) => (
            <Section key={group.title} title={group.title} description={group.description}>
              <div className={styles.grid}>
                {group.items.map((board) => (
                  <article key={board.id} className={styles.card}>
                    <div className={styles.head}>
                      <span className={styles.icon} aria-hidden>
                        <BoardRegular />
                      </span>
                      <Link
                        to="/analytics/dashboards/$dashboardId"
                        params={{ dashboardId: board.id }}
                        className={styles.titleLink}
                      >
                        <Subtitle2>{board.name}</Subtitle2>
                      </Link>
                      <div className={styles.menu}>
                        <Menu>
                          <MenuTrigger disableButtonEnhancement>
                            <Button
                              size="small"
                              appearance="subtle"
                              icon={<MoreHorizontalRegular />}
                              aria-label={`Actions for ${board.name}`}
                              {...restoreFocusTarget}
                            />
                          </MenuTrigger>
                          <MenuPopover>
                            <MenuList>
                              <MenuItem
                                icon={<OpenRegular />}
                                onClick={() =>
                                  void navigate({
                                    to: '/analytics/dashboards/$dashboardId',
                                    params: { dashboardId: board.id },
                                  })
                                }
                              >
                                Open
                              </MenuItem>
                              {board.canEdit && (
                                <MenuItem
                                  icon={<EditRegular />}
                                  onClick={() =>
                                    void navigate({
                                      to: '/analytics/dashboards/$dashboardId',
                                      params: { dashboardId: board.id },
                                      search: { edit: true },
                                    })
                                  }
                                >
                                  Change
                                </MenuItem>
                              )}
                              {canManage && (
                                <MenuItem
                                  icon={<CopyRegular />}
                                  disabled={copy.isPending}
                                  onClick={() => copy.mutate(board)}
                                >
                                  Make a copy
                                </MenuItem>
                              )}
                              {board.canEdit && (
                                <MenuItem icon={<DeleteRegular />} onClick={() => setDeleting(board)}>
                                  Delete
                                </MenuItem>
                              )}
                            </MenuList>
                          </MenuPopover>
                        </Menu>
                      </div>
                    </div>
                    <Text size={200} className={styles.description}>
                      {board.description || 'No description'}
                    </Text>
                    <Caption1 className={styles.meta}>
                      {board.isTemplate ? (
                        <Badge appearance="tint" color="brand" size="small">
                          Template
                        </Badge>
                      ) : board.isShared ? (
                        <Badge appearance="tint" color="informative" size="small">
                          Shared
                        </Badge>
                      ) : (
                        <Badge appearance="tint" color="subtle" size="small">
                          Only me
                        </Badge>
                      )}
                      {board.widgetCount === 1 ? '1 tile' : `${board.widgetCount} tiles`}
                      {!board.isMine && board.ownerName ? ` · by ${board.ownerName}` : ''} · updated{' '}
                      {formatRelative(board.updatedAt)}
                    </Caption1>
                  </article>
                ))}
              </div>
            </Section>
          ))}
        </div>
      )}

      {creating && <NewDashboardDialog onClose={() => setCreating(false)} />}

      <ConfirmDialog
        open={deleting !== null}
        title={`Delete “${deleting?.name ?? ''}”?`}
        confirmLabel="Delete"
        destructive
        busy={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting)}
        onCancel={() => setDeleting(null)}
      >
        {deleting?.isTemplate
          ? 'This is a hospital template that everyone can see. Deleting it removes it for all users. The reports themselves are not affected.'
          : deleting?.isShared
            ? 'Colleagues you shared it with will no longer see it. The reports themselves are not affected.'
            : 'The dashboard is removed. The reports themselves are not affected.'}
      </ConfirmDialog>
    </Page>
  );
}

function NewDashboardDialog({ onClose }: { onClose: () => void }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [shared, setShared] = useState(false);

  const create = useMutation({
    mutationFn: () =>
      analyticsApi.createDashboard({
        name: name.trim(),
        description: description.trim() || null,
        isShared: shared,
        widgets: [],
        version: null,
      }),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.dashboards });
      onClose();
      void navigate({
        to: '/analytics/dashboards/$dashboardId',
        params: { dashboardId: created.id },
        search: { edit: true },
      });
    },
    onError: (error) => notify.error(error, 'Could not create the dashboard'),
  });

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (name.trim() && !create.isPending) {
              create.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>New dashboard</DialogTitle>
            <DialogContent className={styles.form}>
              <Field label="Name" required>
                <Input
                  value={name}
                  maxLength={100}
                  autoFocus
                  placeholder="For example: Morning review"
                  onChange={(_, data) => setName(data.value)}
                />
              </Field>
              <Field label="Description">
                <Textarea
                  value={description}
                  maxLength={300}
                  rows={2}
                  placeholder="What this dashboard is for"
                  onChange={(_, data) => setDescription(data.value)}
                />
              </Field>
              <Switch
                checked={shared}
                onChange={(_, data) => setShared(data.checked)}
                label="Share with colleagues (they can view it; only you can change it)"
              />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose}>Cancel</Button>
              <Button
                appearance="primary"
                type="submit"
                disabled={!name.trim() || create.isPending}
                icon={create.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Create and add tiles
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
