import {
  Badge,
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuDivider,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowExitRegular,
  ArrowSwapRegular,
  ArrowUndoRegular,
  CheckmarkCircleRegular,
  ClipboardPulseRegular,
  DismissCircleRegular,
  EditRegular,
  MoreHorizontalRegular,
  PersonRegular,
  PrintRegular,
  ShieldKeyholeRegular,
  TagRegular,
} from '@fluentui/react-icons';
import { formatDate, formatDateOnly, formatDateTime, formatPhone, humanize, isApiError, useSession } from '@hc360/core';
import {
  DataTable,
  EmptyState,
  ErrorState,
  LoadingState,
  Page,
  PatientBanner,
  Section,
  StatusBadge,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { AdmissionDietPanel } from '@/modules/dietary/AdmissionDietPanel';
import {
  ADMISSION_SOURCE_LABEL,
  ADMISSION_STATUS_LABEL,
  ADMISSION_TYPE_LABEL,
  admissionApi,
  DISCHARGE_TYPE_LABEL,
  inpatientKeys,
  inpatientQueries,
  isActiveAdmission,
  PAYER_LABEL,
  type AdmissionDetail,
} from '@/modules/inpatient/api';
import { ConsultantVisits } from '@/modules/inpatient/ConsultantVisits';
import {
  ClearanceStatus,
  CompleteDischargeDialog,
  InitiateDischargeDialog,
} from '@/modules/inpatient/DischargeDialogs';
import { EditAdmissionDrawer } from '@/modules/inpatient/EditAdmissionDrawer';
import { useInpatientPermissions, useInpatientRealtime } from '@/modules/inpatient/hooks';
import { printAdmissionSlip, printWristband } from '@/modules/inpatient/print';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { formatDuration } from '@/modules/inpatient/time';
import { TransferDialog } from '@/modules/inpatient/TransferDialog';

type TabId = 'overview' | 'beds' | 'visits' | 'charges' | 'diet';
const TABS: TabId[] = ['overview', 'beds', 'visits', 'charges', 'diet'];

export const Route = createFileRoute('/_app/inpatient/admissions/$admissionId')({
  validateSearch: (s: Record<string, unknown>): { tab?: TabId } => ({
    tab: TABS.includes(s.tab as TabId) && s.tab !== 'overview' ? (s.tab as TabId) : undefined,
  }),
  component: AdmissionPage,
});

const useStyles = makeStyles({
  details: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(230px, 1fr))', gap: '16px 24px' },
  item: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  wide: { gridColumn: '1 / -1' },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

function Detail({ label, children, wide }: { label: string; children: ReactNode; wide?: boolean }) {
  const styles = useStyles();
  return (
    <div className={wide ? `${styles.item} ${styles.wide}` : styles.item}>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text style={{ whiteSpace: 'pre-wrap' }}>{children || '—'}</Text>
    </div>
  );
}

type OpenDialog = 'transfer' | 'initiate' | 'complete' | 'revoke' | 'cancel' | 'edit' | null;

/** One in-patient stay: details, bed history, consultant visits, charges and the ADT actions (WB-03/04). */
function AdmissionPage() {
  const { admissionId } = Route.useParams();
  const { tab = 'overview' } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const permissions = useInpatientPermissions();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const [dialog, setDialog] = useState<OpenDialog>(null);
  const canSeeDiet = usePermission('dietary.orders.read');
  useInpatientRealtime();
  const admission = useQuery(inpatientQueries.admission(admissionId));

  const revoke = useMutation({
    mutationFn: (reason: string) => admissionApi.revokeDischarge(admissionId, reason),
    onSuccess: () => {
      notify.success('Discharge revoked', 'The patient stays admitted.');
      setDialog(null);
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
    },
  });
  const cancel = useMutation({
    mutationFn: (reason: string) => admissionApi.cancel(admissionId, reason),
    onSuccess: () => {
      notify.success('Admission cancelled', 'Its bed and nursing charges were reversed and the bed sent for cleaning.');
      setDialog(null);
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
    },
  });

  if (admission.isLoading) {
    return <LoadingState label="Loading the admission…" />;
  }
  if (admission.error || !admission.data) {
    const restricted = isApiError(admission.error) && admission.error.code === 'patient.restricted';
    return (
      <Page maxWidth={900}>
        {restricted ? (
          <MessageBar intent="warning" layout="multiline" icon={<ShieldKeyholeRegular />}>
            <MessageBarBody>
              <MessageBarTitle>Restricted record</MessageBarTitle>
              Access to this patient's record is restricted. If it is clinically necessary, open the patient from
              Patients and request emergency access; the request is recorded.
            </MessageBarBody>
            <MessageBarActions>
              <Button onClick={() => void navigate({ to: '/patients' })}>Go to Patients</Button>
            </MessageBarActions>
          </MessageBar>
        ) : (
          <ErrorState
            error={admission.error}
            onRetry={() => void admission.refetch()}
            title="Could not load the admission"
          />
        )}
      </Page>
    );
  }

  const a = admission.data;
  const active = isActiveAdmission(a.status);
  const initiated = a.status === 'DischargeInitiated';
  const chartOpen =
    active ||
    (a.dischargedAt !== null &&
      a.status !== 'Cancelled' &&
      Date.parse(a.dischargedAt) > admission.dataUpdatedAt - 24 * 3600_000);
  const alerts = [a.isMlc ? 'Medico-legal case' : null, initiated ? 'Discharge in progress' : null].filter(
    (x): x is string => x !== null,
  );

  const print = (what: 'slip' | 'wristband') => {
    const job = what === 'slip' ? printAdmissionSlip(a, facilityName) : printWristband(a);
    job.catch((error: unknown) => notify.error(error, 'Printing failed'));
  };

  return (
    <Page>
      <PatientBanner
        patient={{
          id: a.patientId,
          uhid: a.uhid,
          fullName: a.patientName,
          gender: a.gender,
          age: a.age,
          alerts,
          payer: PAYER_LABEL[a.payerType],
        }}
        actions={
          <>
            {initiated && permissions.discharge && (
              <Button appearance="primary" icon={<CheckmarkCircleRegular />} onClick={() => setDialog('complete')}>
                Complete discharge
              </Button>
            )}
            {a.status === 'Admitted' && permissions.transfer && (
              <Button
                appearance={permissions.discharge ? 'secondary' : 'primary'}
                icon={<ArrowSwapRegular />}
                onClick={() => setDialog('transfer')}
              >
                Transfer
              </Button>
            )}
            {a.status === 'Admitted' && permissions.discharge && (
              <Button appearance="primary" icon={<ArrowExitRegular />} onClick={() => setDialog('initiate')}>
                Start discharge
              </Button>
            )}
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button icon={<MoreHorizontalRegular />} aria-label="More actions" />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {active && permissions.admit && (
                    <MenuItem icon={<EditRegular />} onClick={() => setDialog('edit')}>
                      Edit details
                    </MenuItem>
                  )}
                  {(permissions.chart || permissions.mar) && active && a.wardId && (
                    <MenuItem
                      icon={<ClipboardPulseRegular />}
                      onClick={() =>
                        void navigate({ to: '/nursing', search: { wardId: a.wardId ?? undefined, admissionId: a.id } })
                      }
                    >
                      Nursing chart
                    </MenuItem>
                  )}
                  <MenuItem
                    icon={<PersonRegular />}
                    onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: a.patientId } })}
                  >
                    Patient profile
                  </MenuItem>
                  <MenuDivider />
                  <MenuItem icon={<PrintRegular />} onClick={() => print('slip')}>
                    Print admission slip
                  </MenuItem>
                  {active && (
                    <MenuItem icon={<TagRegular />} onClick={() => print('wristband')}>
                      Print wristband
                    </MenuItem>
                  )}
                  {initiated && permissions.discharge && (
                    <MenuItem icon={<ArrowUndoRegular />} onClick={() => setDialog('revoke')}>
                      Revoke discharge
                    </MenuItem>
                  )}
                  {a.status === 'Admitted' && permissions.discharge && (
                    <MenuItem icon={<ArrowExitRegular />} onClick={() => setDialog('complete')}>
                      Record death or abscondence
                    </MenuItem>
                  )}
                  {a.status === 'Admitted' && permissions.admit && (
                    <>
                      <MenuDivider />
                      <MenuItem icon={<DismissCircleRegular />} onClick={() => setDialog('cancel')}>
                        Cancel admission
                      </MenuItem>
                    </>
                  )}
                </MenuList>
              </MenuPopover>
            </Menu>
          </>
        }
      />

      <StayHeader admission={a} />

      {initiated && (
        <Section
          title="Discharge in progress"
          description={a.dischargeInitiatedAt ? `Started ${formatDateTime(a.dischargeInitiatedAt)}` : undefined}
        >
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <ClearanceStatus admissionId={a.id} />
            {a.dischargeNotes && <Caption1>Notes: {a.dischargeNotes}</Caption1>}
          </div>
        </Section>
      )}

      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={tab}
            onTabSelect={(_, d) =>
              void navigate({ search: { tab: d.value === 'overview' ? undefined : (d.value as TabId) }, replace: true })
            }
          >
            <Tab value="overview">Details</Tab>
            <Tab value="beds">Bed history</Tab>
            <Tab value="visits">Consultant visits</Tab>
            <Tab value="charges">Charges so far</Tab>
            {canSeeDiet && <Tab value="diet">Diet</Tab>}
          </TabList>
        </div>
        <div style={{ padding: 16 }}>
          {tab === 'overview' && <Overview admission={a} />}
          {tab === 'beds' && <BedHistory admissionId={a.id} />}
          {tab === 'visits' && (
            <ConsultantVisits
              admissionId={a.id}
              chartOpen={chartOpen}
              defaultPractitioner={
                a.consultantPractitionerId
                  ? { id: a.consultantPractitionerId, code: '', name: a.consultantName ?? 'Consultant' }
                  : null
              }
            />
          )}
          {tab === 'charges' && <Charges admissionId={a.id} />}
          {tab === 'diet' && canSeeDiet && (
            <AdmissionDietPanel
              admission={{
                admissionId: a.id,
                patientName: a.patientName,
                place: [a.wardName, a.bedLabel ? `bed ${a.bedLabel}` : null].filter(Boolean).join(', '),
              }}
              canOrder={active}
            />
          )}
        </div>
      </Section>

      {dialog === 'transfer' && <TransferDialog admission={a} open onClose={() => setDialog(null)} />}
      {dialog === 'initiate' && <InitiateDischargeDialog admission={a} open onClose={() => setDialog(null)} />}
      {dialog === 'complete' && <CompleteDischargeDialog admission={a} open onClose={() => setDialog(null)} />}
      {dialog === 'edit' && <EditAdmissionDrawer admission={a} open onClose={() => setDialog(null)} />}
      <ReasonDialog
        open={dialog === 'revoke'}
        title="Revoke the discharge?"
        description="Use this when the patient's condition changed and the stay continues. Billing is told to stop the final bill."
        confirmLabel="Revoke discharge"
        busy={revoke.isPending}
        error={revoke.error}
        onConfirm={(reason) => revoke.mutate(reason)}
        onCancel={() => {
          setDialog(null);
          revoke.reset();
        }}
      />
      <ReasonDialog
        open={dialog === 'cancel'}
        title="Cancel this admission?"
        description={
          <>
            Only for an admission made in error or a patient who never arrived. This cannot be undone. Cancelling also:
            <ul style={{ margin: '6px 0 0', paddingInlineStart: 20 }}>
              <li>cancels the IP visit and reverses this stay's bed and nursing charges;</li>
              <li>stops the diet order and any meals not yet served;</li>
              <li>cancels pending ward pharmacy indents;</li>
              <li>cancels surgeries still waiting to be done and pending blood requests;</li>
              <li>sends the bed for cleaning.</li>
            </ul>
          </>
        }
        minLength={5}
        confirmLabel="Cancel admission"
        destructive
        busy={cancel.isPending}
        error={cancel.error}
        onConfirm={(reason) => cancel.mutate(reason)}
        onCancel={() => {
          setDialog(null);
          cancel.reset();
        }}
      />
    </Page>
  );
}

function StayHeader({ admission: a }: { admission: AdmissionDetail }) {
  const statusTone =
    a.status === 'Admitted'
      ? 'brand'
      : a.status === 'DischargeInitiated'
        ? 'warning'
        : a.status === 'Discharged'
          ? 'success'
          : a.status === 'Cancelled'
            ? 'informative'
            : 'danger';
  return (
    <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'center' }}>
      <StatusBadge status={a.status} label={ADMISSION_STATUS_LABEL[a.status]} tone={statusTone} />
      <Text>
        <Text weight="semibold" font="monospace">
          {a.admissionNumber}
        </Text>{' '}
        · {a.wardName ?? 'No ward'}
        {a.bedLabel ? `, bed ${a.bedLabel}` : ''} · Day {a.lengthOfStayDays} · Admitted {formatDateTime(a.admittedAt)}
        {a.consultantName ? ` · ${a.consultantName}` : ''}
      </Text>
      {a.isMlc && (
        <Badge color="danger" appearance="filled">
          MLC {a.mlcNumber ?? ''}
        </Badge>
      )}
    </div>
  );
}

function Overview({ admission: a }: { admission: AdmissionDetail }) {
  const styles = useStyles();
  const attendant = [
    a.attendantName,
    a.attendantRelation ? `(${a.attendantRelation})` : null,
    a.attendantPhone ? formatPhone(a.attendantPhone) : null,
  ]
    .filter(Boolean)
    .join(' ');
  return (
    <div className={styles.stack}>
      <div className={styles.details}>
        <Detail label="IP number">{a.admissionNumber}</Detail>
        <Detail label="IP visit number">{a.visitNumber}</Detail>
        <Detail label="Admitted">{formatDateTime(a.admittedAt)}</Detail>
        <Detail label="Admission type">{ADMISSION_TYPE_LABEL[a.type] ?? humanize(a.type)}</Detail>
        <Detail label="Came from">{ADMISSION_SOURCE_LABEL[a.source] ?? humanize(a.source)}</Detail>
        {a.referredBy && <Detail label="Referred by">{a.referredBy}</Detail>}
        <Detail label="Department">{a.departmentName}</Detail>
        <Detail label="Admitting doctor">{a.admittingPractitionerName}</Detail>
        <Detail label="Consultant in charge">{a.consultantName}</Detail>
        <Detail label="Ward and bed">{[a.wardName, a.bedLabel].filter(Boolean).join(' · ')}</Detail>
        <Detail label="Expected discharge">
          {a.expectedDischargeDate ? formatDateOnly(a.expectedDischargeDate) : null}
        </Detail>
        <Detail label="Payment by">{PAYER_LABEL[a.payerType] ?? humanize(a.payerType)}</Detail>
        {a.policyNumberMasked && <Detail label="Policy / TPA card">{a.policyNumberMasked}</Detail>}
        <Detail label="Attendant">{attendant}</Detail>
        <Detail label="Provisional diagnosis" wide>
          {a.provisionalDiagnosis}
        </Detail>
        {a.notes && (
          <Detail label="Notes" wide>
            {a.notes}
          </Detail>
        )}
      </div>
      {(a.dischargedAt || a.dischargeType) && (
        <MessageBar intent={a.status === 'Expired' ? 'error' : 'info'} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{a.dischargeType ? DISCHARGE_TYPE_LABEL[a.dischargeType] : 'Discharged'}</MessageBarTitle>
            {a.dischargedAt ? `On ${formatDateTime(a.dischargedAt)}.` : ''} {a.dischargeNotes ?? ''}
          </MessageBarBody>
        </MessageBar>
      )}
      {a.cancelledAt && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Cancelled on {formatDateTime(a.cancelledAt)}</MessageBarTitle>
            {a.cancelReason}
          </MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}

function BedHistory({ admissionId }: { admissionId: string }) {
  const history = useQuery(inpatientQueries.bedAssignments(admissionId));
  return (
    <DataTable
      ariaLabel="Bed history"
      rows={history.data}
      loading={history.isLoading}
      error={history.error}
      onRetry={() => void history.refetch()}
      getRowId={(h) => h.id}
      size="small"
      columns={[
        { key: 'bed', header: 'Ward · bed', render: (h) => `${h.wardName} · ${h.bedLabel}` },
        {
          key: 'reason',
          header: 'Why',
          render: (h) =>
            h.reason === 'Admission' ? 'Admitted' : `Transferred${h.transferReason ? `: ${h.transferReason}` : ''}`,
        },
        { key: 'from', header: 'From', width: 170, render: (h) => formatDateTime(h.fromAt) },
        {
          key: 'to',
          header: 'To',
          width: 170,
          render: (h) =>
            h.toAt ? (
              formatDateTime(h.toAt)
            ) : (
              <Badge appearance="tint" color="brand">
                Now
              </Badge>
            ),
        },
        {
          key: 'duration',
          header: 'Stay',
          width: 110,
          priority: 'low',
          render: (h) =>
            formatDuration(
              Math.round(((h.toAt ? Date.parse(h.toAt) : history.dataUpdatedAt) - Date.parse(h.fromAt)) / 60_000),
            ),
        },
      ]}
      emptyTitle="No bed history"
    />
  );
}

function Charges({ admissionId }: { admissionId: string }) {
  const charges = useQuery(inpatientQueries.charges(admissionId));
  const visits = useQuery(inpatientQueries.visits(admissionId));
  const rows = charges.data ?? [];
  const live = rows.filter((c) => !c.cancelledAt);
  const bedDays = live.filter((c) => c.kind === 'Bed').length;
  const nursingDays = live.filter((c) => c.kind === 'Nursing').length;
  const visitCount = visits.data?.filter((v) => v.isCharged).length ?? 0;

  if (charges.data && rows.length === 0 && visitCount === 0) {
    return (
      <EmptyState
        title="No charges posted yet"
        description="Bed and nursing days are charged at the midnight census (and when discharge starts). Amounts appear on the IP bill in Billing."
      />
    );
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <Text>
        <strong>{bedDays}</strong> bed days, <strong>{nursingDays}</strong> nursing days and{' '}
        <strong>{visitCount}</strong> consultant visits charged so far. Amounts, discounts and deposits are on the IP
        bill in Billing.
      </Text>
      <DataTable
        ariaLabel="Daily charges"
        rows={charges.data}
        loading={charges.isLoading}
        error={charges.error}
        onRetry={() => void charges.refetch()}
        getRowId={(c) => `${c.chargeDate}-${c.kind}-${c.chargeId}`}
        size="small"
        columns={[
          { key: 'date', header: 'Day', width: 130, render: (c) => formatDateOnly(c.chargeDate) },
          { key: 'kind', header: 'Charge', render: (c) => (c.kind === 'Bed' ? 'Bed day' : 'Nursing day') },
          {
            key: 'code',
            header: 'Service',
            width: 130,
            render: (c) => <Text font="monospace">{c.serviceCode ?? '—'}</Text>,
          },
          { key: 'posted', header: 'Posted', width: 130, priority: 'low', render: (c) => formatDate(c.postedAt) },
          {
            key: 'status',
            header: 'Status',
            width: 120,
            render: (c) =>
              c.cancelledAt ? (
                <StatusBadge status="Cancelled" label="Reversed" />
              ) : (
                <StatusBadge status="Completed" label="On bill" />
              ),
          },
        ]}
        emptyTitle="No bed or nursing days charged yet"
      />
    </div>
  );
}
