import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { formatDateOnly } from '@hc360/core';
import { Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import type { KeyboardEvent } from 'react';
import { usePermission } from '@/lib/permissions';
import { mrdQueries } from '@/modules/mrd/api';
import { MedicalRecordsWidget } from '@/modules/mrd/widgets';

export const Route = createFileRoute('/_app/mrd/')({
  component: MrdOverviewPage,
});

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(min(100%, 420px), 1fr))',
    alignItems: 'start',
  },
  list: { display: 'flex', flexDirection: 'column', gap: '2px', margin: 0, padding: 0, listStyle: 'none' },
  item: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: '2px 8px',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Medical records department at a glance: coding waiting, charts overdue, files out. */
function MrdOverviewPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canCode = usePermission('mrd.coding');
  const canRecords = usePermission('mrd.records');
  const coding = useQuery({ ...mrdQueries.codings({ status: 'Pending' }), enabled: canCode });
  const overdue = useQuery({ ...mrdQueries.records({ overdue: true }), enabled: canRecords });

  const open = (action: () => void) => ({
    role: 'button' as const,
    tabIndex: 0,
    onClick: action,
    onKeyDown: (event: KeyboardEvent) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        action();
      }
    },
  });

  return (
    <Page>
      <PageHeader title="Medical records" subtitle="Coding, chart completion, record requests and statistics" />
      <MedicalRecordsWidget />
      <div className={styles.grid}>
        {canCode && (
          <Section
            title="Next to code"
            description="Oldest discharged episodes first"
            actions={
              <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/mrd/coding' })}>
                Coding queue
              </Button>
            }
          >
            {(coding.data?.items.length ?? 0) === 0 ? (
              <Text className={styles.muted}>{coding.isLoading ? 'Loading…' : 'Nothing waiting for coding.'}</Text>
            ) : (
              <ul className={styles.list}>
                {coding.data?.items.slice(0, 8).map((c) => (
                  <li
                    key={c.id}
                    className={styles.item}
                    {...open(() => void navigate({ to: '/mrd/coding/$codingId', params: { codingId: c.id } }))}
                  >
                    <Text weight="semibold">{c.patientName ?? 'Patient'}</Text>
                    <StatusBadge status={c.status} />
                    <Caption1 className={styles.muted}>
                      {c.uhid} · {c.admissionId ? 'In-patient' : 'Out-patient'}
                      {c.episodeDate ? ` · ${formatDateOnly(c.episodeDate)}` : ''}
                    </Caption1>
                  </li>
                ))}
              </ul>
            )}
          </Section>
        )}
        {canRecords && (
          <Section
            title="Overdue charts"
            description="Incomplete or deficient past the completion date"
            actions={
              <Button
                size="small"
                appearance="subtle"
                onClick={() => void navigate({ to: '/mrd/records', search: { overdue: true } })}
              >
                All charts
              </Button>
            }
          >
            {(overdue.data?.items.length ?? 0) === 0 ? (
              <Text className={styles.muted}>{overdue.isLoading ? 'Loading…' : 'No overdue charts.'}</Text>
            ) : (
              <ul className={styles.list}>
                {overdue.data?.items.slice(0, 8).map((r) => (
                  <li
                    key={r.id}
                    className={styles.item}
                    {...open(() => void navigate({ to: '/mrd/records/$recordId', params: { recordId: r.id } }))}
                  >
                    <Text weight="semibold">{r.patientName ?? 'Patient'}</Text>
                    <StatusBadge status={r.status} />
                    <Caption1 className={styles.muted}>
                      {r.uhid} · {r.openDeficiencies} open {r.openDeficiencies === 1 ? 'deficiency' : 'deficiencies'}
                      {r.dueDate ? ` · due ${formatDateOnly(r.dueDate)}` : ''}
                    </Caption1>
                  </li>
                ))}
              </ul>
            )}
          </Section>
        )}
      </div>
    </Page>
  );
}
