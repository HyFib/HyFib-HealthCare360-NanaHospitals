import { Button, Caption1, Field, SearchBox, Select, Text, tokens } from '@fluentui/react-components';
import { AddRegular, MegaphoneRegular } from '@fluentui/react-icons';
import { formatDateTime, formatRelative, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { crmQueries } from '@/modules/crm/api';
import {
  CAMPAIGN_CHANNELS,
  CAMPAIGN_STATUSES,
  CAMPAIGN_VIEWERS,
  campaignChannelLabel,
  CRM_PERMISSIONS as C,
  type CampaignChannel,
  type CampaignStatus,
} from '@/modules/crm/labels';
import { campaignTone } from '@/modules/crm/rules';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

interface CampaignsSearch {
  status?: CampaignStatus;
  channel?: CampaignChannel;
  search?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/crm/campaigns/')({
  validateSearch: (s: Record<string, unknown>): CampaignsSearch => ({
    status: CAMPAIGN_STATUSES.includes(s.status as CampaignStatus) ? (s.status as CampaignStatus) : undefined,
    channel: CAMPAIGN_CHANNELS.includes(s.channel as CampaignChannel) ? (s.channel as CampaignChannel) : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={CAMPAIGN_VIEWERS} what="campaigns">
      <CampaignsPage />
    </RequirePermission>
  ),
});

function CampaignsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(C.campaignsManage);
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const campaigns = useQuery(
    crmQueries.campaigns({ status: search.status, channel: search.channel, search: search.search, page: search.page }),
  );

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  const set = (patch: Partial<CampaignsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Campaigns"
        subtitle="SMS and WhatsApp messages to patients who agreed to hear from us. Sending happens between 10:00 and 21:00 only, and every message has an opt-out link."
        icon={<MegaphoneRegular />}
        breadcrumbs={[{ label: 'Patient relations', onClick: () => void navigate({ to: '/crm' }) }]}
        actions={
          canManage && (
            <Button
              appearance="primary"
              icon={<AddRegular />}
              onClick={() => void navigate({ to: '/crm/campaigns/new' })}
            >
              New campaign
            </Button>
          )
        }
      />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Campaign name" />
            </Field>
            <Field label="Stage">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as CampaignStatus | undefined })}
              >
                <option value="">Any</option>
                {CAMPAIGN_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {s}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Channel">
              <Select
                value={search.channel ?? ''}
                onChange={(_, d) => set({ channel: (d.value || undefined) as CampaignChannel | undefined })}
              >
                <option value="">Any</option>
                {CAMPAIGN_CHANNELS.map((c) => (
                  <option key={c} value={c}>
                    {campaignChannelLabel[c]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Campaigns"
          rows={campaigns.data?.items}
          loading={campaigns.isLoading}
          error={campaigns.error}
          onRetry={() => void campaigns.refetch()}
          getRowId={(c) => c.id}
          onRowClick={(c) => void navigate({ to: '/crm/campaigns/$campaignId', params: { campaignId: c.id } })}
          columns={[
            {
              key: 'name',
              header: 'Campaign',
              render: (c) => (
                <span>
                  <Text weight="semibold">{c.name}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {campaignChannelLabel[c.channel]} · created {formatRelative(c.createdAt)}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Stage',
              render: (c) => <StatusBadge status={c.status} tone={campaignTone(c.status)} />,
            },
            { key: 'scheduled', header: 'Sends', render: (c) => (c.scheduledAt ? formatDateTime(c.scheduledAt) : '—') },
            {
              key: 'audience',
              header: 'Audience',
              align: 'end',
              render: (c) => (c.status === 'Draft' ? '—' : c.audienceCount.toLocaleString('en-IN')),
            },
            {
              key: 'queued',
              header: 'Queued · skipped',
              align: 'end',
              priority: 'low',
              render: (c) =>
                c.queuedCount + c.skippedCount === 0
                  ? '—'
                  : `${c.queuedCount.toLocaleString('en-IN')} · ${c.skippedCount.toLocaleString('en-IN')}`,
            },
          ]}
          emptyTitle="No campaigns yet"
          emptyDescription={
            canManage
              ? 'Create a campaign, check the audience and schedule it.'
              : 'Campaigns drafted by the CRM team appear here.'
          }
          emptyAction={
            canManage ? (
              <Button
                appearance="primary"
                icon={<AddRegular />}
                onClick={() => void navigate({ to: '/crm/campaigns/new' })}
              >
                New campaign
              </Button>
            ) : undefined
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={campaigns.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
    </Page>
  );
}
