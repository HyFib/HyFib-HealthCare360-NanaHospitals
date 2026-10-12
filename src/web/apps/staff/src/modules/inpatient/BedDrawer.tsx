import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Divider,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
  Subtitle2,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  BroomRegular,
  CalendarClockRegular,
  CheckmarkCircleRegular,
  ClipboardPulseRegular,
  DismissRegular,
  LockClosedRegular,
  LockOpenRegular,
  OpenRegular,
  PersonAddRegular,
  PersonRegular,
  PlayRegular,
  WrenchRegular,
} from '@fluentui/react-icons';
import { formatDate, formatDateOnly, formatDateTime, formatRelative, humanize, isApiError } from '@hc360/core';
import { ConfirmDialog, useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState, type ReactElement, type ReactNode } from 'react';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { bedApi, inpatientKeys, inpatientQueries, PAYER_LABEL, type BedBoardItem, type BedBoardWard } from './api';
import { BED_ACTION_LABEL, bedActions, type BedAction } from './bedState';
import { BedAlertBadges, BedFeatures, BedStateChip } from './BedTile';
import { useInpatientPermissions } from './hooks';
import { ReasonDialog } from './ReasonDialog';
import { fromIstInput, toIstInput } from './time';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '16px', paddingBottom: '16px' },
  rows: { display: 'grid', gridTemplateColumns: 'minmax(110px, auto) 1fr', gap: '6px 12px' },
  label: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', flexDirection: 'column', gap: '8px' },
  actionButton: { justifyContent: 'flex-start', minHeight: '44px' },
  history: { display: 'flex', flexDirection: 'column', gap: '6px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const ACTION_ICONS: Record<BedAction, ReactElement> = {
  admit: <PersonAddRegular />,
  reserve: <CalendarClockRegular />,
  cancelReservation: <DismissRegular />,
  sendForCleaning: <BroomRegular />,
  startCleaning: <PlayRegular />,
  completeCleaning: <CheckmarkCircleRegular />,
  block: <LockClosedRegular />,
  unblock: <LockOpenRegular />,
  startMaintenance: <WrenchRegular />,
  endMaintenance: <CheckmarkCircleRegular />,
};

function Row({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text>{children || '—'}</Text>
    </>
  );
}

type Pending = { action: BedAction } | null;

/** Everything about one bed and what may be done with it, in a side panel that keeps the board visible. */
export function BedDrawer({
  bed,
  ward,
  onClose,
}: {
  bed: BedBoardItem;
  ward: Pick<BedBoardWard, 'wardId' | 'name'>;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const permissions = useInpatientPermissions();
  const [pending, setPending] = useState<Pending>(null);
  const actions = bedActions(bed.status, {
    admit: permissions.admit,
    manage: permissions.manageBeds,
    housekeeping: permissions.housekeeping,
  });
  const needsTask = bed.status === 'Cleaning' && (permissions.housekeeping || permissions.manageBeds);
  const tasks = useQuery({ ...inpatientQueries.housekeeping(ward.wardId), enabled: needsTask });
  const task =
    tasks.data?.find((t) => t.bedId === bed.bedId && (t.status === 'Pending' || t.status === 'InProgress')) ?? null;
  const history = useQuery({ ...inpatientQueries.bedHistory(bed.bedId), enabled: permissions.read });

  const visibleActions = actions.filter((action) => {
    if (action === 'startCleaning') {
      return task?.status === 'Pending';
    }
    if (action === 'completeCleaning') {
      return task !== null;
    }
    return true;
  });

  const run = useMutation({
    mutationFn: async ({ action, text }: { action: BedAction; text?: string }) => {
      switch (action) {
        case 'cancelReservation':
          return bedApi.cancelReservation(bed.bedId);
        case 'block':
          return bedApi.block(bed.bedId, text ?? '');
        case 'unblock':
          return bedApi.unblock(bed.bedId);
        case 'startMaintenance':
          return bedApi.startMaintenance(bed.bedId, text ?? '');
        case 'endMaintenance':
          return bedApi.endMaintenance(bed.bedId);
        case 'sendForCleaning':
          return bedApi.sendForCleaning(bed.bedId, text || null);
        case 'startCleaning':
          return task ? bedApi.startCleaning(task.id) : undefined;
        case 'completeCleaning':
          return task ? bedApi.completeCleaning(task.id, text || null) : undefined;
        default:
          return undefined;
      }
    },
    onSuccess: (_, { action }) => {
      const done: Partial<Record<BedAction, string>> = {
        cancelReservation: `Reservation on ${bed.label} cancelled`,
        block: `${bed.label} blocked`,
        unblock: `${bed.label} is vacant again`,
        startMaintenance: `${bed.label} sent for maintenance`,
        endMaintenance: `${bed.label} sent for cleaning after repair`,
        sendForCleaning: `${bed.label} sent for cleaning`,
        startCleaning: `Cleaning of ${bed.label} started`,
        completeCleaning: `${bed.label} is clean and vacant`,
      };
      notify.success(done[action] ?? 'Bed updated');
      setPending(null);
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      }
    },
  });

  const start = (action: BedAction) => {
    run.reset();
    if (action === 'admit') {
      void navigate({
        to: '/inpatient/admit',
        search: { bedId: bed.bedId },
      });
      return;
    }
    if (action === 'startCleaning') {
      run.mutate({ action });
      return;
    }
    setPending({ action });
  };

  const occupant = bed.occupant;
  const reasonAction =
    pending && ['block', 'startMaintenance', 'sendForCleaning', 'completeCleaning'].includes(pending.action)
      ? pending.action
      : null;
  const confirmAction =
    pending && ['cancelReservation', 'unblock', 'endMaintenance'].includes(pending.action) ? pending.action : null;

  return (
    <OverlayDrawer open position="end" size="small" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          Bed {bed.label}
        </DrawerHeaderTitle>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
          <BedStateChip status={bed.status} />
          <Caption1 className={styles.muted}>
            {ward.name}
            {bed.roomNumber ? ` · Room ${bed.roomNumber}` : ''}
          </Caption1>
        </div>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.body}>
          <div className={styles.rows}>
            <Row label="Since">
              {bed.statusChangedAt
                ? `${formatDateTime(bed.statusChangedAt)} (${formatRelative(bed.statusChangedAt)})`
                : null}
            </Row>
            {bed.statusReason && <Row label="Reason">{bed.statusReason}</Row>}
            {bed.reservedUntil && <Row label="Held until">{formatDateTime(bed.reservedUntil)}</Row>}
            <Row label="Equipment">
              <BedFeatures oxygen={bed.hasOxygen} ventilator={bed.hasVentilator} monitor={bed.hasMonitor} />
              {!bed.hasOxygen && !bed.hasVentilator && !bed.hasMonitor ? 'Standard bed' : null}
            </Row>
            {task && (
              <Row label="Cleaning">
                {task.status === 'InProgress'
                  ? `In progress since ${formatRelative(task.startedAt)}`
                  : `Waiting ${task.waitingMinutes} min`}
              </Row>
            )}
          </div>

          {bed.alerts.length > 0 && <BedAlertBadges alerts={bed.alerts} max={10} />}

          {occupant && (
            <>
              <Divider />
              <Subtitle2 as="h3">{occupant.patientName}</Subtitle2>
              <div className={styles.rows}>
                <Row label="UHID">{occupant.uhid}</Row>
                <Row label="Age / sex">{`${occupant.age ?? '—'} / ${humanize(occupant.gender)}`}</Row>
                <Row label="Admission">{occupant.admissionNumber}</Row>
                <Row label="Admitted">{`${formatDateTime(occupant.admittedAt)} · day ${occupant.lengthOfStayDays}`}</Row>
                <Row label="Consultant">{occupant.consultantName}</Row>
                <Row label="Expected discharge">
                  {occupant.expectedDischargeDate ? formatDateOnly(occupant.expectedDischargeDate) : null}
                </Row>
                <Row label="Payment by">{PAYER_LABEL[occupant.payerType] ?? occupant.payerType}</Row>
              </div>
              <div className={styles.actions}>
                {permissions.read && (
                  <Button
                    className={styles.actionButton}
                    appearance="primary"
                    icon={<OpenRegular />}
                    onClick={() =>
                      void navigate({
                        to: '/inpatient/admissions/$admissionId',
                        params: { admissionId: occupant.admissionId },
                      })
                    }
                  >
                    Open admission
                  </Button>
                )}
                {(permissions.chart || permissions.mar) && (
                  <Button
                    className={styles.actionButton}
                    icon={<ClipboardPulseRegular />}
                    onClick={() =>
                      void navigate({
                        to: '/nursing',
                        search: { wardId: ward.wardId, admissionId: occupant.admissionId },
                      })
                    }
                  >
                    Nursing chart
                  </Button>
                )}
                <Button
                  className={styles.actionButton}
                  icon={<PersonRegular />}
                  onClick={() =>
                    void navigate({ to: '/patients/$patientId', params: { patientId: occupant.patientId } })
                  }
                >
                  Patient profile
                </Button>
              </div>
            </>
          )}

          {visibleActions.length > 0 && (
            <>
              <Divider />
              <div className={styles.actions} role="group" aria-label="Bed actions">
                {visibleActions.map((action, index) => (
                  <Button
                    key={action}
                    className={styles.actionButton}
                    appearance={index === 0 && !occupant ? 'primary' : 'secondary'}
                    icon={
                      run.isPending && run.variables?.action === action ? (
                        <Spinner size="tiny" aria-hidden />
                      ) : (
                        ACTION_ICONS[action]
                      )
                    }
                    disabled={run.isPending}
                    onClick={() => start(action)}
                  >
                    {BED_ACTION_LABEL[action]}
                  </Button>
                ))}
              </div>
            </>
          )}
          {run.error && !pending && (
            <MessageBar intent="error">
              <MessageBarBody>
                {isApiError(run.error) ? run.error.userMessage : 'The bed could not be updated.'}
              </MessageBarBody>
            </MessageBar>
          )}

          {permissions.read && history.data && history.data.items.length > 0 && (
            <>
              <Divider />
              <Subtitle2 as="h3">Recent patients in this bed</Subtitle2>
              <div className={styles.history}>
                {history.data.items.map((h) => (
                  <div key={`${h.admissionId}-${h.fromAt}`}>
                    <Text weight="semibold">{h.patientName}</Text>{' '}
                    <Caption1 className={styles.muted}>
                      {h.admissionNumber} · {formatDate(h.fromAt)} to {h.toAt ? formatDate(h.toAt) : 'now'}
                      {h.reason === 'Transfer' ? ' · transfer in' : ''}
                    </Caption1>
                  </div>
                ))}
              </div>
            </>
          )}
        </div>
      </DrawerBody>

      {pending?.action === 'reserve' && (
        <ReserveDialog
          bedLabel={bed.label}
          bedId={bed.bedId}
          onClose={() => setPending(null)}
          onDone={() => {
            setPending(null);
            void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
            onClose();
          }}
        />
      )}

      <ReasonDialog
        open={reasonAction !== null}
        title={
          reasonAction === 'block'
            ? `Block bed ${bed.label}?`
            : reasonAction === 'startMaintenance'
              ? `Send ${bed.label} for maintenance?`
              : reasonAction === 'completeCleaning'
                ? `Mark ${bed.label} as cleaned?`
                : `Send ${bed.label} for cleaning?`
        }
        description={
          reasonAction === 'block'
            ? 'A blocked bed is out of service and does not count as available.'
            : reasonAction === 'startMaintenance'
              ? 'After the repair the bed goes to cleaning before anyone uses it.'
              : reasonAction === 'completeCleaning'
                ? 'The bed becomes vacant and appears on the admission desk immediately.'
                : 'Housekeeping sees it in the cleaning queue.'
        }
        label={reasonAction === 'block' || reasonAction === 'startMaintenance' ? 'Reason' : 'Note'}
        placeholder={
          reasonAction === 'startMaintenance'
            ? 'e.g. Side rail broken, oxygen flowmeter leaking'
            : reasonAction === 'block'
              ? 'e.g. Reserved for isolation, AC not working'
              : undefined
        }
        optional={reasonAction === 'sendForCleaning' || reasonAction === 'completeCleaning'}
        confirmLabel={reasonAction ? BED_ACTION_LABEL[reasonAction] : 'Confirm'}
        destructive={reasonAction === 'block'}
        busy={run.isPending}
        error={run.error}
        onConfirm={(text) => reasonAction && run.mutate({ action: reasonAction, text })}
        onCancel={() => setPending(null)}
      />

      <ConfirmDialog
        open={confirmAction !== null}
        title={confirmAction ? `${BED_ACTION_LABEL[confirmAction]}?` : ''}
        confirmLabel={confirmAction ? BED_ACTION_LABEL[confirmAction] : 'Confirm'}
        busy={run.isPending}
        onConfirm={() => confirmAction && run.mutate({ action: confirmAction })}
        onCancel={() => setPending(null)}
      >
        {confirmAction === 'cancelReservation'
          ? `The reservation on ${bed.label} ends and the bed becomes available to anyone.`
          : confirmAction === 'unblock'
            ? `${bed.label} becomes vacant and available for admission.`
            : `${bed.label} goes to the cleaning queue before it can be used.`}
        {run.error && (
          <MessageBar intent="error" style={{ marginTop: 12 }}>
            <MessageBarBody>
              {isApiError(run.error) ? run.error.userMessage : 'The bed could not be updated.'}
            </MessageBarBody>
          </MessageBar>
        )}
      </ConfirmDialog>
    </OverlayDrawer>
  );
}

function ReserveDialog({
  bedId,
  bedLabel,
  onClose,
  onDone,
}: {
  bedId: string;
  bedLabel: string;
  onClose: () => void;
  onDone: () => void;
}) {
  const notify = useNotify();
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [limits] = useState(() => {
    const now = new Date();
    return {
      min: toIstInput(now),
      max: toIstInput(new Date(now.getTime() + 7 * 24 * 3600_000)),
      initial: toIstInput(new Date(now.getTime() + 4 * 3600_000)),
    };
  });
  const [until, setUntil] = useState(limits.initial);
  const [note, setNote] = useState('');
  const reserve = useMutation({
    mutationFn: () =>
      bedApi.reserve(bedId, { patientId: patient!.id, reservedUntil: fromIstInput(until)!, note: note.trim() || null }),
    onSuccess: () => {
      notify.success(`${bedLabel} reserved`, `Held for ${patient?.fullName} until the chosen time.`);
      onDone();
    },
  });
  const valid = patient !== null && fromIstInput(until) !== null;

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ patient, until, note }),
    busy: reserve.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid) {
              reserve.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Reserve bed {bedLabel}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <PatientPicker value={patient} onChange={setPatient} label="Reserve for" required />
              <Field
                label="Hold until"
                required
                hint="At most 7 days. After this time anyone may be admitted to the bed."
              >
                <Input
                  type="datetime-local"
                  value={until}
                  min={limits.min}
                  max={limits.max}
                  onChange={(_, d) => setUntil(d.value)}
                />
              </Field>
              <Field label="Note">
                <Textarea
                  value={note}
                  onChange={(_, d) => setNote(d.value)}
                  maxLength={300}
                  rows={2}
                  placeholder="e.g. Planned surgery tomorrow 9 am"
                />
              </Field>
              {reserve.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {isApiError(reserve.error) ? reserve.error.userMessage : 'The bed could not be reserved.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={reserve.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || reserve.isPending}
                icon={reserve.isPending ? <Spinner size="tiny" aria-hidden /> : <CalendarClockRegular />}
              >
                Reserve bed
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
