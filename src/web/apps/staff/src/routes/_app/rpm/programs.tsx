import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { DataTable, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { rpmQueries, type RpmProgramDto } from '@/modules/virtualcare/rpm/api';
import { programKindLabel } from '@/modules/virtualcare/rpm/labels';
import { ProgramDrawer } from '@/modules/virtualcare/rpm/ProgramDrawer';

export const Route = createFileRoute('/_app/rpm/programs')({
  component: ProgramsPage,
});

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
});

/** Monitoring programmes (hypertension, diabetes, cardiac, after discharge…) and their expected readings per day. */
function ProgramsPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canManage = usePermission('rpm.manage');
  const programs = useQuery(rpmQueries.programs());
  const [editing, setEditing] = useState<{ program: RpmProgramDto | null } | null>(null);

  return (
    <Page>
      <PageHeader
        title="Monitoring programmes"
        subtitle="Each programme sets the standard alert limits for new enrolments and how often patients should send readings."
        breadcrumbs={[
          { label: 'Remote monitoring', onClick: () => void navigate({ to: '/rpm' }) },
          { label: 'Programmes' },
        ]}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing({ program: null })}>
              Add programme
            </Button>
          )
        }
      />
      <Section flush>
        <DataTable
          ariaLabel="Monitoring programmes"
          rows={programs.data}
          loading={programs.isLoading}
          error={programs.error}
          onRetry={() => void programs.refetch()}
          getRowId={(p) => p.id}
          onRowClick={canManage ? (p) => setEditing({ program: p }) : undefined}
          columns={[
            { key: 'code', header: 'Code', width: 100, render: (p) => <Text className={styles.mono}>{p.code}</Text> },
            {
              key: 'name',
              header: 'Programme',
              render: (p) => (
                <span className={styles.stack}>
                  <Text weight="semibold">{p.name}</Text>
                  {p.description && <Caption1 className={styles.muted}>{p.description}</Caption1>}
                </span>
              ),
            },
            { key: 'kind', header: 'Kind', width: 150, render: (p) => programKindLabel[p.kind] },
            { key: 'readings', header: 'Readings a day', width: 140, render: (p) => p.expectedReadingsPerDay },
            {
              key: 'status',
              header: 'Status',
              width: 120,
              render: (p) => (
                <StatusBadge
                  status={p.isActive ? 'Active' : 'Inactive'}
                  tone={p.isActive ? 'success' : 'informative'}
                  label={p.isActive ? 'Open' : 'Closed'}
                />
              ),
            },
          ]}
          emptyTitle="No programmes"
          emptyDescription={canManage ? 'Add a programme before enrolling patients.' : undefined}
          emptyAction={
            canManage ? (
              <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing({ program: null })}>
                Add programme
              </Button>
            ) : undefined
          }
          size="small"
        />
      </Section>
      <ProgramDrawer open={editing !== null} program={editing?.program ?? null} onClose={() => setEditing(null)} />
    </Page>
  );
}
