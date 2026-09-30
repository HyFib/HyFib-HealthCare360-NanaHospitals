import { Button, Caption1, Text, tokens } from '@fluentui/react-components';
import {
  AddRegular,
  CallRegular,
  ClockAlarmRegular,
  MegaphoneRegular,
  PeopleCommunityRegular,
  PersonAddRegular,
  PersonHeartRegular,
  StethoscopeRegular,
  TargetArrowRegular,
} from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { DataTable, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { crmQueries } from '@/modules/crm/api';
import { FollowUpCreateDialog } from '@/modules/crm/FollowUpCreateDialog';
import {
  CAMPAIGN_VIEWERS,
  CRM_PERMISSIONS as C,
  CRM_USERS,
  followUpTypeLabel,
  leadSourceLabel,
  leadStatusLabel,
} from '@/modules/crm/labels';
import { LeadDrawer } from '@/modules/crm/LeadDrawer';
import { presetRange } from '@/modules/feedback/rules';
import { Columns, KpiGrid } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

export const Route = createFileRoute('/_app/crm/')({
  component: () => (
    <RequirePermission permission={CRM_USERS} what="the CRM workspace">
      <CrmOverview />
    </RequirePermission>
  ),
});

function count(value: number | undefined, loading: boolean): string {
  return loading ? '…' : (value ?? 0).toLocaleString('en-IN');
}

function CrmOverview() {
  const navigate = useNavigate();
  const canLeads = usePermission(C.leadsManage);
  const canFollowUps = usePermission(C.followUpsManage);
  const canCampaigns = usePermission(CAMPAIGN_VIEWERS);
  const canNewCampaign = usePermission(C.campaignsManage);
  const canReferrals = usePermission(C.referralsManage);
  const canPatient360 = usePermission(C.patient360Read);
  const [addingLead, setAddingLead] = useState(false);
  const [addingFollowUp, setAddingFollowUp] = useState(false);
  const today = todayIso();
  const month = presetRange('thisMonth', today);

  const openLeads = useQuery({ ...crmQueries.leads({ pageSize: 1 }), enabled: canLeads });
  const newLeads = useQuery({ ...crmQueries.leads({ status: 'New', pageSize: 1 }), enabled: canLeads });
  const dueLeads = useQuery({ ...crmQueries.leads({ dueOnly: true, pageSize: 5 }), enabled: canLeads });
  const callsToday = useQuery({ ...crmQueries.followUps({ dueTo: today }), enabled: canFollowUps });
  const overdueCalls = useQuery({ ...crmQueries.followUps({ overdueOnly: true }), enabled: canFollowUps });
  const myCalls = useQuery({ ...crmQueries.followUps({ mine: true, dueTo: today }), enabled: canFollowUps });
  const scheduled = useQuery({ ...crmQueries.campaigns({ status: 'Scheduled' }), enabled: canCampaigns });
  const sending = useQuery({ ...crmQueries.campaigns({ status: 'Sending' }), enabled: canCampaigns });
  const referrers = useQuery({
    ...crmQueries.referringDoctors({ from: month.from, to: month.to }),
    enabled: canReferrals,
  });
  const topReferrers = (referrers.data?.items ?? []).filter((d) => d.referrals > 0).slice(0, 5);

  return (
    <Page>
      <PageHeader
        title="Patient relations"
        subtitle="Enquiries to follow up, patients to call, campaigns going out and the doctors who refer to us."
        icon={<PeopleCommunityRegular />}
        actions={
          <>
            {canLeads && (
              <Button icon={<PersonAddRegular />} onClick={() => setAddingLead(true)}>
                New lead
              </Button>
            )}
            {canFollowUps && (
              <Button icon={<CallRegular />} onClick={() => setAddingFollowUp(true)}>
                Add follow-up call
              </Button>
            )}
            {canNewCampaign && (
              <Button icon={<AddRegular />} onClick={() => void navigate({ to: '/crm/campaigns/new' })}>
                New campaign
              </Button>
            )}
            {canPatient360 && (
              <Button icon={<PersonHeartRegular />} onClick={() => void navigate({ to: '/crm/patients' })}>
                Patient 360
              </Button>
            )}
          </>
        }
      />

      <KpiGrid>
        {canFollowUps && (
          <KpiCard
            label="Calls due today"
            value={count(callsToday.data?.totalCount, callsToday.isLoading)}
            hint={`${count(myCalls.data?.totalCount, myCalls.isLoading)} assigned to you`}
            icon={<CallRegular />}
            onClick={() => void navigate({ to: '/crm/follow-ups' })}
          />
        )}
        {canFollowUps && (
          <KpiCard
            label="Overdue calls"
            value={count(overdueCalls.data?.totalCount, overdueCalls.isLoading)}
            hint="Past their call date"
            icon={<ClockAlarmRegular />}
            onClick={() => void navigate({ to: '/crm/follow-ups', search: { view: 'overdue' } })}
          />
        )}
        {canLeads && (
          <KpiCard
            label="Open leads"
            value={count(openLeads.data?.totalCount, openLeads.isLoading)}
            hint={`${count(newLeads.data?.totalCount, newLeads.isLoading)} not contacted yet`}
            icon={<TargetArrowRegular />}
            onClick={() => void navigate({ to: '/crm/leads' })}
          />
        )}
        {canLeads && (
          <KpiCard
            label="Call-backs due"
            value={count(dueLeads.data?.totalCount, dueLeads.isLoading)}
            hint="Leads whose next call time has come"
            icon={<ClockAlarmRegular />}
            onClick={() => void navigate({ to: '/crm/leads', search: { view: 'list', dueOnly: true } })}
          />
        )}
        {canCampaigns && (
          <KpiCard
            label="Campaigns going out"
            value={count(
              (scheduled.data?.totalCount ?? 0) + (sending.data?.totalCount ?? 0),
              scheduled.isLoading || sending.isLoading,
            )}
            hint={`${count(sending.data?.totalCount, sending.isLoading)} sending now`}
            icon={<MegaphoneRegular />}
            onClick={() => void navigate({ to: '/crm/campaigns' })}
          />
        )}
      </KpiGrid>

      <Columns>
        {canLeads && (
          <Section
            title="Leads to call back"
            description="Enquiries whose next call time has come"
            actions={
              <Button
                size="small"
                appearance="subtle"
                onClick={() => void navigate({ to: '/crm/leads', search: { view: 'list', dueOnly: true } })}
              >
                See all
              </Button>
            }
          >
            <DataTable
              ariaLabel="Leads to call back"
              rows={dueLeads.data?.items}
              loading={dueLeads.isLoading}
              error={dueLeads.error}
              onRetry={() => void dueLeads.refetch()}
              getRowId={(l) => l.id}
              onRowClick={(l) => void navigate({ to: '/crm/leads/$leadId', params: { leadId: l.id } })}
              columns={[
                {
                  key: 'name',
                  header: 'Lead',
                  render: (l) => (
                    <span>
                      <Text weight="semibold">{l.name}</Text>
                      <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                        {l.leadNumber} · {leadSourceLabel[l.source]}
                      </Caption1>
                    </span>
                  ),
                },
                { key: 'status', header: 'Stage', render: (l) => leadStatusLabel[l.status] },
                {
                  key: 'due',
                  header: 'Call by',
                  render: (l) => (l.nextFollowUpAt ? formatDateTime(l.nextFollowUpAt) : '—'),
                },
              ]}
              emptyTitle="No call-backs due"
              emptyDescription="Leads with a next call time appear here when it comes."
            />
          </Section>
        )}
        {canFollowUps && (
          <Section
            title="Calls for today"
            description="Follow-up calls due today or earlier"
            actions={
              <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/crm/follow-ups' })}>
                Open the call list
              </Button>
            }
          >
            <DataTable
              ariaLabel="Follow-up calls due"
              rows={callsToday.data?.items.slice(0, 5)}
              loading={callsToday.isLoading}
              error={callsToday.error}
              onRetry={() => void callsToday.refetch()}
              getRowId={(f) => f.id}
              onRowClick={() => void navigate({ to: '/crm/follow-ups' })}
              columns={[
                {
                  key: 'patient',
                  header: 'Patient',
                  render: (f) => (
                    <span>
                      <Text weight="semibold">{f.patientName}</Text>
                      <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                        {f.uhid}
                      </Caption1>
                    </span>
                  ),
                },
                { key: 'type', header: 'Reason', render: (f) => followUpTypeLabel[f.type] },
                {
                  key: 'due',
                  header: 'Due',
                  render: (f) => (
                    <Text style={{ color: f.isOverdue ? tokens.colorPaletteRedForeground1 : undefined }}>
                      {f.isOverdue ? 'Overdue' : 'Today'}
                    </Text>
                  ),
                },
              ]}
              emptyTitle="No calls due"
              emptyDescription="Discharge calls are added automatically two days after discharge."
            />
          </Section>
        )}
        {canReferrals && (
          <Section
            title="Top referring doctors this month"
            actions={
              <Button
                size="small"
                appearance="subtle"
                icon={<StethoscopeRegular />}
                onClick={() => void navigate({ to: '/crm/referrals' })}
              >
                All doctors
              </Button>
            }
          >
            <DataTable
              ariaLabel="Top referring doctors this month"
              rows={referrers.data ? topReferrers : undefined}
              loading={referrers.isLoading}
              error={referrers.error}
              onRetry={() => void referrers.refetch()}
              getRowId={(d) => d.id}
              onRowClick={(d) => void navigate({ to: '/crm/referrals/$doctorId', params: { doctorId: d.id } })}
              columns={[
                {
                  key: 'name',
                  header: 'Doctor',
                  render: (d) => (
                    <span>
                      <Text weight="semibold">{d.name}</Text>
                      <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                        {[d.clinic, d.city].filter(Boolean).join(' · ')}
                      </Caption1>
                    </span>
                  ),
                },
                {
                  key: 'referrals',
                  header: 'Referrals',
                  align: 'end',
                  render: (d) => d.referrals.toLocaleString('en-IN'),
                },
              ]}
              emptyTitle="No referrals yet this month"
              emptyDescription="Visits whose 'Referred by' matches a registered doctor are counted here."
            />
          </Section>
        )}
      </Columns>

      {addingLead && <LeadDrawer lead={null} onClose={() => setAddingLead(false)} />}
      <FollowUpCreateDialog open={addingFollowUp} onClose={() => setAddingFollowUp(false)} />
    </Page>
  );
}
