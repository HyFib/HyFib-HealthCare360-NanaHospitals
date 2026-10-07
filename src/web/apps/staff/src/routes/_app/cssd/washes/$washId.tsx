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
import { CheckmarkCircleRegular, DismissCircleRegular, WasherRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { DataTable, ErrorState, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  cssdApi,
  cssdKeys,
  cssdQueries,
  PACK_STATUS_LABEL,
  WASH_STATUS_LABEL,
  washStatusTone,
} from '@/modules/cssd/api';
import { CompleteWashDialog } from '@/modules/cssd/WashDialogs';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';

export const Route = createFileRoute('/_app/cssd/washes/$washId')({
  component: WashPage,
});

const useStyles = makeStyles({
  rows: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: '12px 24px' },
  item: { display: 'flex', flexDirection: 'column', gap: '2px' },
  label: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

function Item({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.item}>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text>{children || '—'}</Text>
    </div>
  );
}

/** One wash cycle: its printout and inspection, the trays it washed and the packs made from them (CSSD-04). */
function WashPage() {
  const styles = useStyles();
  const { washId } = Route.useParams();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canProcess = usePermission('cssd.process');
  const wash = useQuery(cssdQueries.wash(washId));
  const [action, setAction] = useState<'complete' | 'abort' | null>(null);

  const abort = useMutation({
    mutationFn: (reason: string) => cssdApi.abortWash(washId, reason),
    onSuccess: () => {
      notify.warning('Wash cycle aborted', 'Wash the trays again in a new cycle.');
      setAction(null);
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
    },
  });

  if (wash.isLoading) {
    return <LoadingState label="Loading the wash cycle…" />;
  }
  if (wash.error || !wash.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={wash.error} onRetry={() => void wash.refetch()} title="Could not load this wash cycle" />
      </Page>
    );
  }

  const w = wash.data;
  const trays = w.trays.reduce((sum, t) => sum + t.quantity, 0);

  return (
    <Page>
      <PageHeader
        title={`Wash cycle ${w.cycleNumber}`}
        icon={<WasherRegular />}
        subtitle={`${w.washerName}${w.program ? ` · ${w.program}` : ''} · ${trays} ${trays === 1 ? 'tray' : 'trays'}`}
        breadcrumbs={[
          { label: 'Wash cycles', onClick: () => void navigate({ to: '/cssd/washes' }) },
          { label: w.cycleNumber },
        ]}
        actions={
          canProcess &&
          w.status === 'Running' && (
            <div className={styles.actions}>
              <Button appearance="primary" icon={<CheckmarkCircleRegular />} onClick={() => setAction('complete')}>
                Record result
              </Button>
              <Button icon={<DismissCircleRegular />} onClick={() => setAction('abort')}>
                Abort
              </Button>
            </div>
          )
        }
      />

      {w.status === 'Failed' && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Wash cycle failed</MessageBarTitle>
            {w.failureReason} Wash the trays again in a new cycle before packing them.
          </MessageBarBody>
        </MessageBar>
      )}

      <Section title="Cycle record">
        <div className={styles.rows}>
          <Item label="Status">
            <StatusBadge status={w.status} label={WASH_STATUS_LABEL[w.status]} tone={washStatusTone(w.status)} />
          </Item>
          <Item label="Started">{`${formatDateTime(w.startedAt)}${w.operatorName ? ` by ${w.operatorName}` : ''}`}</Item>
          <Item label="Completed">{w.completedAt ? formatDateTime(w.completedAt) : null}</Item>
          <Item label="Disinfection">
            {w.disinfectionTemperatureC !== null && w.holdMinutes !== null
              ? `${w.disinfectionTemperatureC} °C held for ${w.holdMinutes} min`
              : null}
          </Item>
          <Item label="A0">{w.a0 !== null ? `${w.a0} (at least ${w.requiredA0} needed)` : null}</Item>
          <Item label="Inspection">{w.inspectedClean === null ? null : w.inspectedClean ? 'Clean' : 'Not clean'}</Item>
          {w.notes && <Item label="Notes">{w.notes}</Item>}
        </div>
      </Section>

      <Section title="Trays washed" flush>
        <DataTable
          ariaLabel="Trays washed in this cycle"
          rows={w.trays}
          getRowId={(t) => t.instrumentSetId}
          size="small"
          columns={[
            { key: 'set', header: 'Set', render: (t) => t.setName ?? '—' },
            { key: 'quantity', header: 'Trays', width: 100, align: 'end', render: (t) => t.quantity },
          ]}
          emptyTitle="No trays in this cycle"
        />
      </Section>

      <Section title="Packs made from these trays" description="The sterilizer loads that named this wash cycle" flush>
        <DataTable
          ariaLabel="Packs made from the trays of this wash cycle"
          rows={w.packs}
          getRowId={(p) => p.packId}
          size="small"
          onRowClick={(p) => void navigate({ to: '/cssd/loads/$loadId', params: { loadId: p.loadId } })}
          columns={[
            { key: 'pack', header: 'Pack', width: 200, render: (p) => <Text font="monospace">{p.packNumber}</Text> },
            { key: 'set', header: 'Set', render: (p) => p.setName ?? '—' },
            { key: 'load', header: 'Sterilizer load', width: 170, render: (p) => p.loadCycleNumber ?? '—' },
            { key: 'status', header: 'Status', width: 140, render: (p) => PACK_STATUS_LABEL[p.status] },
          ]}
          emptyTitle="No packs yet"
          emptyDescription={
            w.status === 'Passed' ? 'Name this wash cycle when the trays go into a sterilizer load.' : undefined
          }
        />
      </Section>

      {action === 'complete' && <CompleteWashDialog wash={w} onClose={() => setAction(null)} />}
      <ReasonDialog
        open={action === 'abort'}
        title={`Abort wash cycle ${w.cycleNumber}?`}
        description="The cycle fails and its trays are washed again in a new cycle."
        placeholder="e.g. Washer stopped in the thermal stage, detergent ran out"
        confirmLabel="Abort cycle"
        destructive
        busy={abort.isPending}
        error={abort.error}
        onConfirm={(reason) => abort.mutate(reason)}
        onCancel={() => {
          setAction(null);
          abort.reset();
        }}
      />
    </Page>
  );
}
