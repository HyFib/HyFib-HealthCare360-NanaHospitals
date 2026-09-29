import { Body1, Button, Caption1, makeStyles, MessageBar, MessageBarBody, tokens } from '@fluentui/react-components';
import { ArrowUndoRegular, CalendarClockRegular, DismissCircleRegular, MegaphoneRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, humanize } from '@hc360/core';
import { ConfirmDialog, ErrorState, KpiCard, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { crmApi, crmKeys, crmQueries, type CampaignDetail, type SegmentInput } from '@/modules/crm/api';
import { CampaignEditor } from '@/modules/crm/CampaignEditor';
import { CAMPAIGN_VIEWERS, campaignChannelLabel, CRM_PERMISSIONS as C } from '@/modules/crm/labels';
import { campaignActions, campaignTone, percent } from '@/modules/crm/rules';
import { ScheduleCampaignDialog } from '@/modules/crm/ScheduleCampaignDialog';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { KpiGrid, MainAside, Stack } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';

export const Route = createFileRoute('/_app/crm/campaigns/$campaignId')({
  component: () => (
    <RequirePermission permission={CAMPAIGN_VIEWERS} what="campaigns">
      <CampaignPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  bubble: {
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground3,
    maxWidth: '520px',
  },
});

function segmentSummary(segment: SegmentInput, departmentName: (id: string) => string): string[] {
  const lines: string[] = [];
  if (segment.minAge !== null || segment.maxAge !== null) {
    lines.push(
      segment.minAge !== null && segment.maxAge !== null
        ? `Aged ${segment.minAge} to ${segment.maxAge}`
        : segment.minAge !== null
          ? `Aged ${segment.minAge} or more`
          : `Aged up to ${segment.maxAge}`,
    );
  }
  if (segment.gender && segment.gender !== 'Unknown') lines.push(humanize(segment.gender));
  if (segment.city) lines.push(`Living in ${segment.city}`);
  if (segment.lastVisitFrom || segment.lastVisitTo) {
    lines.push(
      `Last visit ${segment.lastVisitFrom ? `from ${formatDateOnly(segment.lastVisitFrom)}` : ''}${segment.lastVisitFrom && segment.lastVisitTo ? ' ' : ''}${segment.lastVisitTo ? `to ${formatDateOnly(segment.lastVisitTo)}` : ''}`,
    );
  }
  if (segment.departmentIds && segment.departmentIds.length > 0) lines.push(`Seen in ${segment.departmentIds.map(departmentName).join(', ')}`);
  return lines.length > 0 ? lines : ['Every patient with a marketing consent'];
}

function CampaignPage() {
  const { campaignId } = Route.useParams();
  const navigate = useNavigate();
  const campaign = useQuery(crmQueries.campaign(campaignId));
  const crumbs = [
    { label: 'Patient relations', onClick: () => void navigate({ to: '/crm' }) },
    { label: 'Campaigns', onClick: () => void navigate({ to: '/crm/campaigns' }) },
  ];
  if (!campaign.data) {
    return (
      <Page>
        <PageHeader title="Campaign" icon={<MegaphoneRegular />} breadcrumbs={crumbs} />
        {campaign.error ? <ErrorState error={campaign.error} onRetry={() => void campaign.refetch()} /> : <LoadingState />}
      </Page>
    );
  }
  return <CampaignView campaign={campaign.data} crumbs={crumbs} />;
}

function CampaignView({ campaign: c, crumbs }: { campaign: CampaignDetail; crumbs: { label: string; onClick: () => void }[] }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments('Clinical');
  const canManage = usePermission(C.campaignsManage);
  const canSend = usePermission(C.campaignsSend);
  const actions = campaignActions(c.status, { manage: canManage, send: canSend });
  const [scheduling, setScheduling] = useState(false);
  const [unscheduling, setUnscheduling] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [busy, setBusy] = useState(false);
  const started = c.status === 'Sending' || c.status === 'Completed' || (c.status === 'Cancelled' && c.startedAt !== null);
  const delivery = useQuery({ ...crmQueries.delivery(c.id, c.status === 'Sending'), enabled: started });
  const departmentName = (id: string) => departments.data?.find((d) => d.id === id)?.name ?? 'a department';

  const run = async (action: () => Promise<void>, done: string, after: () => void) => {
    setBusy(true);
    try {
      await action();
      notify.success(done);
      after();
      void queryClient.invalidateQueries({ queryKey: crmKeys.campaigns });
    } catch (error) {
      notify.error(error, 'Could not change the campaign');
      void queryClient.invalidateQueries({ queryKey: crmKeys.campaigns });
    } finally {
      setBusy(false);
    }
  };

  const editing = actions.includes('edit');

  return (
    <Page>
      <PageHeader
        title={
          <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            {c.name}
            <StatusBadge status={c.status} tone={campaignTone(c.status)} />
          </span>
        }
        subtitle={`${campaignChannelLabel[c.channel]}${c.scheduledAt ? ` · sends ${formatDateTime(c.scheduledAt)}` : ''}`}
        icon={<MegaphoneRegular />}
        breadcrumbs={crumbs}
        actions={
          <>
            {actions.includes('schedule') && (
              <Button appearance="primary" icon={<CalendarClockRegular />} onClick={() => setScheduling(true)} disabled={busy}>
                Schedule…
              </Button>
            )}
            {actions.includes('unschedule') && (
              <Button icon={<ArrowUndoRegular />} onClick={() => setUnscheduling(true)} disabled={busy}>
                Back to draft
              </Button>
            )}
            {actions.includes('cancel') && (
              <Button icon={<DismissCircleRegular />} onClick={() => setCancelling(true)} disabled={busy}>
                Cancel campaign
              </Button>
            )}
          </>
        }
      />

      {c.status === 'Scheduled' && (
        <MessageBar intent="info" style={{ marginBottom: 12 }}>
          <MessageBarBody>
            Scheduled for {c.scheduledAt ? formatDateTime(c.scheduledAt) : 'later'}. To change the message or audience, take it back to draft first.
          </MessageBarBody>
        </MessageBar>
      )}
      {c.status === 'Cancelled' && c.cancelReason && (
        <MessageBar intent="warning" style={{ marginBottom: 12 }}>
          <MessageBarBody>Cancelled: {c.cancelReason}</MessageBarBody>
        </MessageBar>
      )}

      {editing ? (
        <CampaignEditor key={c.id} campaign={c} onSaved={() => undefined} />
      ) : (
        <MainAside>
          <Stack>
            <Section title="Message">
              <Body1 className={styles.bubble}>{c.message}</Body1>
              <Caption1 block style={{ marginTop: 8 }}>
                Sent with the hospital name and a personal opt-out link.
              </Caption1>
            </Section>
            <Section title="Who receives it">
              <ul style={{ margin: 0, paddingLeft: 20 }}>
                {segmentSummary(c.segment, departmentName).map((line) => (
                  <li key={line}>
                    <Body1>{line}</Body1>
                  </li>
                ))}
              </ul>
              <Caption1 block style={{ marginTop: 8 }}>
                Only patients with an active marketing consent, checked again when each message is sent.
              </Caption1>
            </Section>
          </Stack>
          <Section title="Sending">
            <DetailGrid minColumnWidth={150}>
              <Detail label="Audience">{c.audienceCount.toLocaleString('en-IN')}</Detail>
              <Detail label="Queued">{c.queuedCount.toLocaleString('en-IN')}</Detail>
              <Detail label="Skipped">{c.skippedCount.toLocaleString('en-IN')}</Detail>
              <Detail label="Scheduled">{c.scheduledAt ? formatDateTime(c.scheduledAt) : null}</Detail>
              <Detail label="Started">{c.startedAt ? formatDateTime(c.startedAt) : null}</Detail>
              <Detail label={c.status === 'Cancelled' ? 'Cancelled' : 'Finished'}>{c.completedAt ? formatDateTime(c.completedAt) : null}</Detail>
            </DetailGrid>
          </Section>
        </MainAside>
      )}

      {started && (
        <Section title="Delivery" description={c.status === 'Sending' ? 'Updates every 30 seconds while sending' : undefined}>
          {delivery.error ? (
            <ErrorState error={delivery.error} onRetry={() => void delivery.refetch()} />
          ) : !delivery.data ? (
            <LoadingState />
          ) : (
            <KpiGrid>
              <KpiCard label="Recipients" value={delivery.data.recipients.toLocaleString('en-IN')} />
              <KpiCard label="Delivered to the gateway" value={delivery.data.sent.toLocaleString('en-IN')} hint={percent(delivery.data.sent, delivery.data.recipients)} />
              <KpiCard label="Waiting" value={delivery.data.queued.toLocaleString('en-IN')} hint="Queued for the next sending window" />
              <KpiCard label="Failed" value={delivery.data.failed.toLocaleString('en-IN')} hint={percent(delivery.data.failed, delivery.data.recipients)} />
              <KpiCard
                label="Skipped"
                value={delivery.data.skipped.toLocaleString('en-IN')}
                hint="No consent or no mobile number at send time, or the number replied STOP on WhatsApp"
              />
              <KpiCard label="Opted out since" value={delivery.data.optedOut.toLocaleString('en-IN')} hint="Used the link in the message" />
            </KpiGrid>
          )}
        </Section>
      )}

      <ScheduleCampaignDialog open={scheduling} onClose={() => setScheduling(false)} campaign={c} />
      <ConfirmDialog
        open={unscheduling}
        title="Take the campaign back to draft?"
        confirmLabel="Back to draft"
        busy={busy}
        onConfirm={() => void run(() => crmApi.unscheduleCampaign(c.id), 'Back to draft', () => setUnscheduling(false))}
        onCancel={() => setUnscheduling(false)}
      >
        It will not be sent until someone schedules it again.
      </ConfirmDialog>
      <ReasonDialog
        open={cancelling}
        title="Cancel the campaign"
        description={c.status === 'Sending' ? 'Messages already handed to the gateway are still delivered. The rest are not sent.' : 'Nothing will be sent.'}
        label="Reason"
        maxLength={300}
        confirmLabel="Cancel campaign"
        destructive
        busy={busy}
        onConfirm={(reason) => void run(() => crmApi.cancelCampaign(c.id, reason), 'Campaign cancelled', () => setCancelling(false))}
        onCancel={() => setCancelling(false)}
      />
    </Page>
  );
}
