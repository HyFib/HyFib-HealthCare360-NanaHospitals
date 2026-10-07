import {
  Button,
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
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  CalendarArrowRightRegular,
  CalendarCancelRegular,
  DismissCircleRegular,
  EditRegular,
  MoreHorizontalRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatTime, useRealtimeEvent } from '@hc360/core';
import { LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { AnaesthesiaPanel } from '@/modules/ot/AnaesthesiaPanel';
import { otApi, otKeys, otQueries, type SurgeryDetail } from '@/modules/ot/api';
import { PlanDialog, ScheduleDialog } from '@/modules/ot/BookingDialogs';
import { anaesthesiaLabel, lateralityLabel } from '@/modules/ot/labels';
import {
  cancelGate,
  currentStepId,
  planGate,
  postponeGate,
  procedureWithSide,
  scheduleGate,
  STEP_IDS,
  type StepId,
} from '@/modules/ot/logic';
import { MaterialsPanel } from '@/modules/ot/MaterialsPanel';
import { OperationNotePanel } from '@/modules/ot/OperationNotePanel';
import { PriorityBadge, SideBadge, SurgeryStatusBadge } from '@/modules/ot/OtBadges';
import { formatDuration, minutesBetween } from '@/modules/ot/shared/datetime';
import {
  ChargeStatusPanel,
  Detail,
  DetailGrid,
  NoAccess,
  ReasonDialog,
  RecordLoadError,
} from '@/modules/ot/shared/feedback';
import { useFacilityId } from '@/modules/ot/shared/hooks';
import { SpecialtyPatientBanner } from '@/modules/ot/shared/SpecialtyPatientBanner';
import { SurgeryStepper } from '@/modules/ot/SurgeryStepper';

const TABS = ['day', 'anaesthesia', 'note', 'materials', 'details'] as const;
type TabId = (typeof TABS)[number];

interface BookingSearch {
  tab?: Exclude<TabId, 'day'>;
  step?: StepId;
  action?: 'schedule';
}

export const Route = createFileRoute('/_app/ot/bookings/$id')({
  validateSearch: (s: Record<string, unknown>): BookingSearch => ({
    tab: TABS.includes(s.tab as TabId) && s.tab !== 'day' ? (s.tab as Exclude<TabId, 'day'>) : undefined,
    step: STEP_IDS.includes(s.step as StepId) ? (s.step as StepId) : undefined,
    action: s.action === 'schedule' ? 'schedule' : undefined,
  }),
  component: BookingPage,
});

const useStyles = makeStyles({
  badges: { display: 'inline-flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
  tabs: { padding: '0 8px' },
  body: { padding: '16px' },
});

type Dialog = 'schedule' | 'plan' | 'postpone' | 'cancel' | null;

function BookingPage() {
  const { id } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const facilityId = useFacilityId();
  const canRead = usePermission('ot.read');
  const booking = useQuery({ ...otQueries.booking(id), enabled: canRead });
  const [dialog, setDialog] = useState<Dialog>(search.action === 'schedule' ? 'schedule' : null);

  useRealtimeEvent<{ bookingId?: string }>(facilityId ? `ot:${facilityId}` : null, 'ot.booking.changed', (payload) => {
    if (payload.bookingId === id) {
      void queryClient.invalidateQueries({ queryKey: otKeys.booking(id) });
    }
  });

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="surgery records" />
      </Page>
    );
  }
  if (booking.isLoading) {
    return <LoadingState label="Loading the surgery…" />;
  }
  if (booking.error || !booking.data) {
    return (
      <Page maxWidth={900}>
        <RecordLoadError error={booking.error} onRetry={() => void booking.refetch()} what="surgery booking" />
      </Page>
    );
  }

  const b = booking.data;
  const closeDialog = () => {
    setDialog(null);
    if (search.action) {
      void navigate({ search: (prev) => ({ ...prev, action: undefined }), replace: true });
    }
  };

  return (
    <BookingView
      booking={b}
      tab={search.tab ?? 'day'}
      step={search.step ?? currentStepId(b)}
      onTab={(tab) =>
        void navigate({ search: (prev) => ({ ...prev, tab: tab === 'day' ? undefined : tab }), replace: true })
      }
      onStep={(step) => void navigate({ search: (prev) => ({ ...prev, step }), replace: true })}
      onAdvance={() => void navigate({ search: (prev) => ({ ...prev, step: undefined }), replace: true })}
      dialog={dialog}
      setDialog={setDialog}
      closeDialog={closeDialog}
    />
  );
}

function BookingView({
  booking: b,
  tab,
  step,
  onTab,
  onStep,
  onAdvance,
  dialog,
  setDialog,
  closeDialog,
}: {
  booking: SurgeryDetail;
  tab: TabId;
  step: StepId;
  onTab: (tab: TabId) => void;
  onStep: (step: StepId) => void;
  onAdvance: () => void;
  dialog: Dialog;
  setDialog: (dialog: Dialog) => void;
  closeDialog: () => void;
}) {
  const styles = useStyles();
  // Closing a dialog opened from the menu returns focus to the menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('ot.bookings.manage');

  const postpone = useMutation({
    mutationFn: (reason: string) => otApi.postpone(b.id, reason),
    onSuccess: () => {
      notify.success('Surgery postponed', 'The slot is free again. Schedule it when a new date is agreed.');
      void queryClient.invalidateQueries({ queryKey: otKeys.all });
      closeDialog();
    },
  });
  const cancel = useMutation({
    mutationFn: (reason: string) => otApi.cancel(b.id, reason),
    onSuccess: () => {
      notify.success('Surgery cancelled');
      void queryClient.invalidateQueries({ queryKey: otKeys.all });
      closeDialog();
    },
  });
  const retry = useMutation({
    mutationFn: () => otApi.retryCharges(b.id),
    onSuccess: (outcome) => {
      if (outcome.status === 'Failed') {
        notify.warning('Billing is still not available', outcome.error ?? undefined);
      } else {
        notify.success('Charges posted to the bill');
      }
      void queryClient.invalidateQueries({ queryKey: otKeys.booking(b.id) });
      void queryClient.invalidateQueries({ queryKey: otKeys.materials(b.id) });
    },
    onError: (error) => notify.error(error, 'Could not retry billing'),
  });

  const slot = b.scheduledStart
    ? `${formatDateTime(b.scheduledStart)}–${formatTime(b.scheduledEnd)}`
    : b.preferredDate
      ? `prefers ${formatDateOnly(b.preferredDate)}`
      : 'no slot yet';
  const needsSlot = b.status === 'Requested' || b.status === 'Postponed';

  return (
    <Page>
      <SpecialtyPatientBanner
        summary={b.patient}
        patientId={b.patientId}
        fallback={{ uhid: '', fullName: 'Patient' }}
        alerts={b.priority === 'Emergency' ? ['Emergency surgery'] : undefined}
      />
      <PageHeader
        title={b.procedureName}
        subtitle={`${b.bookingNumber} · ${b.theatreName ?? 'No theatre'} · ${slot} · ${anaesthesiaLabel[b.anaesthesiaType]}`}
        breadcrumbs={[
          { label: 'OT calendar', onClick: () => void navigate({ to: '/ot' }) },
          { label: 'Surgeries', onClick: () => void navigate({ to: '/ot/bookings' }) },
          { label: b.bookingNumber },
        ]}
        actions={
          <>
            <span className={styles.badges}>
              <SideBadge laterality={b.laterality} size="large" />
              <PriorityBadge priority={b.priority} />
              <SurgeryStatusBadge status={b.status} />
            </span>
            {canManage && needsSlot && scheduleGate(b).allowed && (
              <Button appearance="primary" icon={<CalendarArrowRightRegular />} onClick={() => setDialog('schedule')}>
                Schedule surgery
              </Button>
            )}
            {canManage && planGate(b).allowed && (
              <Button icon={<EditRegular />} onClick={() => setDialog('plan')}>
                Edit plan
              </Button>
            )}
            {canManage && (b.status === 'Scheduled' || postponeGate(b).allowed || cancelGate(b).allowed) && (
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button icon={<MoreHorizontalRegular />} aria-label="More actions" {...restoreFocusTarget} />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    {b.status === 'Scheduled' && (
                      <MenuItem icon={<CalendarArrowRightRegular />} onClick={() => setDialog('schedule')}>
                        Move to another slot
                      </MenuItem>
                    )}
                    {postponeGate(b).allowed && (
                      <MenuItem icon={<CalendarCancelRegular />} onClick={() => setDialog('postpone')}>
                        Postpone
                      </MenuItem>
                    )}
                    {cancelGate(b).allowed && (
                      <MenuItem icon={<DismissCircleRegular />} onClick={() => setDialog('cancel')}>
                        Cancel surgery
                      </MenuItem>
                    )}
                  </MenuList>
                </MenuPopover>
              </Menu>
            )}
          </>
        }
      />

      {b.status === 'Cancelled' && (
        <MessageBar intent="error">
          <MessageBarBody>
            <MessageBarTitle>Cancelled</MessageBarTitle>
            {b.cancelReason ?? 'No reason recorded.'}
          </MessageBarBody>
        </MessageBar>
      )}
      {b.status === 'Postponed' && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Postponed{b.postponeCount > 1 ? ` (${b.postponeCount} times)` : ''}</MessageBarTitle>
            {b.postponeReason ?? ''} The WHO checklist was reset; it is done again on the new day.
          </MessageBarBody>
        </MessageBar>
      )}
      {b.chargeStatus === 'Failed' && (
        <ChargeStatusPanel
          status={b.chargeStatus}
          error={b.chargeError}
          canRetry={canManage}
          retrying={retry.isPending}
          onRetry={() => retry.mutate()}
        />
      )}

      <Section flush>
        <div className={styles.tabs}>
          <TabList selectedValue={tab} onTabSelect={(_, d) => onTab(d.value as TabId)}>
            <Tab value="day">Day of surgery</Tab>
            <Tab value="anaesthesia">Anaesthesia</Tab>
            <Tab value="note">Operation note</Tab>
            <Tab value="materials">Implants and consumables</Tab>
            <Tab value="details">Details and billing</Tab>
          </TabList>
        </div>
        <div className={styles.body}>
          {tab === 'day' && (
            <SurgeryStepper
              booking={b}
              selected={step}
              onSelect={onStep}
              onAdvance={onAdvance}
              onSchedule={() => setDialog('schedule')}
            />
          )}
          {tab === 'anaesthesia' && <AnaesthesiaPanel booking={b} />}
          {tab === 'note' && <OperationNotePanel booking={b} />}
          {tab === 'materials' && <MaterialsPanel booking={b} />}
          {tab === 'details' && (
            <Details booking={b} canRetry={canManage} retrying={retry.isPending} onRetry={() => retry.mutate()} />
          )}
        </div>
      </Section>

      {dialog === 'schedule' && <ScheduleDialog booking={b} onClose={closeDialog} />}
      {dialog === 'plan' && <PlanDialog booking={b} onClose={closeDialog} />}
      <ReasonDialog
        open={dialog === 'postpone'}
        title="Postpone this surgery?"
        description="The theatre slot is freed and the WHO checklist is reset, because its checks are valid only on the day they are done. The pre-operative checklist is kept."
        confirmLabel="Postpone"
        busy={postpone.isPending}
        error={postpone.error}
        presets={[
          'Patient not fit (PAC)',
          'Theatre time over-ran',
          'Surgeon unavailable',
          'Blood not arranged',
          'Patient request',
        ]}
        onConfirm={(reason) => postpone.mutate(reason)}
        onCancel={() => {
          postpone.reset();
          closeDialog();
        }}
      />
      <ReasonDialog
        open={dialog === 'cancel'}
        title="Cancel this surgery?"
        description="The booking is closed and cannot be scheduled again. Book a new surgery if it is needed later."
        confirmLabel="Cancel surgery"
        destructive
        busy={cancel.isPending}
        error={cancel.error}
        presets={['Surgery no longer needed', 'Patient declined surgery', 'Patient transferred', 'Booked by mistake']}
        onConfirm={(reason) => cancel.mutate(reason)}
        onCancel={() => {
          cancel.reset();
          closeDialog();
        }}
      />
    </Page>
  );
}

function Details({
  booking: b,
  canRetry,
  retrying,
  onRetry,
}: {
  booking: SurgeryDetail;
  canRetry: boolean;
  retrying: boolean;
  onRetry: () => void;
}) {
  const inTheatre =
    b.completedAt && (b.inRoomAt ?? b.startedAt)
      ? minutesBetween(b.inRoomAt ?? b.startedAt ?? b.completedAt, b.completedAt)
      : null;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <DetailGrid>
        <Detail label="Booking number">{b.bookingNumber}</Detail>
        <Detail label="Procedure">{procedureWithSide(b.procedureName, b.laterality)}</Detail>
        <Detail label="Procedure code">{b.procedureCode}</Detail>
        <Detail label="Operative side">{lateralityLabel[b.laterality]}</Detail>
        <Detail label="Diagnosis">{b.diagnosis}</Detail>
        <Detail label="Operating surgeon">{b.surgeonName}</Detail>
        <Detail label="Assistants">
          {b.assistantIds.length
            ? `${b.assistantIds.length} assistant surgeon${b.assistantIds.length === 1 ? '' : 's'}`
            : null}
        </Detail>
        <Detail label="Anaesthetist">{b.anaesthetistName}</Detail>
        <Detail label="Anaesthesia">{anaesthesiaLabel[b.anaesthesiaType]}</Detail>
        <Detail label="Theatre">{b.theatreName}</Detail>
        <Detail label="Slot">
          {b.scheduledStart ? `${formatDateTime(b.scheduledStart)} – ${formatTime(b.scheduledEnd)}` : null}
        </Detail>
        <Detail label="Preferred date">{b.preferredDate ? formatDateOnly(b.preferredDate) : null}</Detail>
        <Detail label="Received in pre-op">{b.receivedInPreOpAt ? formatDateTime(b.receivedInPreOpAt) : null}</Detail>
        <Detail label="In theatre (Sign-in)">{b.inRoomAt ? formatDateTime(b.inRoomAt) : null}</Detail>
        <Detail label="Incision">{b.startedAt ? formatDateTime(b.startedAt) : null}</Detail>
        <Detail label="Out of theatre">{b.completedAt ? formatDateTime(b.completedAt) : null}</Detail>
        <Detail label="Time in theatre">{inTheatre !== null ? formatDuration(inTheatre) : null}</Detail>
        <Detail label="Origin">{b.orderItemId ? 'From a doctor’s order' : 'Booked at the OT desk'}</Detail>
        <Detail label="Encounter">
          {b.admissionId ? 'In-patient (charges go to the IP bill)' : b.visitId ? 'Out-patient / day care visit' : null}
        </Detail>
      </DetailGrid>
      {b.notes && (
        <Section title="Notes for the OT team">
          <Text style={{ whiteSpace: 'pre-wrap' }}>{b.notes}</Text>
        </Section>
      )}
      <Section
        title="Billing"
        description="Theatre time, procedure, surgeon and anaesthetist fees are posted when surgery is completed."
      >
        <ChargeStatusPanel
          status={b.chargeStatus}
          error={b.chargeError}
          canRetry={canRetry}
          retrying={retrying}
          onRetry={onRetry}
        />
      </Section>
    </div>
  );
}
