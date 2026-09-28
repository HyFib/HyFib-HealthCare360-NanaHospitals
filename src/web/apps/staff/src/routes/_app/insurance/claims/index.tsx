import { Button, Caption1, Field, SearchBox, Select, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { AddRegular, DocumentCheckmarkRegular } from '@fluentui/react-icons';
import { formatDate, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { fromPaise, maxBig, toPaise } from '@/modules/billing/money';
import { enumParam, idParam, pageParam, textParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess } from '@/modules/billing/ui';
import { insuranceQueries } from '@/modules/insurance/api';
import { CreateClaimDialog } from '@/modules/insurance/ClaimDialogs';
import { CLAIM_STATUS_LABEL, CLAIM_STATUSES, claimTone, type ClaimStatus } from '@/modules/insurance/labels';

interface ClaimsSearch {
  status?: ClaimStatus;
  payerId?: string;
  q?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/insurance/claims/')({
  validateSearch: (s: Record<string, unknown>): ClaimsSearch => ({
    status: enumParam(s.status, CLAIM_STATUSES),
    payerId: idParam(s.payerId),
    q: textParam(s.q, 64),
    page: pageParam(s.page),
  }),
  component: ClaimsPage,
});

const TABS: (ClaimStatus | 'all')[] = [
  'all',
  'Submitted',
  'Query',
  'Approved',
  'PartiallySettled',
  'Settled',
  'Rejected',
];

/** Insurance claims (INS-05/06): submitted, queried, approved, settled in tranches with deductions, or rejected. */
function ClaimsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canClaims = usePermission('insurance.claims.manage');
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [creating, setCreating] = useState(false);
  const payers = useQuery({ ...insuranceQueries.payers(), enabled: canClaims });

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const claims = useQuery({
    ...insuranceQueries.claims({
      status: search.status,
      payerId: search.payerId,
      search: search.q,
      page: search.page ?? 1,
    }),
    enabled: canClaims,
  });

  if (!canClaims) {
    return (
      <Page>
        <PageHeader title="Claims" />
        <NoAccess what="insurance claims" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Claims"
        subtitle="What insurers, TPAs and corporates owe on finalised bills"
        icon={<DocumentCheckmarkRegular />}
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
            New claim
          </Button>
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
                  status: d.value === 'all' ? undefined : (d.value as ClaimStatus),
                  page: undefined,
                }),
              })
            }
          >
            {TABS.map((t) => (
              <Tab key={t} value={t}>
                {t === 'all' ? 'All' : CLAIM_STATUS_LABEL[t]}
              </Tab>
            ))}
          </TabList>
        </div>
        <div style={{ padding: '8px 16px 12px' }}>
          <FilterBar>
            <Field label="Claim number or insurer reference" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="e.g. NHCL26000031" />
            </Field>
            <Field label="Payer">
              <Select
                value={search.payerId ?? ''}
                onChange={(_, d) =>
                  void navigate({ search: (prev) => ({ ...prev, payerId: d.value || undefined, page: undefined }) })
                }
              >
                <option value="">All payers</option>
                {(payers.data ?? []).map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Claims"
          rows={claims.data?.items}
          loading={claims.isLoading}
          error={claims.error}
          onRetry={() => void claims.refetch()}
          getRowId={(c) => c.id}
          onRowClick={(c) => void navigate({ to: '/insurance/claims/$claimId', params: { claimId: c.id } })}
          columns={[
            {
              key: 'claim',
              header: 'Claim',
              render: (c) => (
                <span>
                  <Text font="monospace" weight="semibold">
                    {c.claimNumber}
                  </Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    Bill {c.billNumber} · {formatDate(c.submittedAt)}
                  </Caption1>
                </span>
              ),
            },
            { key: 'patient', header: 'Patient', render: (c) => c.patientName ?? '—' },
            { key: 'payer', header: 'Payer', priority: 'low', render: (c) => c.payerName ?? '—' },
            { key: 'claimed', header: 'Claimed', align: 'end', render: (c) => <Amount value={c.claimedAmount} /> },
            {
              key: 'settled',
              header: 'Received',
              align: 'end',
              priority: 'low',
              render: (c) => <Amount value={c.settledAmount} />,
            },
            {
              key: 'outstanding',
              header: 'Outstanding',
              align: 'end',
              render: (c) => {
                const outstanding = fromPaise(
                  maxBig(toPaise(c.claimedAmount) - toPaise(c.settledAmount) - toPaise(c.deductionAmount), 0n),
                );
                return (
                  <Amount
                    value={outstanding}
                    strong
                    tone={outstanding > 0 && c.status !== 'Rejected' ? 'negative' : 'muted'}
                  />
                );
              },
            },
            {
              key: 'status',
              header: 'Status',
              width: 190,
              render: (c) => (
                <StatusBadge status={c.status} tone={claimTone(c.status)} label={CLAIM_STATUS_LABEL[c.status]} />
              ),
            },
          ]}
          emptyTitle="No claims here"
          emptyDescription="Raise a claim from a finalised bill with an insurer share."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={claims.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {creating && (
        <CreateClaimDialog
          onClose={() => setCreating(false)}
          onCreated={(id) => void navigate({ to: '/insurance/claims/$claimId', params: { claimId: id } })}
        />
      )}
    </Page>
  );
}
