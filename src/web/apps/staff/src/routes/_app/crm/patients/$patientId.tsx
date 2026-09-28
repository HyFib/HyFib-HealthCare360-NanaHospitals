import { Badge, Button, Caption1, Link, MessageBar, MessageBarBody, MessageBarTitle, Text, tokens } from '@fluentui/react-components';
import {
  CallRegular,
  ChatMultipleRegular,
  DocumentRegular,
  MoneyRegular,
  PersonFeedbackRegular,
  PersonHeartRegular,
  PersonSubtractRegular,
  StethoscopeRegular,
} from '@fluentui/react-icons';
import { formatDate, formatDateOnly, formatInr, humanize, isApiError } from '@hc360/core';
import { DataTable, ErrorState, KpiCard, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { crmApi, crmKeys, crmQueries, type Patient360View } from '@/modules/crm/api';
import { FollowUpCreateDialog } from '@/modules/crm/FollowUpCreateDialog';
import { CRM_PERMISSIONS as C, followUpStatusLabel, followUpTypeLabel, leadSourceLabel, OPT_OUT_RECORDERS } from '@/modules/crm/labels';
import { SentimentBadge, StarsValue } from '@/modules/feedback/badges';
import { ComplaintDialog } from '@/modules/feedback/ComplaintDialog';
import { categoryLabel, complaintStatusLabel, FEEDBACK_CAPTURERS, FEEDBACK_PERMISSIONS as F } from '@/modules/feedback/labels';
import { StaffFeedbackDialog } from '@/modules/feedback/StaffFeedbackDialog';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { Columns, KpiGrid } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';

export const Route = createFileRoute('/_app/crm/patients/$patientId')({
  component: () => (
    <RequirePermission permission={C.patient360Read} what="the Patient 360 view">
      <Patient360Page />
    </RequirePermission>
  ),
});

function Patient360Page() {
  const { patientId } = Route.useParams();
  const navigate = useNavigate();
  const view = useQuery(crmQueries.patient360(patientId));
  const crumbs = [
    { label: 'Patient relations', onClick: () => void navigate({ to: '/crm' }) },
    { label: 'Patient 360', onClick: () => void navigate({ to: '/crm/patients' }) },
  ];

  if (!view.data) {
    const restricted = isApiError(view.error) && (view.error.code === 'patient.restricted' || view.error.status === 403);
    return (
      <Page>
        <PageHeader title="Patient 360" icon={<PersonHeartRegular />} breadcrumbs={crumbs} />
        {restricted ? (
          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>This record is restricted</MessageBarTitle>
              The patient's record is protected (for example a VIP). The CRM view is not available without emergency access, which only clinical staff
              should request from the patient record.
            </MessageBarBody>
          </MessageBar>
        ) : view.error ? (
          <ErrorState error={view.error} onRetry={() => void view.refetch()} />
        ) : (
          <LoadingState />
        )}
      </Page>
    );
  }
  return <View v={view.data} crumbs={crumbs} />;
}

function View({ v, crumbs }: { v: Patient360View; crumbs: { label: string; onClick: () => void }[] }) {
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canOptOut = usePermission(OPT_OUT_RECORDERS);
  const canFollowUps = usePermission(C.followUpsManage);
  const canLeads = usePermission(C.leadsManage);
  const canCapture = usePermission(FEEDBACK_CAPTURERS);
  const canComplaints = usePermission(F.complaintsManage);
  const canPatientRecord = usePermission('patients.read');
  const [optingOut, setOptingOut] = useState(false);
  const [busy, setBusy] = useState(false);
  const [dialog, setDialog] = useState<'followUp' | 'feedback' | 'complaint' | null>(null);
  const p = v.patient;
  const picked = { id: p.id, uhid: p.uhid, fullName: p.fullName };

  const optOut = async (reason: string) => {
    setBusy(true);
    try {
      await crmApi.marketingOptOut(p.id, reason);
      notify.success('Marketing messages stopped', `${p.fullName} will not receive campaigns. Service messages (appointments, reports) continue.`);
      setOptingOut(false);
      void queryClient.invalidateQueries({ queryKey: crmKeys.patient360 });
    } catch (error) {
      notify.error(error, 'Could not record the opt-out');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Page>
      <PageHeader
        title={
          <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            {p.fullName}
            {p.isVip && (
              <Badge color="important" appearance="tint">
                VIP
              </Badge>
            )}
            {p.isDeceased && (
              <Badge color="informative" appearance="filled">
                Deceased
              </Badge>
            )}
          </span>
        }
        subtitle={[p.uhid, [humanize(p.gender), p.age].filter(Boolean).join(', '), humanize(p.category), p.city, p.phoneMasked].filter(Boolean).join(' · ')}
        icon={<PersonHeartRegular />}
        breadcrumbs={crumbs}
        actions={
          <>
            {canFollowUps && !p.isDeceased && (
              <Button icon={<CallRegular />} onClick={() => setDialog('followUp')}>
                Add follow-up call
              </Button>
            )}
            {canCapture && !p.isDeceased && (
              <Button icon={<PersonFeedbackRegular />} onClick={() => setDialog('feedback')}>
                Record feedback
              </Button>
            )}
            {canComplaints && (
              <Button icon={<ChatMultipleRegular />} onClick={() => setDialog('complaint')}>
                Record complaint
              </Button>
            )}
            {canPatientRecord && (
              <Button icon={<DocumentRegular />} onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: p.id } })}>
                Patient record
              </Button>
            )}
          </>
        }
      />

      <KpiGrid>
        <KpiCard
          label="Visits"
          value={v.visits.total.toLocaleString('en-IN')}
          hint={`${v.visits.lastTwelveMonths} in the last 12 months`}
          icon={<StethoscopeRegular />}
        />
        <KpiCard label="Billed" value={formatInr(v.spend.totalBilled, { whole: true })} hint={`${formatInr(v.spend.billedLastTwelveMonths, { whole: true })} in the last 12 months`} icon={<MoneyRegular />} />
        <KpiCard
          label="Feedback"
          value={v.feedback.averageRating === null ? '—' : `${v.feedback.averageRating.toFixed(1)} / 5`}
          hint={`${v.feedback.responses} response(s) · last NPS ${v.feedback.lastNps ?? '—'}`}
          icon={<PersonFeedbackRegular />}
        />
        <KpiCard
          label="Open follow-up calls"
          value={v.openFollowUps.length.toLocaleString('en-IN')}
          hint={v.openFollowUps.some((f) => f.isOverdue) ? 'Some are overdue' : 'None overdue'}
          icon={<CallRegular />}
        />
      </KpiGrid>

      <Columns>
        <Section title="Visits">
          <DetailGrid minColumnWidth={170}>
            <Detail label="Registered">{formatDate(p.registeredAt)}</Detail>
            <Detail label="First visit">{v.visits.firstVisitAt ? formatDate(v.visits.firstVisitAt) : null}</Detail>
            <Detail label="Last visit">{v.visits.lastVisitAt ? formatDate(v.visits.lastVisitAt) : null}</Detail>
            <Detail label="Last referred by">{v.visits.lastReferredBy}</Detail>
            <Detail label="Kinds of visit" wide>
              {v.visits.byType.length > 0 ? v.visits.byType.map((t) => `${humanize(t.label)} ${t.count}`).join(' · ') : null}
            </Detail>
            <Detail label="Departments seen most" wide>
              {v.visits.topDepartments.length > 0 ? v.visits.topDepartments.map((d) => `${d.label} (${d.count})`).join(', ') : null}
            </Detail>
          </DetailGrid>
        </Section>
        <Section title="Spend">
          <DetailGrid minColumnWidth={170}>
            <Detail label="Total billed">{formatInr(v.spend.totalBilled)}</Detail>
            <Detail label="Total paid">{formatInr(v.spend.totalPaid)}</Detail>
            <Detail label="Bills">{v.spend.bills.toLocaleString('en-IN')}</Detail>
            <Detail label="Last bill">{v.spend.lastBilledAt ? formatDate(v.spend.lastBilledAt) : null}</Detail>
          </DetailGrid>
        </Section>
      </Columns>

      <Columns>
        <Section title="Feedback">
          <DetailGrid minColumnWidth={170}>
            <Detail label="Responses">{v.feedback.responses.toLocaleString('en-IN')}</Detail>
            <Detail label="Average rating">
              <StarsValue value={v.feedback.averageRating} />
            </Detail>
            <Detail label="Last recommendation score">{v.feedback.lastNps === null ? null : `${v.feedback.lastNps} of 10`}</Detail>
            <Detail label="Last sentiment">{v.feedback.lastSentiment ? <SentimentBadge sentiment={v.feedback.lastSentiment} /> : null}</Detail>
            <Detail label="Low scores">{v.feedback.lowScores.toLocaleString('en-IN')}</Detail>
            <Detail label="Last feedback">{v.feedback.lastSubmittedAt ? formatDate(v.feedback.lastSubmittedAt) : null}</Detail>
          </DetailGrid>
          {v.feedback.responses > 0 && <Caption1 block style={{ marginTop: 8 }}>Anonymous QR and kiosk answers are never linked to a patient.</Caption1>}
        </Section>
        <Section
          title="Communication"
          actions={
            canOptOut &&
            v.communication.marketingConsent && (
              <Button size="small" icon={<PersonSubtractRegular />} onClick={() => setOptingOut(true)}>
                Stop marketing messages
              </Button>
            )
          }
        >
          <DetailGrid minColumnWidth={170}>
            <Detail label="Marketing messages">
              <Text weight="semibold" style={{ color: v.communication.marketingConsent ? tokens.colorPaletteGreenForeground1 : tokens.colorNeutralForeground3 }}>
                {v.communication.marketingConsent ? 'Agreed' : 'Not agreed'}
              </Text>
              {v.communication.optedOutByLink && <Caption1 block>Opted out using the link in a message</Caption1>}
            </Detail>
            <Detail label="Service messages (SMS updates)">{v.communication.updatesConsent ? 'Agreed' : 'Not agreed'}</Detail>
            <Detail label="Marketing choice changed">{v.communication.marketingChangedAt ? formatDate(v.communication.marketingChangedAt) : null}</Detail>
            <Detail label="Campaigns received">{v.communication.campaignsReceived.toLocaleString('en-IN')}</Detail>
            <Detail label="Last campaign">{v.communication.lastCampaignAt ? formatDate(v.communication.lastCampaignAt) : null}</Detail>
          </DetailGrid>
        </Section>
      </Columns>

      <Columns>
        <Section title="Recent complaints">
          <DataTable
            ariaLabel="Recent complaints"
            rows={v.recentComplaints}
            getRowId={(c) => c.id}
            onRowClick={canComplaints ? (c) => void navigate({ to: '/feedback/complaints/$complaintId', params: { complaintId: c.id } }) : undefined}
            columns={[
              { key: 'number', header: 'Complaint', render: (c) => <Text font="monospace">{c.complaintNumber}</Text> },
              { key: 'about', header: 'About', render: (c) => categoryLabel(c.category) },
              { key: 'raised', header: 'Raised', render: (c) => formatDate(c.raisedAt) },
              { key: 'status', header: 'Stage', render: (c) => `${complaintStatusLabel[c.status]}${c.priority === 'High' ? ' · high' : ''}` },
            ]}
            emptyTitle="No complaints"
            emptyDescription="Complaints and low feedback scores linked to this patient appear here."
          />
        </Section>
        <Section title="Open follow-up calls">
          <DataTable
            ariaLabel="Open follow-up calls"
            rows={v.openFollowUps}
            getRowId={(f) => f.id}
            onRowClick={canFollowUps ? () => void navigate({ to: '/crm/follow-ups', search: { view: 'all', search: p.uhid } }) : undefined}
            columns={[
              { key: 'type', header: 'Reason', render: (f) => followUpTypeLabel[f.type] },
              {
                key: 'due',
                header: 'Call on',
                render: (f) => <Text style={{ color: f.isOverdue ? tokens.colorPaletteRedForeground1 : undefined }}>{formatDateOnly(f.dueDate)}</Text>,
              },
              { key: 'status', header: 'Stage', render: (f) => `${followUpStatusLabel[f.status]}${f.priority === 'High' ? ' · high' : ''}` },
            ]}
            emptyTitle="No open calls"
            emptyDescription="Calls after discharge are added automatically."
          />
        </Section>
      </Columns>

      {v.sourceLead && (
        <Section title="Came to us through">
          <Text>
            {leadSourceLabel[v.sourceLead.source]}
            {v.sourceLead.sourceDetail ? ` (${v.sourceLead.sourceDetail})` : ''} · lead{' '}
            {canLeads ? (
              <Link onClick={() => v.sourceLead && void navigate({ to: '/crm/leads/$leadId', params: { leadId: v.sourceLead.id } })}>{v.sourceLead.leadNumber}</Link>
            ) : (
              v.sourceLead.leadNumber
            )}{' '}
            · enquired {formatDate(v.sourceLead.createdAt)}
            {v.sourceLead.convertedAt ? ` · converted ${formatDate(v.sourceLead.convertedAt)}` : ''}
          </Text>
        </Section>
      )}

      <Caption1 block style={{ marginTop: 8 }}>
        This view was recorded in the audit log as access to the patient's record.
      </Caption1>

      <ReasonDialog
        open={optingOut}
        title={`Stop marketing messages to ${p.fullName}`}
        description="Campaign SMS and WhatsApp messages stop at once. Messages about the patient's care (appointments, reports, bills) continue."
        label="How did the patient ask?"
        hint="For example: asked on the phone during a follow-up call"
        maxLength={300}
        confirmLabel="Stop marketing messages"
        destructive
        busy={busy}
        onConfirm={(reason) => void optOut(reason)}
        onCancel={() => setOptingOut(false)}
      />
      <FollowUpCreateDialog open={dialog === 'followUp'} onClose={() => setDialog(null)} patient={picked} />
      <StaffFeedbackDialog open={dialog === 'feedback'} onClose={() => setDialog(null)} patient={picked} />
      <ComplaintDialog
        open={dialog === 'complaint'}
        onClose={() => setDialog(null)}
        patient={picked}
        onCreated={(complaintId) => void navigate({ to: '/feedback/complaints/$complaintId', params: { complaintId } })}
      />
    </Page>
  );
}
