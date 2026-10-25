import { Button, Caption1, Field, Link, Select, Text, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatDate, formatDateTime } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import {
  MTP_CASE_READERS,
  MTP_CASE_STATUSES,
  MTP_PERMISSIONS,
  mtpQueries,
  mtpStatusLabel,
  mtpStatusTone,
  type MtpCaseFilters,
  type MtpCaseListItem,
  type MtpCaseStatus,
} from '@/modules/mtp/api';
import { MtpConfidential } from '@/modules/mtp/MtpConfidential';
import { CaseDialog } from '@/modules/mtp/MtpDialogs';

export const Route = createFileRoute('/_app/mtp/')({
  validateSearch: (s: Record<string, unknown>): MtpCaseFilters => ({
    status: MTP_CASE_STATUSES.includes(s.status as MtpCaseStatus) ? (s.status as MtpCaseStatus) : undefined,
    patientId: typeof s.patientId === 'string' && s.patientId ? s.patientId : undefined,
  }),
  component: () => (
    <RequirePermission permission={MTP_CASE_READERS} what="terminations of pregnancy">
      <MtpCasesPage />
    </RequirePermission>
  ),
});

/** The terminations of pregnancy: the cases certified and not yet done first, then those done or cancelled. */
function MtpCasesPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canCertify = usePermission(MTP_PERMISSIONS.certify);
  const cases = useQuery(mtpQueries.cases(filters));
  const [opening, setOpening] = useState(false);

  return (
    <Page>
      <PageHeader
        title="Terminations of pregnancy"
        subtitle="The woman's consent (Form C), the doctors' opinion (Form I) and the termination, as the admission register (Form III) records it."
        actions={
          canCertify && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setOpening(true)}>
              Open MTP case
            </Button>
          )
        }
      />
      <MtpConfidential />
      <Section flush>
        <div style={{ padding: '8px 16px 12px' }}>
          <FilterBar>
            <Field label="Status">
              <Select
                value={filters.status ?? ''}
                onChange={(_, d) =>
                  void navigate({
                    search: (prev) => ({ ...prev, status: (d.value || undefined) as MtpCaseStatus | undefined }),
                  })
                }
              >
                <option value="">All</option>
                {MTP_CASE_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {mtpStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            {filters.patientId && (
              <Button onClick={() => void navigate({ search: (prev) => ({ ...prev, patientId: undefined }) })}>
                Show every woman
              </Button>
            )}
          </FilterBar>
        </div>
        <DataTable<MtpCaseListItem>
          ariaLabel="Terminations of pregnancy"
          rows={cases.data}
          loading={cases.isLoading}
          error={cases.error}
          onRetry={() => void cases.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => void navigate({ to: '/mtp/$caseId', params: { caseId: r.id } })}
          size="small"
          columns={[
            {
              key: 'serial',
              header: 'Serial no.',
              width: 150,
              render: (r) => (
                <Link onClick={() => void navigate({ to: '/mtp/$caseId', params: { caseId: r.id } })}>
                  {r.serialNumber}
                </Link>
              ),
            },
            {
              key: 'woman',
              header: 'Woman',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {r.uhid}
                  </Caption1>
                </span>
              ),
            },
            { key: 'weeks', header: 'Pregnancy', render: (r) => `${r.weeksOfGestation} weeks` },
            { key: 'certified', header: 'Certified', priority: 'low', render: (r) => formatDateTime(r.certifiedAt) },
            {
              key: 'terminated',
              header: 'Terminated on',
              priority: 'low',
              render: (r) => (r.terminatedOn ? formatDate(r.terminatedOn) : ''),
            },
            {
              key: 'status',
              header: 'Status',
              render: (r) => (
                <StatusBadge status={r.status} tone={mtpStatusTone[r.status]} label={mtpStatusLabel[r.status]} />
              ),
            },
          ]}
          emptyTitle={filters.status ? 'No case in this status' : 'No MTP cases'}
          emptyDescription="A case is opened once the woman's consent and the doctors' opinion are taken."
        />
      </Section>
      {opening && <CaseDialog existing={null} onClose={() => setOpening(false)} />}
    </Page>
  );
}
