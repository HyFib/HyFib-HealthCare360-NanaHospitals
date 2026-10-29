import {
  Button,
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular, CheckmarkCircleRegular } from '@fluentui/react-icons';
import { DataTable, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  foundText,
  signOffSimulatorRecords,
  simulatorRecordsQueries,
  sweepSimulatorRecords,
  sweepStatus,
  whenText,
} from '@/modules/admin/simulatorRecordsApi';

export const Route = createFileRoute('/_app/admin/simulator-records')({
  component: SimulatorRecordsPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  signOff: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '8px', maxWidth: '640px' },
  note: { width: '100%' },
});

/**
 * Records the integrations' simulators made that look real (audit P6): payments and refunds that moved no money, card
 * sales nobody paid, Aadhaar links nobody's finger proved, images and results from nowhere. The server sweeps a live
 * server's database for them every day; an IT officer deals with each kind and signs them off, and until then System
 * health warns.
 */
function SimulatorRecordsPage() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('admin.system.manage');
  const view = useQuery(simulatorRecordsQueries.view());
  const [note, setNote] = useState('');
  const refresh = () => void queryClient.invalidateQueries({ queryKey: simulatorRecordsQueries.view().queryKey });

  const sweep = useMutation({
    mutationFn: sweepSimulatorRecords,
    onSuccess: (result) => {
      notify.info(
        'Swept',
        result.total === 0 ? 'No records made by simulators.' : `${result.total} record(s) made by simulators.`,
      );
      refresh();
    },
    onError: (error) => notify.error(error, 'The database was not swept'),
  });

  const signOff = useMutation({
    mutationFn: ({ sweptAt, text }: { sweptAt: string; text: string }) => signOffSimulatorRecords(sweptAt, text),
    onSuccess: () => {
      setNote('');
      notify.success('Signed off', 'System health stops warning about these records.');
      refresh();
    },
    onError: (error) => notify.error(error, 'Not signed off'),
  });

  const data = view.data;
  const status = data ? sweepStatus(data) : null;
  const found = data?.latest?.findings.filter((f) => f.count > 0) ?? [];
  const clear = data?.latest?.findings.filter((f) => f.count === 0) ?? [];

  return (
    <Page>
      <PageHeader
        title="Simulator records"
        subtitle="Records the integrations' simulators made while this database was used for training. They look real."
        actions={
          canManage && (
            <Button
              appearance="primary"
              icon={<ArrowSyncRegular />}
              disabled={sweep.isPending}
              onClick={() => sweep.mutate()}
            >
              {sweep.isPending ? 'Sweeping…' : 'Sweep now'}
            </Button>
          )
        }
      />
      {status && (
        <MessageBar intent={status.intent} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{status.title}</MessageBarTitle>
            {status.text}
          </MessageBarBody>
        </MessageBar>
      )}
      <Section flush>
        <DataTable
          ariaLabel="Records made by simulators"
          rows={data ? found : undefined}
          loading={view.isLoading}
          error={view.error}
          onRetry={() => void view.refetch()}
          getRowId={(f) => f.code}
          columns={[
            {
              key: 'what',
              header: 'Records',
              width: 380,
              render: (f) => (
                <span>
                  <Text weight="semibold">{f.title}</Text>
                  <Caption1 block className={styles.muted}>
                    {f.guidance}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'severity',
              header: 'Severity',
              width: 110,
              render: (f) => (
                <StatusBadge
                  status={f.severity}
                  tone={f.severity === 'critical' ? 'danger' : f.severity === 'warning' ? 'warning' : 'informative'}
                />
              ),
            },
            { key: 'found', header: 'Found', width: 140, render: (f) => foundText(f) },
            { key: 'when', header: 'Made', width: 220, priority: 'low', render: (f) => whenText(f) },
            {
              key: 'examples',
              header: 'Newest',
              width: 240,
              priority: 'low',
              render: (f) => <Text font="monospace">{f.examples.join(', ') || '—'}</Text>,
            },
          ]}
          emptyTitle={data?.latest ? 'Nothing made by a simulator' : 'Not swept yet'}
          emptyDescription={
            data?.latest ? 'No kind of record a simulator makes is in this database.' : 'Sweep now to see what is here.'
          }
          size="small"
        />
        {clear.length > 0 && (
          <Caption1 block className={styles.muted}>
            None found: {clear.map((f) => f.title.toLowerCase()).join('; ')}.
          </Caption1>
        )}
      </Section>
      {data?.latest && data.outstanding && canManage && (
        <Section title="Sign off">
          <div className={styles.signOff}>
            <Text>
              Sign off once each kind above has been dealt with as it says. The sign-off covers the records this sweep
              found; one a simulator makes later brings the warning back.
            </Text>
            <Field label="What was done" hint="10 to 1000 characters. It is kept in the audit trail." required>
              <Textarea
                className={styles.note}
                value={note}
                maxLength={1000}
                onChange={(_, d) => setNote(d.value)}
                resize="vertical"
              />
            </Field>
            <Button
              icon={<CheckmarkCircleRegular />}
              disabled={note.trim().length < 10 || signOff.isPending}
              onClick={() => signOff.mutate({ sweptAt: data.latest!.sweptAt, text: note.trim() })}
            >
              Sign off this sweep
            </Button>
          </div>
        </Section>
      )}
    </Page>
  );
}
