import {
  Body1,
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowMoveRegular,
  BuildingRegular,
  CallRegular,
  ChatRegular,
  EditRegular,
  MailRegular,
  NoteRegular,
  NotepadRegular,
  PersonArrowRightRegular,
  TargetArrowRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatPhone, humanize } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { crmApi, crmKeys, crmQueries, type LeadActivityDto, type LeadDetail } from '@/modules/crm/api';
import { ConvertLeadDialog } from '@/modules/crm/ConvertLeadDialog';
import {
  CRM_PERMISSIONS as C,
  leadActivityLabel,
  leadSourceLabel,
  leadStatusLabel,
  type LeadStatus,
} from '@/modules/crm/labels';
import { LeadActivityDialog } from '@/modules/crm/LeadActivityDialog';
import { LeadDrawer } from '@/modules/crm/LeadDrawer';
import { Patient360Link } from '@/modules/crm/Patient360Link';
import { isLeadOpen, leadMoves, leadTone } from '@/modules/crm/rules';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { MainAside, Stack } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { UserName } from '@/modules/hr/shared/UserPicker';

export const Route = createFileRoute('/_app/crm/leads/$leadId')({
  component: () => (
    <RequirePermission permission={C.leadsManage} what="leads">
      <LeadPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  timeline: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column' },
  item: {
    display: 'grid',
    gridTemplateColumns: '32px 1fr',
    gap: '10px',
    padding: '10px 0',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': { borderBottom: 'none' },
  },
  icon: {
    width: '32px',
    height: '32px',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground2,
    fontSize: '16px',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  text: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
});

function activityIcon(activity: LeadActivityDto) {
  switch (activity.type) {
    case 'Call':
      return <CallRegular />;
    case 'Sms':
    case 'WhatsApp':
      return <ChatRegular />;
    case 'Email':
      return <MailRegular />;
    case 'Visit':
      return <BuildingRegular />;
    default:
      return <NoteRegular />;
  }
}

function LeadPage() {
  const { leadId } = Route.useParams();
  const navigate = useNavigate();
  const lead = useQuery(crmQueries.lead(leadId));
  const crumbs = [
    { label: 'Patient relations', onClick: () => void navigate({ to: '/crm' }) },
    { label: 'Leads', onClick: () => void navigate({ to: '/crm/leads' }) },
  ];
  if (!lead.data) {
    return (
      <Page>
        <PageHeader title="Lead" icon={<TargetArrowRegular />} breadcrumbs={crumbs} />
        {lead.error ? <ErrorState error={lead.error} onRetry={() => void lead.refetch()} /> : <LoadingState />}
      </Page>
    );
  }
  return <LeadView lead={lead.data} crumbs={crumbs} />;
}

function LeadView({ lead, crumbs }: { lead: LeadDetail; crumbs: { label: string; onClick: () => void }[] }) {
  const styles = useStyles();
  // Closing the dialog opened from the stage menu returns focus to its button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments('Clinical');
  const canReferrals = usePermission(C.referralsManage);
  const doctor = useQuery({
    ...crmQueries.referringDoctor(lead.referringDoctorId ?? ''),
    enabled: Boolean(lead.referringDoctorId) && canReferrals,
  });
  const [editing, setEditing] = useState(false);
  const [logging, setLogging] = useState(false);
  const [converting, setConverting] = useState(false);
  const [losing, setLosing] = useState(false);
  const [busy, setBusy] = useState(false);

  const department = lead.interestDepartmentId
    ? (departments.data?.find((d) => d.id === lead.interestDepartmentId)?.name ?? 'Department')
    : null;
  const open = isLeadOpen(lead.status);

  const move = async (to: LeadStatus, lostReason: string | null = null) => {
    setBusy(true);
    try {
      await crmApi.changeLeadStatus(lead.id, to, lostReason);
      notify.success(`Moved to ${leadStatusLabel[to]}`);
      setLosing(false);
      void queryClient.invalidateQueries({ queryKey: crmKeys.leads });
    } catch (error) {
      notify.error(error, 'Could not change the stage');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Page>
      <PageHeader
        title={
          <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            {lead.name}
            <StatusBadge status={lead.status} tone={leadTone(lead.status)} label={leadStatusLabel[lead.status]} />
          </span>
        }
        subtitle={`${lead.leadNumber} · ${leadSourceLabel[lead.source]}${lead.sourceDetail ? ` (${lead.sourceDetail})` : ''}`}
        icon={<TargetArrowRegular />}
        breadcrumbs={crumbs}
        actions={
          <>
            {lead.status !== 'Converted' && (
              <Button icon={<NotepadRegular />} onClick={() => setLogging(true)}>
                Log a call or note
              </Button>
            )}
            {leadMoves(lead.status).length > 0 && (
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button
                    icon={<ArrowMoveRegular />}
                    disabled={busy}
                    // Stays focusable while the lead moves, so focus is not dropped to the page.
                    disabledFocusable={busy}
                    {...restoreFocusTarget}
                  >
                    Change stage
                  </Button>
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    {leadMoves(lead.status).map((to) => (
                      <MenuItem key={to} onClick={() => (to === 'Lost' ? setLosing(true) : void move(to))}>
                        {to === 'Lost'
                          ? 'Mark as lost…'
                          : lead.status === 'Lost'
                            ? `Reopen as ${leadStatusLabel[to]}`
                            : `Move to ${leadStatusLabel[to]}`}
                      </MenuItem>
                    ))}
                  </MenuList>
                </MenuPopover>
              </Menu>
            )}
            {lead.status !== 'Converted' && (
              <Button icon={<EditRegular />} onClick={() => setEditing(true)}>
                Edit
              </Button>
            )}
            {open && (
              <Button appearance="primary" icon={<PersonArrowRightRegular />} onClick={() => setConverting(true)}>
                Convert to patient
              </Button>
            )}
          </>
        }
      />

      {lead.status === 'Lost' && lead.lostReason && (
        <MessageBar intent="warning" style={{ marginBottom: 12 }}>
          <MessageBarBody>Lost: {lead.lostReason}</MessageBarBody>
        </MessageBar>
      )}
      {lead.status === 'Converted' && (
        <MessageBar intent="success" style={{ marginBottom: 12 }}>
          <MessageBarBody>
            Converted {lead.convertedAt ? formatDateTime(lead.convertedAt) : ''}
            {lead.convertedAppointmentId ? ' with an appointment booked' : ''}.{' '}
            {lead.convertedPatientId && (
              <Patient360Link patientId={lead.convertedPatientId} size="small" appearance="transparent" />
            )}
          </MessageBarBody>
        </MessageBar>
      )}

      <MainAside>
        <Section title="Calls, messages and notes" description="Newest first">
          {lead.activities.length === 0 ? (
            <Caption1 className={styles.muted}>
              Nothing recorded yet. Log the first call to move the lead to Contacted.
            </Caption1>
          ) : (
            <ol className={styles.timeline}>
              {lead.activities.map((a) => (
                <li key={a.id} className={styles.item}>
                  <span className={styles.icon} aria-hidden>
                    {activityIcon(a)}
                  </span>
                  <span>
                    <Text weight="semibold">{leadActivityLabel[a.type]}</Text>
                    {a.outcome && <Text> · {a.outcome}</Text>}
                    <Caption1 block className={styles.muted}>
                      {formatDateTime(a.occurredAt)}
                      {a.performedByUserId && (
                        <>
                          {' · '}
                          <UserName id={a.performedByUserId} />
                        </>
                      )}
                    </Caption1>
                    {a.notes && (
                      <Body1 block className={styles.text}>
                        {a.notes}
                      </Body1>
                    )}
                  </span>
                </li>
              ))}
            </ol>
          )}
        </Section>
        <Stack>
          <Section title="Contact">
            <DetailGrid minColumnWidth={160}>
              <Detail label="Mobile">{lead.phone ? formatPhone(lead.phone) : null}</Detail>
              <Detail label="Email">{lead.email}</Detail>
              <Detail label="Gender">{lead.gender && lead.gender !== 'Unknown' ? humanize(lead.gender) : null}</Detail>
              <Detail label="Age">{lead.ageYears === null ? null : `${lead.ageYears} years`}</Detail>
              <Detail label="City / area">{lead.city}</Detail>
            </DetailGrid>
          </Section>
          <Section title="Enquiry">
            <DetailGrid minColumnWidth={160}>
              <Detail label="Interested in">{lead.interestPackage}</Detail>
              <Detail label="Department">{department}</Detail>
              <Detail label="Referred by">
                {lead.referringDoctorId ? (doctor.data?.name ?? 'A registered doctor') : null}
              </Detail>
              <Detail label="Owner">{lead.ownerUserId ? <UserName id={lead.ownerUserId} /> : 'Not assigned'}</Detail>
              <Detail label="Next call">{lead.nextFollowUpAt ? formatDateTime(lead.nextFollowUpAt) : null}</Detail>
              <Detail label="Last contacted">
                {lead.lastContactedAt ? formatDateTime(lead.lastContactedAt) : null}
              </Detail>
              {lead.notes && (
                <Detail label="Notes" wide>
                  <span className={styles.text}>{lead.notes}</span>
                </Detail>
              )}
            </DetailGrid>
          </Section>
        </Stack>
      </MainAside>

      {editing && <LeadDrawer lead={lead} onClose={() => setEditing(false)} />}
      <LeadActivityDialog open={logging} onClose={() => setLogging(false)} leadId={lead.id} leadName={lead.name} />
      <ConvertLeadDialog open={converting} onClose={() => setConverting(false)} lead={lead} />
      <ReasonDialog
        open={losing}
        title={`Mark ${lead.name} as lost`}
        description="The reason helps improve campaigns and follow-up. A lost lead can be reopened later."
        label="Why was it lost?"
        hint="For example: chose another hospital, too expensive, not reachable after 3 calls"
        maxLength={300}
        confirmLabel="Mark as lost"
        destructive
        busy={busy}
        onConfirm={(reason) => void move('Lost', reason)}
        onCancel={() => setLosing(false)}
      />
    </Page>
  );
}
