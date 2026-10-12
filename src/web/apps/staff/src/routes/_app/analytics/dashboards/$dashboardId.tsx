import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowClockwiseRegular,
  BoardRegular,
  CopyRegular,
  EditRegular,
  SaveRegular,
} from '@fluentui/react-icons';
import { formatRelative, isApiError } from '@hc360/core';
import {
  EmptyState,
  ErrorState,
  LoadingState,
  Page,
  PageHeader,
  Section,
  useLeaveGuard,
  useNotify,
  type LeaveQuestion,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  analyticsApi,
  analyticsKeys,
  analyticsQueries,
  type DashboardDetail,
  type ReportCatalogue,
  type WidgetWidth,
} from '@/modules/analytics/api';
import { ANALYTICS_PERMISSIONS as A, CUSTOM_PERIOD, PERIOD_PRESETS } from '@/modules/analytics/labels';
import { applyPeriodOverride, moveItem, nextWidgetId } from '@/modules/analytics/params';
import { WidgetCard, type WidgetConfig } from '@/modules/analytics/WidgetCard';
import { WidgetEditorDialog, type WidgetDraft } from '@/modules/analytics/WidgetEditorDialog';

interface DashboardSearch {
  /** Shows every tile for this period instead of the saved ones (not saved). */
  period?: string;
  edit?: boolean;
}

const MAX_WIDGETS = 24;

export const Route = createFileRoute('/_app/analytics/dashboards/$dashboardId')({
  validateSearch: (s: Record<string, unknown>): DashboardSearch => ({
    period:
      typeof s.period === 'string' && s.period !== CUSTOM_PERIOD && PERIOD_PRESETS.some((p) => p.value === s.period)
        ? s.period
        : undefined,
    edit: s.edit === true ? true : undefined,
  }),
  component: DashboardPage,
});

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    alignItems: 'stretch',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
    '@media (max-width: 700px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  span1: { gridColumn: 'span 1' },
  span2: { gridColumn: 'span 2', '@media (max-width: 700px)': { gridColumn: 'span 1' } },
  span4: { gridColumn: '1 / -1' },
  add: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '6px',
    minHeight: '200px',
    borderRadius: tokens.borderRadiusXLarge,
    border: `2px dashed ${tokens.colorNeutralStroke1}`,
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground2,
    cursor: 'pointer',
    fontSize: tokens.fontSizeBase300,
    ':hover': { border: `2px dashed ${tokens.colorBrandStroke1}`, color: tokens.colorBrandForeground1 },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  addIcon: { fontSize: '28px', display: 'flex' },
  details: {
    display: 'grid',
    gap: '12px',
    gridTemplateColumns: 'minmax(200px, 1fr) minmax(240px, 2fr) auto',
    alignItems: 'end',
    '@media (max-width: 900px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** BI-02/03: a saved dashboard, viewed with live figures or edited tile by tile. */
function DashboardPage() {
  const { dashboardId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const dashboard = useQuery(analyticsQueries.dashboard(dashboardId));
  const catalogue = useQuery(analyticsQueries.catalogue());

  if (dashboard.isLoading || catalogue.isLoading) {
    return <LoadingState label="Opening the dashboard…" />;
  }
  if (dashboard.error || catalogue.error || !dashboard.data || !catalogue.data) {
    const notFound = isApiError(dashboard.error) && dashboard.error.status === 404;
    return (
      <Page maxWidth={900}>
        <PageHeader
          title={notFound ? 'Dashboard not found' : 'Dashboard'}
          breadcrumbs={[
            { label: 'Dashboards', onClick: () => void navigate({ to: '/analytics/dashboards' }) },
            { label: 'Not available' },
          ]}
        />
        {notFound ? (
          <Section>
            <EmptyState
              title="This dashboard is not available"
              description="It may have been deleted, or it is no longer shared with you."
              action={<Button onClick={() => void navigate({ to: '/analytics/dashboards' })}>All dashboards</Button>}
            />
          </Section>
        ) : (
          <ErrorState
            error={dashboard.error ?? catalogue.error}
            onRetry={() => void Promise.all([dashboard.refetch(), catalogue.refetch()])}
          />
        )}
      </Page>
    );
  }

  const board = dashboard.data;
  if (search.edit && board.canEdit) {
    return <DashboardEditor key={`${board.id}:${board.version}`} dashboard={board} catalogue={catalogue.data} />;
  }
  return <DashboardViewer dashboard={board} catalogue={catalogue.data} period={search.period} />;
}

function spanClass(styles: ReturnType<typeof useStyles>, width: WidgetWidth): string {
  return width === 4 ? styles.span4 : width === 2 ? styles.span2 : styles.span1;
}

function toConfig(widget: DashboardDetail['widgets'][number]): WidgetConfig {
  return {
    id: widget.id,
    reportId: widget.reportId,
    title: widget.title,
    view: widget.view,
    width: widget.width,
    parameters: widget.parameters,
    canView: widget.canView,
  };
}

// ------------------------------------------------------------------------------------------------ viewer

function DashboardViewer({
  dashboard,
  catalogue,
  period,
}: {
  dashboard: DashboardDetail;
  catalogue: ReportCatalogue;
  period: string | undefined;
}) {
  const styles = useStyles();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission(A.dashboardsManage);
  const reports = new Map(catalogue.reports.map((r) => [r.id, r]));

  const copy = useMutation({
    mutationFn: () => analyticsApi.copyDashboard(dashboard.id),
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

  const owner = dashboard.isTemplate
    ? 'Hospital template'
    : dashboard.isMine
      ? dashboard.isShared
        ? 'Yours, shared with colleagues'
        : 'Only you can see this'
      : `Shared by ${dashboard.ownerName ?? 'a colleague'}`;

  return (
    <Page>
      <PageHeader
        title={dashboard.name}
        icon={<BoardRegular />}
        subtitle={[dashboard.description, `${owner} · updated ${formatRelative(dashboard.updatedAt)}`]
          .filter(Boolean)
          .join(' · ')}
        breadcrumbs={[
          { label: 'Analytics', onClick: () => void navigate({ to: '/analytics' }) },
          { label: 'Dashboards', onClick: () => void navigate({ to: '/analytics/dashboards' }) },
          { label: dashboard.name },
        ]}
        actions={
          <>
            <Select
              aria-label="Period for every tile"
              value={period ?? ''}
              onChange={(_, data) =>
                void navigate({ search: (prev) => ({ ...prev, period: data.value || undefined }), replace: true })
              }
            >
              <option value="">Saved periods</option>
              {PERIOD_PRESETS.filter((p) => p.value !== CUSTOM_PERIOD).map((p) => (
                <option key={p.value} value={p.value}>
                  {p.label} (all tiles)
                </option>
              ))}
            </Select>
            <Tooltip content="Refresh every tile" relationship="label">
              <Button
                icon={<ArrowClockwiseRegular />}
                onClick={() => void queryClient.invalidateQueries({ queryKey: analyticsKeys.runs })}
              />
            </Tooltip>
            {canManage && (
              <Button
                icon={copy.isPending ? <Spinner size="tiny" aria-hidden /> : <CopyRegular />}
                disabled={copy.isPending}
                onClick={() => copy.mutate()}
              >
                Make a copy
              </Button>
            )}
            {dashboard.canEdit && (
              <Button
                appearance="primary"
                icon={<EditRegular />}
                onClick={() => void navigate({ search: (prev) => ({ ...prev, edit: true }) })}
              >
                Change
              </Button>
            )}
          </>
        }
      />

      {dashboard.widgets.length === 0 ? (
        <Section>
          <EmptyState
            title="This dashboard has no tiles yet"
            description={
              dashboard.canEdit ? 'Add tiles from any report you can open.' : 'The owner has not added any tiles.'
            }
            action={
              dashboard.canEdit ? (
                <Button
                  icon={<AddRegular />}
                  onClick={() => void navigate({ search: (prev) => ({ ...prev, edit: true }) })}
                >
                  Add tiles
                </Button>
              ) : undefined
            }
          />
        </Section>
      ) : (
        <div className={styles.grid}>
          {dashboard.widgets.map((widget) => {
            const report = reports.get(widget.reportId);
            return (
              <div key={widget.id} className={spanClass(styles, widget.width)}>
                <WidgetCard
                  widget={toConfig(widget)}
                  report={report}
                  inputs={applyPeriodOverride(report?.parameters, widget.parameters, period)}
                />
              </div>
            );
          })}
        </div>
      )}
      {period && (
        <Caption1 className={styles.muted}>
          Showing every tile for {PERIOD_PRESETS.find((p) => p.value === period)?.label.toLowerCase()}. The saved
          periods are unchanged.
        </Caption1>
      )}
    </Page>
  );
}

// ------------------------------------------------------------------------------------------------ editor

const LEAVE_DASHBOARD: LeaveQuestion = {
  title: 'Leave without saving?',
  body: 'Your changes to this dashboard are not saved.',
  confirmLabel: 'Discard changes',
  destructive: true,
};

function DashboardEditor({ dashboard, catalogue }: { dashboard: DashboardDetail; catalogue: ReportCatalogue }) {
  const styles = useStyles();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [name, setName] = useState(dashboard.name);
  const [description, setDescription] = useState(dashboard.description ?? '');
  const [shared, setShared] = useState(dashboard.isShared);
  const [widgets, setWidgets] = useState<WidgetConfig[]>(() => dashboard.widgets.map(toConfig));
  const [editing, setEditing] = useState<{ index: number | null } | null>(null);
  const [conflict, setConflict] = useState(false);
  const reports = new Map(catalogue.reports.map((r) => [r.id, r]));

  const initial = JSON.stringify({
    name: dashboard.name,
    description: dashboard.description ?? '',
    shared: dashboard.isShared,
    widgets: dashboard.widgets.map(toConfig),
  });
  const dirty = JSON.stringify({ name, description, shared, widgets }) !== initial;
  // Any navigation with unsaved changes asks first (SRS UX-08), Cancel too: it leaves the editor (`?edit`).
  const leave = useLeaveGuard({ dirty, leaves: () => true, leaveQuestion: LEAVE_DASHBOARD });

  // Cancel asks first when there are unsaved changes (the leave guard above); after a save the editor closes directly.
  const cancel = () => void navigate({ search: (prev) => ({ ...prev, edit: undefined }) });
  const close = () => {
    leave.allowLeaving();
    cancel();
  };

  const save = useMutation({
    mutationFn: () =>
      analyticsApi.updateDashboard(dashboard.id, {
        name: name.trim(),
        description: description.trim() || null,
        isShared: shared,
        widgets: widgets.map((w) => ({
          id: w.id,
          reportId: w.reportId,
          title: w.title,
          view: w.view,
          width: w.width,
          parameters: w.parameters,
        })),
        version: dashboard.version,
      }),
    onSuccess: (saved) => {
      queryClient.setQueryData(analyticsQueries.dashboard(dashboard.id).queryKey, saved);
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.dashboards });
      notify.success('Dashboard saved');
      close();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        setConflict(true);
      } else {
        notify.error(error, 'Could not save the dashboard');
      }
    },
  });

  const saveWidget = (draft: WidgetDraft) => {
    const config: WidgetConfig = { ...draft, canView: true };
    setWidgets((current) =>
      editing?.index === null || editing === null
        ? [...current, config]
        : current.map((w, i) => (i === editing.index ? config : w)),
    );
    setEditing(null);
  };

  const editingWidget = editing && editing.index !== null ? widgets[editing.index] : undefined;

  return (
    <Page>
      <PageHeader
        title={`Change “${dashboard.name}”`}
        icon={<EditRegular />}
        subtitle="Add, arrange and size the tiles, then save. Each tile keeps its own report inputs."
        breadcrumbs={[
          { label: 'Dashboards', onClick: () => void navigate({ to: '/analytics/dashboards' }) },
          { label: dashboard.name, onClick: cancel },
          { label: 'Change' },
        ]}
        actions={
          <>
            <Button onClick={cancel} disabled={save.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
              disabled={!name.trim() || save.isPending || !dirty}
              onClick={() => save.mutate()}
            >
              Save dashboard
            </Button>
          </>
        }
      />

      {conflict && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            Someone else saved this dashboard while you were changing it. Reload to see their version (your changes here
            will be lost).
          </MessageBarBody>
          <MessageBarActions>
            <Button
              onClick={() => {
                leave.allowLeaving();
                void queryClient.invalidateQueries({ queryKey: analyticsQueries.dashboard(dashboard.id).queryKey });
              }}
            >
              Reload
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <Section>
        <div className={styles.details}>
          <Field
            label="Name"
            required
            validationMessage={name.trim() ? undefined : 'Give the dashboard a name.'}
            validationState={name.trim() ? 'none' : 'error'}
          >
            <Input value={name} maxLength={100} onChange={(_, data) => setName(data.value)} />
          </Field>
          <Field label="Description">
            <Input
              value={description}
              maxLength={300}
              placeholder="What this dashboard is for"
              onChange={(_, data) => setDescription(data.value)}
            />
          </Field>
          <Field hint={dashboard.isTemplate ? 'Templates are always visible to everyone.' : undefined}>
            <Switch
              checked={dashboard.isTemplate || shared}
              disabled={dashboard.isTemplate}
              onChange={(_, data) => setShared(data.checked)}
              label="Shared with colleagues"
            />
          </Field>
        </div>
      </Section>

      <div className={styles.grid}>
        {widgets.map((widget, index) => {
          const report = reports.get(widget.reportId);
          return (
            <div key={widget.id} className={spanClass(styles, widget.width)}>
              <WidgetCard
                widget={widget}
                report={report}
                inputs={widget.parameters}
                editing={{
                  onEdit: () => setEditing({ index }),
                  onRemove: () => setWidgets((current) => current.filter((_, i) => i !== index)),
                  onMove: (delta) => setWidgets((current) => moveItem(current, index, delta)),
                  onResize: (width) =>
                    setWidgets((current) => current.map((w, i) => (i === index ? { ...w, width } : w))),
                  isFirst: index === 0,
                  isLast: index === widgets.length - 1,
                }}
              />
            </div>
          );
        })}
        {widgets.length < MAX_WIDGETS ? (
          <button
            type="button"
            className={mergeClasses(styles.add, styles.span1)}
            onClick={() => setEditing({ index: null })}
          >
            <span className={styles.addIcon}>
              <AddRegular />
            </span>
            Add a tile
          </button>
        ) : (
          <Text className={styles.muted}>A dashboard can have at most {MAX_WIDGETS} tiles.</Text>
        )}
      </div>

      {editing && (
        <WidgetEditorDialog
          catalogue={catalogue}
          initial={editingWidget ?? null}
          newId={nextWidgetId(widgets.map((w) => w.id))}
          onSave={saveWidget}
          onClose={() => setEditing(null)}
        />
      )}
    </Page>
  );
}
