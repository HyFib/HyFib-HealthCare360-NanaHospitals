import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  ArrowRepeatAllRegular,
  ArrowSyncRegular,
  DismissRegular,
  MoreHorizontalRegular,
  OpenRegular,
  PrintRegular,
} from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { DataTable, LoadingState, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { ignoreEscapeWhile } from '@/lib/dialogFocus';
import { printServerDocument, useServerDocuments } from '@/lib/documents';
import { usePermission } from '@/lib/permissions';
import { emrKeys } from './api';
import { directionsText } from './dosing';
import {
  prescriptionDocument,
  prescriptionQueries,
  prescriptionsApi,
  type PrescriptionListItem,
} from './prescriptionsApi';
import { SafetyReview } from './SafetyReview';
import {
  alertsFromProblem,
  emptyDecision,
  groupAlerts,
  signBlockers,
  signRequirements,
  type SafetyAlert,
  type SignDecision,
} from './safety';
import { ReasonDialog } from './ui';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  lines: { display: 'flex', flexDirection: 'column', gap: '4px', margin: 0, paddingLeft: '18px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Authorised refill (ERX-05): issued as a new signed prescription. Safety checks run again on the server because
 * allergies or other medicines may have changed; anything it finds is resolved here before the refill is issued.
 */
export function RefillDialog({
  prescriptionId,
  patientId,
  onClose,
}: {
  prescriptionId: string | null;
  patientId: string;
  onClose: () => void;
}) {
  return (
    <Dialog open={prescriptionId !== null} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 760 }}>
        {prescriptionId && <RefillBody prescriptionId={prescriptionId} patientId={patientId} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function RefillBody({
  prescriptionId,
  patientId,
  onClose,
}: {
  prescriptionId: string;
  patientId: string;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const detail = useQuery(prescriptionQueries.detail(prescriptionId));
  const [alerts, setAlerts] = useState<SafetyAlert[] | null>(null);
  const [decision, setDecision] = useState<SignDecision>(emptyDecision);
  const [error, setError] = useState<string | null>(null);

  const rx = detail.data;
  const hasControlled = rx?.items.some((i) => i.isControlledDrug) ?? false;
  const grouped = groupAlerts(alerts ?? []);
  const requirements = signRequirements(grouped, hasControlled);
  const blockers = signBlockers(requirements, decision);

  const refill = useMutation({
    mutationFn: () =>
      prescriptionsApi.refill(prescriptionId, {
        overrideReason: requirements.needsOverride ? decision.overrideReason.trim() : null,
        acknowledgeWarnings: decision.acknowledgeWarnings,
        confirmControlledDrugs: decision.confirmControlledDrugs,
      }),
    onSuccess: (result) => {
      notify.success(`Refill ${result.prescription.prescriptionNumber} issued`, 'It is now in the pharmacy queue.');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      void printServerDocument(prescriptionDocument(result.prescription.id)).catch((printError: unknown) =>
        notify.error(printError, 'Printing failed'),
      );
      onClose();
    },
    onError: (e) => {
      if (isApiError(e)) {
        const found = alertsFromProblem(e.code, e.data);
        if (found) {
          setAlerts(found);
          setError('The safety check needs your decision before the refill can be issued.');
          return;
        }
        setError(e.userMessage);
      } else {
        notify.error(e, 'Could not issue the refill');
      }
    },
  });

  return (
    <DialogBody onKeyDown={ignoreEscapeWhile(refill.isPending)}>
      <DialogTitle>Issue a refill</DialogTitle>
      <DialogContent className={styles.form}>
        {!rx ? (
          <LoadingState />
        ) : (
          <>
            <Text>
              {rx.prescriptionNumber} · {rx.refillsRemaining} of {rx.refillsAllowed} refills remaining · prescribed by{' '}
              {rx.practitionerName ?? 'the doctor'}
            </Text>
            <ul className={styles.lines}>
              {rx.items.map((i) => (
                <li key={i.id}>
                  <Text weight="semibold">{i.drugName}</Text>{' '}
                  <Caption1>
                    {directionsText({
                      dose: i.dose,
                      frequency: i.frequency,
                      customFrequency: i.customFrequency,
                      durationDays: i.durationDays,
                      route: i.route,
                      instructions: i.instructions,
                    })}
                  </Caption1>
                </li>
              ))}
            </ul>
            {rx.refillsRemaining <= 0 && (
              <MessageBar intent="warning">
                <MessageBarBody>No refills remain on this prescription. A new prescription is needed.</MessageBarBody>
              </MessageBar>
            )}
            {(alerts !== null || hasControlled) && (
              <SafetyReview
                grouped={grouped}
                requirements={requirements}
                decision={decision}
                onDecisionChange={setDecision}
                items={rx.items}
                controlledItems={rx.items.filter((i) => i.isControlledDrug).map((i) => i.drugName)}
                disabled={refill.isPending}
              />
            )}
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
          </>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={refill.isPending}>
          Cancel
        </Button>
        <Button
          appearance="primary"
          icon={refill.isPending ? <Spinner size="tiny" /> : <ArrowSyncRegular />}
          disabled={!rx || rx.refillsRemaining <= 0 || blockers.length > 0 || refill.isPending}
          // Keeps focus while issuing, and when the safety check asks for a decision: a disabled button drops it.
          disabledFocusable={refill.isPending || blockers.length > 0}
          onClick={() => {
            setError(null);
            refill.mutate();
          }}
        >
          Issue refill and print
        </Button>
      </DialogActions>
    </DialogBody>
  );
}

/** Prescriptions of a patient (or one encounter): open, print, download as a PDF, repeat, refill or cancel. */
export function PrescriptionsList({
  patientId,
  encounterId = null,
  excludeDrafts = false,
}: {
  patientId: string;
  encounterId?: string | null;
  excludeDrafts?: boolean;
}) {
  const notify = useNotify();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const documents = useServerDocuments();
  const canWrite = usePermission('clinical.prescriptions.write');
  const canSign = usePermission('clinical.prescriptions.sign');
  const canRefill = usePermission('clinical.prescriptions.refill');
  const [page, setPage] = useState(1);
  const [cancelling, setCancelling] = useState<PrescriptionListItem | null>(null);
  const [refilling, setRefilling] = useState<string | null>(null);
  const list = useQuery(prescriptionQueries.list(patientId, { encounterId, page }));
  const rows = (list.data?.items ?? []).filter((p) => !excludeDrafts || p.status !== 'Draft');

  const open = (id: string) =>
    void navigate({ to: '/emr/prescriptions/$prescriptionId', params: { prescriptionId: id } });

  const repeat = useMutation({
    mutationFn: (id: string) => prescriptionsApi.repeat(id),
    onSuccess: (draft) => {
      notify.success(`Draft ${draft.prescriptionNumber} created`, 'Review the medicines, then check and sign it.');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      open(draft.id);
    },
    onError: (error) => notify.error(error, 'Could not repeat the prescription'),
  });

  const cancel = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => prescriptionsApi.cancel(id, reason),
    onSuccess: () => {
      setCancelling(null);
      notify.success('Prescription cancelled', 'The pharmacy has been told.');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    },
    onError: (error) => notify.error(error, 'Could not cancel the prescription'),
  });

  return (
    <>
      <DataTable
        ariaLabel="Prescriptions"
        rows={list.data ? rows : undefined}
        loading={list.isLoading}
        error={list.error}
        onRetry={() => void list.refetch()}
        getRowId={(p) => p.id}
        onRowClick={(p) => open(p.id)}
        size="small"
        columns={[
          {
            key: 'number',
            header: 'Prescription',
            width: 150,
            render: (p) => <Text font="monospace">{p.prescriptionNumber}</Text>,
          },
          { key: 'date', header: 'Date', width: 170, render: (p) => formatDateTime(p.signedAt ?? p.prescribedAt) },
          { key: 'status', header: 'Status', width: 110, render: (p) => <StatusBadge status={p.status} /> },
          { key: 'drugs', header: 'Medicines', render: (p) => p.drugs.join(', ') },
          { key: 'doctor', header: 'Doctor', priority: 'low', render: (p) => p.practitionerName ?? '—' },
          {
            key: 'setting',
            header: 'Setting',
            priority: 'low',
            width: 90,
            render: (p) => (p.isInpatient ? 'IP' : 'OP'),
          },
        ]}
        rowActions={(p) => (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button
                size="small"
                appearance="subtle"
                icon={<MoreHorizontalRegular />}
                aria-label={`Actions for ${p.prescriptionNumber}`}
                {...restoreFocusTarget}
              />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem icon={<OpenRegular />} onClick={() => open(p.id)}>
                  Open
                </MenuItem>
                {p.status === 'Signed' && (
                  <>
                    <MenuItem
                      icon={<PrintRegular />}
                      onClick={() => documents.print.mutate(prescriptionDocument(p.id))}
                    >
                      Print (A5)
                    </MenuItem>
                    <MenuItem
                      icon={<ArrowDownloadRegular />}
                      onClick={() => documents.downloadPdf.mutate(prescriptionDocument(p.id))}
                    >
                      Download PDF
                    </MenuItem>
                  </>
                )}
                {canWrite && p.status !== 'Draft' && (
                  <MenuItem icon={<ArrowRepeatAllRegular />} onClick={() => repeat.mutate(p.id)}>
                    Repeat as a new draft
                  </MenuItem>
                )}
                {canRefill && p.status === 'Signed' && (
                  <MenuItem icon={<ArrowSyncRegular />} onClick={() => setRefilling(p.id)}>
                    Issue refill
                  </MenuItem>
                )}
                {canSign && p.status !== 'Cancelled' && (
                  <MenuItem icon={<DismissRegular />} onClick={() => setCancelling(p)}>
                    Cancel prescription
                  </MenuItem>
                )}
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        emptyTitle="No prescriptions"
        emptyDescription={
          encounterId
            ? 'Signed prescriptions of this encounter appear here.'
            : 'Prescriptions written in encounters appear here.'
        }
        page={page}
        pageSize={20}
        totalCount={list.data?.totalCount}
        onPageChange={setPage}
      />
      <ReasonDialog
        open={cancelling !== null}
        title={`Cancel ${cancelling?.prescriptionNumber ?? 'prescription'}?`}
        description={
          cancelling?.status === 'Signed'
            ? 'It is withdrawn from the pharmacy queue. Medicines already dispensed are not affected.'
            : 'The draft is discarded.'
        }
        confirmLabel="Cancel prescription"
        destructive
        busy={cancel.isPending}
        onConfirm={(reason) => cancelling && cancel.mutate({ id: cancelling.id, reason })}
        onCancel={() => setCancelling(null)}
      />
      <RefillDialog prescriptionId={refilling} patientId={patientId} onClose={() => setRefilling(null)} />
    </>
  );
}
