import { Button, Tab, TabList, Text } from '@fluentui/react-components';
import { CalendarAddRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatTime } from '@hc360/core';
import { DataTable, EmptyState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { dialysisQueries, type DialysisProfileDto } from '@/modules/dialysis/api';
import { SeroBadge, SessionStatusBadge } from '@/modules/dialysis/DialysisBadges';
import { ScheduleSessionDialog } from '@/modules/dialysis/DialysisDialogs';
import { DialyzersPanel } from '@/modules/dialysis/DialyzersPanel';
import { accessLabel, reuseLabel, seroLabel, shiftLabel, weekdayShort } from '@/modules/dialysis/labels';
import { ProfileForm } from '@/modules/dialysis/ProfileForm';
import { Detail, DetailGrid, NoAccess, RecordLoadError } from '@/modules/ot/shared/feedback';
import { SpecialtyPatientBanner } from '@/modules/ot/shared/SpecialtyPatientBanner';

type TabId = 'profile' | 'dialyzers' | 'sessions';

export const Route = createFileRoute('/_app/dialysis/patients/$patientId')({
  validateSearch: (s: Record<string, unknown>): { tab?: Exclude<TabId, 'profile'> } => ({
    tab: s.tab === 'dialyzers' || s.tab === 'sessions' ? s.tab : undefined,
  }),
  component: DialysisPatientPage,
});

function DialysisPatientPage() {
  const { patientId } = Route.useParams();
  const { tab = 'profile' } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRead = usePermission('dialysis.read');
  const canEdit = usePermission('dialysis.patients.manage');
  const canSessions = usePermission('dialysis.sessions.manage');
  const profile = useQuery({ ...dialysisQueries.profile(patientId), enabled: canRead });
  const [booking, setBooking] = useState(false);

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="dialysis patients" />
      </Page>
    );
  }
  if (profile.isLoading) {
    return <LoadingState label="Loading the dialysis profile…" />;
  }
  if (profile.error) {
    return (
      <Page maxWidth={900}>
        <RecordLoadError error={profile.error} onRetry={() => void profile.refetch()} patientId={patientId} what="dialysis profile" />
      </Page>
    );
  }

  const p = profile.data ?? null;
  const breadcrumbs = [
    { label: 'Dialysis board', onClick: () => void navigate({ to: '/dialysis' }) },
    { label: 'Patients', onClick: () => void navigate({ to: '/dialysis/patients' }) },
    { label: p?.patientName ?? 'Enrol' },
  ];

  if (!p) {
    return (
      <Page maxWidth={1100}>
        <SpecialtyPatientBanner patientId={patientId} fallback={{ uhid: '', fullName: 'Patient' }} />
        <PageHeader title="Enrol in the dialysis unit" subtitle="Dry weight, weekly schedule, access and sero status." breadcrumbs={breadcrumbs} />
        {canEdit ? (
          <ProfileForm patientId={patientId} profile={null} />
        ) : (
          <EmptyState title="Not enrolled in the dialysis unit" description="A doctor, dialysis nurse or technician can enrol the patient." />
        )}
      </Page>
    );
  }

  return (
    <Page maxWidth={1200}>
      <SpecialtyPatientBanner
        patientId={patientId}
        fallback={{ uhid: p.uhid, fullName: p.patientName, gender: p.gender, age: p.age }}
        alerts={p.seroStatus !== 'Negative' ? [seroLabel[p.seroStatus]] : undefined}
      />
      <PageHeader
        title="Dialysis profile"
        subtitle={`Enrolled ${formatDateOnly(p.enrolledOn)} · ${p.scheduleDays.map((d) => weekdayShort[d]).join(', ')} · ${shiftLabel[p.shift]} shift${p.isActive ? '' : ' · inactive'}`}
        breadcrumbs={breadcrumbs}
        actions={
          <>
            <SeroBadge sero={p.seroStatus} long size="large" />
            {canSessions && p.isActive && (
              <Button appearance="primary" icon={<CalendarAddRegular />} onClick={() => setBooking(true)}>
                Book session
              </Button>
            )}
          </>
        }
      />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList selectedValue={tab} onTabSelect={(_, d) => void navigate({ search: { tab: d.value === 'profile' ? undefined : (d.value as Exclude<TabId, 'profile'>) }, replace: true })}>
            <Tab value="profile">Profile</Tab>
            <Tab value="dialyzers">Dialyzers</Tab>
            <Tab value="sessions">Sessions</Tab>
          </TabList>
        </div>
        <div style={{ padding: 16 }}>
          {tab === 'profile' && (canEdit ? <ProfileForm patientId={patientId} profile={p} /> : <ProfileView profile={p} />)}
          {tab === 'dialyzers' && <DialyzersPanel patientId={patientId} profile={p} canManage={canSessions} />}
          {tab === 'sessions' && <PatientSessions patientId={patientId} />}
        </div>
      </Section>
      {booking && <ScheduleSessionDialog onClose={() => setBooking(false)} preset={{ patient: { id: p.patientId, uhid: p.uhid, fullName: p.patientName }, shift: p.shift }} />}
    </Page>
  );
}

function ProfileView({ profile: p }: { profile: DialysisProfileDto }) {
  return (
    <DetailGrid>
      <Detail label="Sero status">{`${seroLabel[p.seroStatus]}${p.seroTestedOn ? ` (tested ${formatDateOnly(p.seroTestedOn)})` : ''}${p.seroRescreeningDue ? ' · re-screening due' : ''}`}</Detail>
      <Detail label="Dry weight">{`${p.dryWeightKg} kg`}</Detail>
      <Detail label="Schedule">{`${p.scheduleDays.map((d) => weekdayShort[d]).join(', ')} · ${shiftLabel[p.shift]} · ${p.sessionMinutes} min`}</Detail>
      <Detail label="Access">{`${accessLabel[p.vascularAccess]}${p.accessSite ? ` · ${p.accessSite}` : ''}`}</Detail>
      <Detail label="Access created">{p.accessCreatedOn ? formatDateOnly(p.accessCreatedOn) : null}</Detail>
      <Detail label="Dialyzer reuse">{`${reuseLabel[p.reusePolicy]} · ${p.effectiveMaxDialyzerUses === 1 ? 'single use' : `up to ${p.effectiveMaxDialyzerUses} uses`}`}</Detail>
      <Detail label="Nephrologist">{p.nephrologistName}</Detail>
      <Detail label="Primary diagnosis">{p.primaryDiagnosis}</Detail>
      <Detail label="Anticoagulation">{p.anticoagulationNotes}</Detail>
    </DetailGrid>
  );
}

function PatientSessions({ patientId }: { patientId: string }) {
  const navigate = useNavigate();
  const [page, setPage] = useState(1);
  const sessions = useQuery(dialysisQueries.sessions({ patientId, page }));
  return (
    <DataTable
      ariaLabel="Dialysis sessions of this patient"
      rows={sessions.data?.items}
      loading={sessions.isLoading}
      error={sessions.error}
      onRetry={() => void sessions.refetch()}
      getRowId={(s) => s.id}
      onRowClick={(s) => void navigate({ to: '/dialysis/sessions/$id', params: { id: s.id } })}
      columns={[
        { key: 'date', header: 'Date', render: (s) => formatDateOnly(s.scheduledDate) },
        { key: 'shift', header: 'Shift', render: (s) => shiftLabel[s.shift] },
        { key: 'machine', header: 'Machine', render: (s) => s.machineCode ?? '—' },
        { key: 'time', header: 'On / off', priority: 'low', render: (s) => (s.startedAt ? `${formatTime(s.startedAt)} – ${s.endedAt ? formatTime(s.endedAt) : 'running'}` : '—') },
        { key: 'status', header: 'Status', render: (s) => <SessionStatusBadge status={s.status} /> },
        { key: 'number', header: 'Session', priority: 'low', render: (s) => <Text font="monospace">{s.sessionNumber}</Text> },
      ]}
      emptyTitle="No sessions yet"
      page={page}
      pageSize={25}
      totalCount={sessions.data?.totalCount}
      onPageChange={setPage}
      size="small"
    />
  );
}
