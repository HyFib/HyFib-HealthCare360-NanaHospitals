import {
  Button,
  Caption1,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Tab,
  TabList,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  BedRegular,
  CalendarAddRegular,
  CalendarLtrRegular,
  EditRegular,
  MoreHorizontalRegular,
  PrintRegular,
  StethoscopeRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatPhone, humanize, isApiError, useSession } from '@hc360/core';
import { DataTable, ErrorState, LoadingState, Page, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { PatientBannerWithFlags } from '@/modules/emr/PatientBannerWithFlags';
import { printUhidCard } from '@/lib/print';
import { CheckInDialog } from '@/modules/front-office/CheckInDialog';
import { guardianRelationLabel, patientApi, patientQueries, type PatientDetail } from '@/modules/patients/api';
import { ConsentsPanel } from '@/modules/patients/ConsentsPanel';
import { DocumentsPanel } from '@/modules/patients/DocumentsPanel';
import { IdentityChecksPanel } from '@/modules/patients/IdentityChecksPanel';
import { PortalAccessPanel } from '@/modules/patients/PortalAccessPanel';
import { PatientPrivacyCard } from '@/modules/privacy/PatientPrivacyCard';

type TabId = 'overview' | 'visits' | 'consents' | 'identity' | 'portal' | 'documents';

export const Route = createFileRoute('/_app/patients/$patientId/')({
  validateSearch: (search: Record<string, unknown>): { tab?: TabId } => ({
    tab: ['overview', 'visits', 'consents', 'identity', 'portal', 'documents'].includes(search.tab as string)
      ? (search.tab as TabId)
      : undefined,
  }),
  component: PatientProfilePage,
});

const useStyles = makeStyles({
  details: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))', gap: '16px 24px' },
  item: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  tabs: { marginBottom: '8px' },
  breakGlass: { display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '560px' },
  masked: { gridColumn: '1 / -1', color: tokens.colorNeutralForeground3 },
});

function Detail({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.item}>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text>{children || '—'}</Text>
    </div>
  );
}

function PatientProfilePage() {
  const { patientId } = Route.useParams();
  const { tab = 'overview' } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  // Editing shows and saves the contact details in full, which only those who contact or bill patients see (SRS PRV-03).
  const canRegister = usePermission('patients.register');
  const canReadContact = usePermission('patients.contact.read');
  const canEdit = canRegister && canReadContact;
  const canCheckIn = usePermission('visits.manage');
  const canOpenChart = usePermission('clinical.emr.read');
  const canBook = usePermission('scheduling.appointments.book');
  const canAdmit = usePermission('inpatient.admissions.admit');
  const [checkIn, setCheckIn] = useState(false);
  const patient = useQuery(patientQueries.detail(patientId));

  if (patient.isLoading) {
    return <LoadingState />;
  }

  if (patient.error) {
    return isApiError(patient.error) && patient.error.code === 'patient.restricted' ? (
      <BreakGlass patientId={patientId} onGranted={() => void patient.refetch()} />
    ) : (
      <Page maxWidth={900}>
        <ErrorState error={patient.error} onRetry={() => void patient.refetch()} />
      </Page>
    );
  }

  const p = patient.data;
  if (!p) {
    return null;
  }

  return (
    <Page>
      <PatientBannerWithFlags
        patientId={p.id}
        photoFileId={p.photoFileId}
        patient={{
          id: p.id,
          uhid: p.uhid,
          fullName: p.fullName,
          gender: p.gender,
          age: p.age,
          phone: p.phone,
          bloodGroup: p.bloodGroup,
          category: p.category,
          isVip: p.isVip,
          isDeceased: p.isDeceased,
        }}
        actions={
          <>
            {canCheckIn && !p.isDeceased && (
              <Button appearance="primary" icon={<CalendarAddRegular />} onClick={() => setCheckIn(true)}>
                Check in
              </Button>
            )}
            {canEdit && (
              <Button
                icon={<EditRegular />}
                onClick={() => void navigate({ to: '/patients/$patientId/edit', params: { patientId } })}
              >
                Edit
              </Button>
            )}
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button icon={<MoreHorizontalRegular />} aria-label="More actions" />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {canOpenChart && (
                    <MenuItem
                      icon={<StethoscopeRegular />}
                      onClick={() =>
                        void navigate({ to: '/emr/patients/$patientId', params: { patientId }, search: {} })
                      }
                    >
                      Open clinical chart
                    </MenuItem>
                  )}
                  {canBook && !p.isDeceased && (
                    <MenuItem
                      icon={<CalendarLtrRegular />}
                      onClick={() => void navigate({ to: '/scheduling/book', search: { patientId } })}
                    >
                      Book appointment
                    </MenuItem>
                  )}
                  {canAdmit && !p.isDeceased && (
                    <MenuItem
                      icon={<BedRegular />}
                      onClick={() => void navigate({ to: '/inpatient/admit', search: { patientId } })}
                    >
                      Admit
                    </MenuItem>
                  )}
                  <MenuItem
                    icon={<PrintRegular />}
                    onClick={() =>
                      void printUhidCard({
                        facilityName,
                        uhid: p.uhid,
                        fullName: p.fullName,
                        ageSex: `${p.age ?? '—'} / ${p.gender.charAt(0)}`,
                        phone: p.phone,
                        registeredAt: p.registeredAt,
                      }).catch((error: unknown) => notify.error(error, 'Printing failed'))
                    }
                  >
                    Print UHID card
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          </>
        }
      />

      {p.mergedIntoPatientId && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Merged record</MessageBarTitle>
            This record was merged into another patient.{' '}
            <Button
              appearance="transparent"
              size="small"
              onClick={() =>
                void navigate({ to: '/patients/$patientId', params: { patientId: p.mergedIntoPatientId! } })
              }
            >
              Open the surviving record
            </Button>
          </MessageBarBody>
        </MessageBar>
      )}

      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={tab}
            onTabSelect={(_, d) =>
              void navigate({ search: { tab: d.value === 'overview' ? undefined : (d.value as TabId) }, replace: true })
            }
          >
            <Tab value="overview">Overview</Tab>
            <Tab value="visits">Visits</Tab>
            <Tab value="consents">Consents</Tab>
            <Tab value="identity">Identity</Tab>
            <Tab value="portal">Portal access</Tab>
            <Tab value="documents">Documents</Tab>
          </TabList>
        </div>
        <div style={{ padding: 16 }}>
          {tab === 'overview' && <Overview patient={p} />}
          {tab === 'visits' && <Visits patientId={patientId} />}
          {tab === 'consents' && <ConsentsPanel patientId={patientId} />}
          {tab === 'identity' && <IdentityChecksPanel patient={p} />}
          {tab === 'portal' && <PortalAccessPanel patientId={patientId} />}
          {tab === 'documents' && <DocumentsPanel patientId={patientId} />}
        </div>
      </Section>

      <PatientPrivacyCard patient={p} />

      <CheckInDialog
        patient={{ id: p.id, uhid: p.uhid, fullName: p.fullName }}
        open={checkIn}
        onClose={() => setCheckIn(false)}
      />
    </Page>
  );
}

function Overview({ patient: p }: { patient: PatientDetail }) {
  const styles = useStyles();
  const address = [
    p.address.line1,
    p.address.line2,
    p.address.city,
    p.address.district,
    p.address.state,
    p.address.pinCode,
  ]
    .filter(Boolean)
    .join(', ');
  return (
    <div className={styles.details}>
      <Detail label="Date of birth">
        {p.dateOfBirth ? `${formatDateOnly(p.dateOfBirth)}${p.isDateOfBirthEstimated ? ' (estimated)' : ''}` : null}
      </Detail>
      <Detail label="Marital status">{p.maritalStatus === 'Unknown' ? null : humanize(p.maritalStatus)}</Detail>
      <Detail label={p.guardianRelation ? guardianRelationLabel[p.guardianRelation] : 'Guardian'}>
        {p.guardianName}
      </Detail>
      <Detail label="Mobile">{p.phone ? formatPhone(p.phone) : null}</Detail>
      <Detail label="Alternate phone">{p.alternatePhone ? formatPhone(p.alternatePhone) : null}</Detail>
      <Detail label="Email">{p.email}</Detail>
      <Detail label="Address">{address}</Detail>
      <Detail label="Preferred language">{p.preferredLanguage}</Detail>
      <Detail label="Emergency contact">
        {p.emergencyContactName
          ? `${p.emergencyContactName}${p.emergencyContactRelation ? ` (${p.emergencyContactRelation})` : ''}${p.emergencyContactPhone ? ` · ${formatPhone(p.emergencyContactPhone)}` : ''}`
          : null}
      </Detail>
      <Detail label="ABHA number">{p.abhaNumberMasked}</Detail>
      <Detail label="ABHA address">{p.abhaAddress}</Detail>
      <Detail label="Aadhaar">{p.aadhaarLast4 ? `XXXX XXXX ${p.aadhaarLast4}` : null}</Detail>
      <Detail label="Category">{humanize(p.category)}</Detail>
      <Detail label="Occupation">{p.occupation}</Detail>
      <Detail label="Religion / nationality">{[p.religion, p.nationality].filter(Boolean).join(' / ')}</Detail>
      <Detail label="Registered">{formatDateTime(p.registeredAt)}</Detail>
      {p.notes && <Detail label="Front-desk notes">{p.notes}</Detail>}
      {p.contactsMasked && (
        <Caption1 className={styles.masked}>
          Phone numbers, email and the street address are shown in part: in full they are for the staff who contact or
          bill patients.
        </Caption1>
      )}
    </div>
  );
}

function Visits({ patientId }: { patientId: string }) {
  const [page, setPage] = useState(1);
  const visits = useQuery(patientQueries.visits(patientId, page));
  return (
    <DataTable
      ariaLabel="Visits"
      rows={visits.data?.items}
      loading={visits.isLoading}
      error={visits.error}
      onRetry={() => void visits.refetch()}
      getRowId={(v) => v.id}
      columns={[
        { key: 'date', header: 'Date', render: (v) => formatDateTime(v.visitedAt), width: 170 },
        { key: 'number', header: 'Visit', render: (v) => <Text font="monospace">{v.visitNumber}</Text> },
        { key: 'type', header: 'Type', render: (v) => humanize(v.type) },
        { key: 'dept', header: 'Department', render: (v) => v.departmentName ?? '—' },
        { key: 'doctor', header: 'Doctor', render: (v) => v.practitionerName ?? '—', priority: 'low' },
        { key: 'token', header: 'Token', render: (v) => v.tokenLabel ?? '—', priority: 'low' },
        { key: 'status', header: 'Status', render: (v) => <StatusBadge status={v.status} /> },
      ]}
      emptyTitle="No visits yet"
      page={page}
      pageSize={10}
      totalCount={visits.data?.totalCount}
      onPageChange={setPage}
      size="small"
    />
  );
}

/** Restricted (VIP) record: emergency access needs a reason, is time-limited and alerts the privacy officer. */
function BreakGlass({ patientId, onGranted }: { patientId: string; onGranted: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const canBreakGlass = usePermission('patients.break_glass');
  const [reason, setReason] = useState('');
  const request = useMutation({
    mutationFn: () => patientApi.breakGlass(patientId, reason),
    onSuccess: (grant) => {
      notify.warning('Emergency access granted', `Until ${formatDateTime(grant.expiresAt)}. This access is recorded.`);
      onGranted();
    },
    onError: (error) => notify.error(error, 'Emergency access was not granted'),
  });

  return (
    <Page maxWidth={720}>
      <Section title="Restricted record">
        <div className={styles.breakGlass}>
          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Access to this patient's record is restricted</MessageBarTitle>
              Use emergency access only when it is clinically necessary. Your name, the reason and every screen you open
              are recorded and reviewed.
            </MessageBarBody>
          </MessageBar>
          {canBreakGlass ? (
            <>
              <Field
                label="Reason for emergency access"
                required
                hint="At least 15 characters, for example: patient unconscious in casualty, needs allergy history"
              >
                <Textarea value={reason} onChange={(_, d) => setReason(d.value)} rows={3} maxLength={500} />
              </Field>
              <div>
                <Button
                  appearance="primary"
                  disabled={reason.trim().length < 15 || request.isPending}
                  onClick={() => request.mutate()}
                >
                  Request emergency access
                </Button>
              </div>
            </>
          ) : (
            <Text>Ask the treating doctor or the duty medical officer to open this record.</Text>
          )}
        </div>
      </Section>
    </Page>
  );
}
