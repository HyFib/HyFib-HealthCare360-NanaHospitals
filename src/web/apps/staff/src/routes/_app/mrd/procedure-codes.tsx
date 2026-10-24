import { Button, Caption1, Field, makeStyles, SearchBox, Text, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { mrdQueries, type ProcedureCodeDto } from '@/modules/mrd/api';
import { sourceLabel } from '@/modules/mrd/catalogue';
import { ProcedureCodeDrawer } from '@/modules/mrd/ProcedureCodeDrawer';

interface ProcedureSearch {
  search?: string;
}

export const Route = createFileRoute('/_app/mrd/procedure-codes')({
  validateSearch: (s: Record<string, unknown>): ProcedureSearch => ({
    search: typeof s.search === 'string' && s.search ? s.search.slice(0, 100) : undefined,
  }),
  component: ProcedureCodesPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '12px 16px' },
  code: { fontFamily: tokens.fontFamilyMonospace, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * The procedure catalogue (MRD-01): the ICD-10-PCS codes and the hospital's own procedure codes coders and doctors
 * search, with ranked search by code, words or shorthand. MRD coders add codes and change them; the CMS ICD-10-PCS file
 * is imported on the ICD catalogue screen.
 */
function ProcedureCodesPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canCode = usePermission('mrd.coding');
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 250);
  const [editing, setEditing] = useState<ProcedureCodeDto | 'new' | null>(null);
  const codes = useQuery(mrdQueries.procedureCatalogue(search.search ?? ''));

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined }), replace: true });
    }
  }, [debounced, search.search, navigate]);

  return (
    <Page>
      <PageHeader
        title="Procedure catalogue"
        subtitle="Search by code (0DTJ4ZZ), words or shorthand (lap chole, LSCS). The same codes are offered in clinical coding and the doctor's procedure notes."
        actions={
          canCode && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add code
            </Button>
          )
        }
      />
      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 280px' }}>
              <SearchBox
                autoFocus
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Code, words or shorthand"
              />
            </Field>
          </FilterBar>
          <Caption1 className={styles.muted}>
            Codes switched off are not offered in searches and are not listed here. The CMS ICD-10-PCS file is imported
            on the <Link to="/mrd/icd">ICD catalogue</Link> screen.
          </Caption1>
        </div>
        <DataTable
          ariaLabel="Procedure codes"
          rows={codes.data}
          loading={codes.isLoading}
          error={codes.error}
          onRetry={() => void codes.refetch()}
          getRowId={(c) => c.id}
          onRowClick={canCode ? (c) => setEditing(c) : undefined}
          size="small"
          columns={[
            { key: 'code', header: 'Code', width: 120, render: (c) => <span className={styles.code}>{c.code}</span> },
            {
              key: 'title',
              header: 'Title',
              render: (c) => (
                <span>
                  <Text>{c.title}</Text>
                  {c.keywords && (
                    <Caption1 block className={styles.muted}>
                      {c.keywords}
                    </Caption1>
                  )}
                </span>
              ),
            },
            { key: 'category', header: 'Category', priority: 'low', render: (c) => c.category ?? '—' },
            { key: 'system', header: 'System', width: 120, priority: 'low', render: (c) => c.system },
            {
              key: 'source',
              header: 'Source',
              width: 110,
              priority: 'low',
              render: (c) => <StatusBadge status={c.source} tone="informative" label={sourceLabel(c.source)} />,
            },
          ]}
          emptyTitle={search.search ? 'No procedure code matches' : 'The procedure catalogue is empty'}
          emptyDescription={
            search.search
              ? 'Try other words or the code.'
              : canCode
                ? 'Add the hospital’s procedure codes, or import the CMS ICD-10-PCS file.'
                : undefined
          }
        />
      </Section>
      <ProcedureCodeDrawer code={editing} onClose={() => setEditing(null)} />
    </Page>
  );
}
