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
  MessageBarTitle,
  Spinner,
  Tab,
  TabList,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  AddRegular,
  EditRegular,
  EyeOffRegular,
  PersonArrowRightRegular,
  ShieldErrorRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, useSession } from '@hc360/core';
import { DataTable, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { UserPicker } from '@/modules/hr/shared/UserPicker';
import type { PickedUser } from '@/modules/hr/shared/users';
import { qualityApi, qualityKeys, qualityQueries, type IncidentDetail } from '@/modules/quality/api';
import { CapaStatusBadge, IncidentStatusBadge, SentinelBadge, SeverityBadge } from '@/modules/quality/badges';
import { CapaDialog } from '@/modules/quality/CapaDialog';
import { IncidentEditDialog } from '@/modules/quality/IncidentEditDialog';
import { InvestigationEditor } from '@/modules/quality/InvestigationEditor';
import {
  incidentStatusLabel,
  incidentTypeLabel,
  QUALITY_PERMISSIONS as Q,
  sentinelKindLabel,
  type IncidentStatus,
} from '@/modules/quality/labels';
import { transitionBlocker, transitionLabel, transitionNeedsNote } from '@/modules/quality/rules';

type TabId = 'overview' | 'investigation' | 'actions';

export const Route = createFileRoute('/_app/quality/incidents/$incidentId')({
  validateSearch: (s: Record<string, unknown>): { tab?: TabId } => ({
    tab: s.tab === 'investigation' || s.tab === 'actions' ? s.tab : undefined,
  }),
  component: () => (
    <RequirePermission permission={Q.incidentsManage} what="incident details">
      <IncidentPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  flags: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  text: { whiteSpace: 'pre-wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  moves: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

function IncidentPage() {
  const { incidentId } = Route.useParams();
  const incident = useQuery(qualityQueries.incident(incidentId));
  if (incident.isLoading) {
    return <LoadingState />;
  }
  if (incident.error || !incident.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={incident.error} onRetry={() => void incident.refetch()} />
      </Page>
    );
  }
  return <IncidentView incident={incident.data} />;
}

function IncidentView({ incident: i }: { incident: IncidentDetail }) {
  const styles = useStyles();
  const navigate = useNavigate({ from: Route.fullPath });
  const { tab = 'overview' } = Route.useSearch();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const me = useSession((s) => s.user);
  const canPlanCapa = usePermission(Q.capaManage);
  const [editing, setEditing] = useState(false);
  const [assigning, setAssigning] = useState(false);
  const [moving, setMoving] = useState<IncidentStatus | null>(null);
  const [planning, setPlanning] = useState(false);
  const [busy, setBusy] = useState(false);
  const closed = i.status === 'Closed';
  const progress = {
    isSentinel: i.isSentinel,
    rcaCompleted: i.investigationStatus === 'Completed',
    capaCount: i.capas.filter((c) => c.status !== 'Cancelled').length,
    openCapaCount: i.capas.filter((c) => c.status === 'Open' || c.status === 'InProgress').length,
  };

  const move = async (to: IncidentStatus, note: string | null) => {
    setBusy(true);
    try {
      await qualityApi.transition(i.id, to, note);
      notify.success(
        `Incident ${to === 'Closed' ? 'closed' : i.status === 'Closed' ? 'reopened' : `moved to "${incidentStatusLabel[to]}"`}`,
      );
      setMoving(null);
      void queryClient.invalidateQueries({ queryKey: qualityKeys.all });
    } catch (error) {
      notify.error(error, 'Could not move the incident');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Page>
      <PageHeader
        title={`${incidentTypeLabel[i.type]} · ${i.incidentNumber}`}
        subtitle={`Happened ${formatDateTime(i.occurredAt)} at ${i.location}${i.departmentName ? `, ${i.departmentName}` : ''}`}
        icon={<ShieldErrorRegular />}
        breadcrumbs={[
          { label: 'Incidents', onClick: () => void navigate({ to: '/quality/incidents' }) },
          { label: i.incidentNumber },
        ]}
        actions={
          <>
            {!closed && (
              <Button icon={<EditRegular />} onClick={() => setEditing(true)}>
                Review and re-grade
              </Button>
            )}
            {!closed && (
              <Button icon={<PersonArrowRightRegular />} onClick={() => setAssigning(true)}>
                {i.assignedTo ? 'Reassign' : 'Assign'}
              </Button>
            )}
          </>
        }
      />

      <div className={styles.flags}>
        <IncidentStatusBadge status={i.status} />
        <SeverityBadge severity={i.severity} />
        {i.isSentinel && <SentinelBadge reason={i.sentinelReason} />}
        {i.isAnonymous && (
          <Caption1 className={styles.muted}>
            <EyeOffRegular aria-hidden /> Reported anonymously
          </Caption1>
        )}
        <Caption1 className={styles.muted}>{i.assignedTo ? `With ${i.assignedTo.name}` : 'Not assigned yet'}</Caption1>
      </div>

      {i.isSentinel && !progress.rcaCompleted && !closed && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Sentinel event</MessageBarTitle>
            {i.sentinelReason ? `${i.sentinelReason}. ` : ''}A completed root-cause analysis is required before it can
            be closed (NABH: analyse within 30 days).
          </MessageBarBody>
        </MessageBar>
      )}

      <Section
        title="Next step"
        description="Moves allowed from the current stage. Closing needs an outcome summary; reopening needs a reason."
      >
        <div className={styles.moves}>
          {i.nextStatuses.map((to) => {
            const blocker = transitionBlocker(i.status, to, progress);
            const button = (
              <Button
                key={to}
                appearance={to === 'Closed' ? 'secondary' : 'primary'}
                disabled={Boolean(blocker) || busy}
                onClick={() => setMoving(to)}
              >
                {transitionLabel(i.status, to)}
              </Button>
            );
            return blocker ? (
              <Tooltip key={to} content={blocker} relationship="description">
                <span>{button}</span>
              </Tooltip>
            ) : (
              button
            );
          })}
          {i.nextStatuses.length === 0 && <Caption1>No further steps.</Caption1>}
        </div>
      </Section>

      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={tab}
            onTabSelect={(_, d) =>
              void navigate({ search: { tab: d.value === 'overview' ? undefined : (d.value as TabId) }, replace: true })
            }
          >
            <Tab value="overview">What happened</Tab>
            <Tab value="investigation">Root-cause analysis{i.investigationStatus === 'Completed' ? ' ✓' : ''}</Tab>
            <Tab value="actions">Actions ({i.capas.length})</Tab>
          </TabList>
        </div>
        <div style={{ padding: 16 }}>
          {tab === 'overview' && <Overview incident={i} />}
          {tab === 'investigation' && <InvestigationEditor incidentId={i.id} canEdit={!closed} />}
          {tab === 'actions' && (
            <>
              {canPlanCapa && !closed && (
                <div style={{ marginBottom: 12 }}>
                  <Button icon={<AddRegular />} appearance="primary" onClick={() => setPlanning(true)}>
                    Plan an action
                  </Button>
                </div>
              )}
              <DataTable
                ariaLabel="Corrective and preventive actions"
                rows={i.capas}
                getRowId={(c) => c.id}
                onRowClick={(c) => void navigate({ to: '/quality/capas/$capaId', params: { capaId: c.id } })}
                columns={[
                  {
                    key: 'action',
                    header: 'Action',
                    render: (c) => (
                      <span>
                        <Text weight="semibold">{c.action}</Text>
                        <Caption1 block>
                          {c.capaNumber} · {c.type}
                        </Caption1>
                      </span>
                    ),
                  },
                  { key: 'due', header: 'Due', width: 120, render: (c) => formatDateOnly(c.dueDate) },
                  {
                    key: 'status',
                    header: 'Status',
                    render: (c) => <CapaStatusBadge status={c.status} isOverdue={c.isOverdue} />,
                  },
                ]}
                emptyTitle="No actions planned"
                emptyDescription="Plan corrective actions to fix the root cause and preventive actions to stop it happening again."
                size="small"
              />
            </>
          )}
        </div>
      </Section>

      <IncidentEditDialog open={editing} onClose={() => setEditing(false)} incident={i} />
      <AssignDialog
        open={assigning}
        onClose={() => setAssigning(false)}
        incident={i}
        meName={me?.displayName ?? null}
      />
      <CapaDialog
        open={planning}
        onClose={() => setPlanning(false)}
        context={{ source: 'Incident', incidentId: i.id, incidentNumber: i.incidentNumber }}
      />
      {moving && (
        <ReasonDialog
          open
          title={transitionLabel(i.status, moving)}
          description={
            moving === 'Closed'
              ? 'Summarise the outcome: what was found and what changed.'
              : i.status === 'Closed'
                ? 'Say why the incident is being reopened.'
                : `Move to "${incidentStatusLabel[moving]}".`
          }
          label={
            moving === 'Closed' ? 'Outcome summary' : i.status === 'Closed' ? 'Reason for reopening' : 'Note (optional)'
          }
          required={transitionNeedsNote(i.status, moving)}
          maxLength={2000}
          confirmLabel={transitionLabel(i.status, moving)}
          busy={busy}
          onConfirm={(note) => void move(moving, note || null)}
          onCancel={() => setMoving(null)}
        />
      )}
    </Page>
  );
}

function Overview({ incident: i }: { incident: IncidentDetail }) {
  const styles = useStyles();
  return (
    <DetailGrid>
      <Detail label="Description" wide>
        <span className={styles.text}>{i.description}</span>
      </Detail>
      <Detail label="Done straight away" wide>
        {i.immediateAction ? <span className={styles.text}>{i.immediateAction}</span> : null}
      </Detail>
      <Detail label="Witnesses">{i.witnesses}</Detail>
      <Detail label="Happened">{formatDateTime(i.occurredAt)}</Detail>
      <Detail label="Reported">{formatDateTime(i.reportedAt)}</Detail>
      <Detail label="Reported by">{i.isAnonymous ? 'Anonymous' : (i.reporterName ?? '—')}</Detail>
      <Detail label="Where">
        {i.location}
        {i.departmentName ? ` · ${i.departmentName}` : ''}
      </Detail>
      <Detail label="Patient">
        {i.patientId ? (
          <Link to="/patients/$patientId" params={{ patientId: i.patientId }}>
            Open the patient's record
          </Link>
        ) : null}
      </Detail>
      <Detail label="Staff involved">{i.involvedStaff.map((p) => p.name).join(', ')}</Detail>
      <Detail label="Never event">{i.sentinelKind ? sentinelKindLabel[i.sentinelKind] : null}</Detail>
      {i.lastStatusNote && (
        <Detail label="Latest note" wide>
          {i.lastStatusNote}
        </Detail>
      )}
      {i.closedAt && (
        <Detail label="Closed" wide>
          {formatDateTime(i.closedAt)}
          {i.closureNote ? ` · ${i.closureNote}` : ''}
        </Detail>
      )}
      {i.reopenCount > 0 && <Detail label="Reopened">{i.reopenCount} time(s)</Detail>}
    </DetailGrid>
  );
}

function AssignDialog({
  open,
  onClose,
  incident,
  meName,
}: {
  open: boolean;
  onClose: () => void;
  incident: IncidentDetail;
  meName: string | null;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [user, setUser] = useState<PickedUser | null>(incident.assignedTo);
  const [busy, setBusy] = useState(false);

  const assign = async () => {
    if (!user) {
      return;
    }
    setBusy(true);
    try {
      await qualityApi.assign(incident.id, user.id);
      notify.success('Incident assigned', `${user.name} has been notified.`);
      void queryClient.invalidateQueries({ queryKey: qualityKeys.all });
      onClose();
    } catch (error) {
      notify.error(error, 'Could not assign the incident');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>Assign {incident.incidentNumber}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <UserPicker label="Who reviews it" required value={user} onChange={setUser} />
            {incident.status === 'Reported' && <Caption1>Assigning moves the incident to "Under review".</Caption1>}
            {meName && !user && <Caption1>Tip: choose yourself ({meName}) to take it on.</Caption1>}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={busy || !user}
              icon={busy ? <Spinner size="tiny" /> : undefined}
              onClick={() => void assign()}
            >
              Assign
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
