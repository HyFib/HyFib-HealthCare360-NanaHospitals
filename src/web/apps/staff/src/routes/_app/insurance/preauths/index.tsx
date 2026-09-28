import { Button, Caption1, Field, SearchBox, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { AddRegular, HeartPulseRegular } from '@fluentui/react-icons';
import { formatDateTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { enumParam, pageParam, textParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess } from '@/modules/billing/ui';
import { insuranceQueries } from '@/modules/insurance/api';
import { PREAUTH_STATUS_LABEL, PREAUTH_STATUSES, preAuthTone, type PreAuthStatus } from '@/modules/insurance/labels';
import { PreAuthDialog } from '@/modules/insurance/PreAuthDialog';

interface PreAuthSearch {
  status?: PreAuthStatus;
  q?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/insurance/preauths/')({
  validateSearch: (s: Record<string, unknown>): PreAuthSearch => ({
    status: enumParam(s.status, PREAUTH_STATUSES),
    q: textParam(s.q, 40),
    page: pageParam(s.page),
  }),
  component: PreAuthsPage,
});

const TABS: (PreAuthStatus | 'all')[] = [
  'all',
  'Query',
  'Submitted',
  'Draft',
  'Approved',
  'PartiallyApproved',
  'Rejected',
];

/** Pre-authorisation worklist (INS-04): questions to answer, requests waiting for the insurer, drafts, decisions. */
function PreAuthsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRead = usePermission(['insurance.preauth.manage', 'insurance.claims.manage', 'billing.ip.manage']);
  const canManage = usePermission('insurance.preauth.manage');
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [creating, setCreating] = useState(false);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const preAuths = useQuery({
    ...insuranceQueries.preAuths({ status: search.status, search: search.q, page: search.page ?? 1 }),
    enabled: canRead,
  });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Pre-authorisations" />
        <NoAccess what="pre-authorisations" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Pre-authorisations"
        subtitle="Cashless approvals from insurers and TPAs"
        icon={<HeartPulseRegular />}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
              New pre-authorisation
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={search.status ?? 'all'}
            onTabSelect={(_, d) =>
              void navigate({
                search: (prev) => ({
                  ...prev,
                  status: d.value === 'all' ? undefined : (d.value as PreAuthStatus),
                  page: undefined,
                }),
              })
            }
          >
            {TABS.map((t) => (
              <Tab key={t} value={t}>
                {t === 'all' ? 'All' : PREAUTH_STATUS_LABEL[t]}
              </Tab>
            ))}
          </TabList>
        </div>
        <div style={{ padding: '8px 16px 12px' }}>
          <FilterBar>
            <Field label="Pre-authorisation number" style={{ flex: '1 1 240px', maxWidth: 360 }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="e.g. NHPA26000012" />
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Pre-authorisations"
          rows={preAuths.data?.items}
          loading={preAuths.isLoading}
          error={preAuths.error}
          onRetry={() => void preAuths.refetch()}
          getRowId={(p) => p.id}
          onRowClick={(p) => void navigate({ to: '/insurance/preauths/$preAuthId', params: { preAuthId: p.id } })}
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (p) => (
                <span>
                  <Text weight="semibold">{p.patientName ?? '—'}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {p.uhid} · {p.number || 'draft'}
                  </Caption1>
                </span>
              ),
            },
            { key: 'payer', header: 'Insurer', render: (p) => p.payerName ?? '—' },
            {
              key: 'requested',
              header: 'Requested',
              align: 'end',
              render: (p) => <Amount value={p.requestedAmount} />,
            },
            {
              key: 'approved',
              header: 'Approved',
              align: 'end',
              render: (p) =>
                p.effectiveApprovedAmount > 0 ? <Amount value={p.effectiveApprovedAmount} strong /> : '—',
            },
            {
              key: 'submitted',
              header: 'Submitted',
              priority: 'low',
              render: (p) => (p.submittedAt ? formatDateTime(p.submittedAt) : 'Not yet'),
            },
            {
              key: 'status',
              header: 'Status',
              width: 190,
              render: (p) => (
                <StatusBadge status={p.status} tone={preAuthTone(p.status)} label={PREAUTH_STATUS_LABEL[p.status]} />
              ),
            },
          ]}
          emptyTitle={search.status === 'Query' ? 'No questions from insurers' : 'No pre-authorisations here'}
          emptyDescription="Start one for a planned or current admission of an insured patient."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={preAuths.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {creating && (
        <PreAuthDialog
          preAuth={null}
          onClose={() => setCreating(false)}
          onSaved={(id) => void navigate({ to: '/insurance/preauths/$preAuthId', params: { preAuthId: id } })}
        />
      )}
    </Page>
  );
}
