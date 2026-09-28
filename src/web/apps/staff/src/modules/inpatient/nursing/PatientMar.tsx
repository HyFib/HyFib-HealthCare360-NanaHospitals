import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ChevronLeftRegular, ChevronRightRegular, PillRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatTime, isApiError, todayIso } from '@hc360/core';
import { DataTable, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { COMMON_ROUTES, MAR_STATUS_LABEL, nursingApi, nursingKeys, nursingQueries, type MarEntryDto, type MarOutcome, type MedicationOrderDto } from '../nursingApi';
import { addDays, fromIstInput, istDate, toIstInput } from '../time';
import { RecordDoseDialog, type DoseToRecord } from './RecordDoseDialog';

const useStyles = makeStyles({
  toolbar: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', marginBottom: '12px' },
  dateNav: { display: 'flex', gap: '4px', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  two: { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: '12px', '@media (max-width: 520px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
});

function statusTone(entry: MarEntryDto): 'success' | 'danger' | 'warning' | 'informative' | 'brand' {
  if (entry.status === 'Due') {
    return entry.isOverdue ? 'danger' : 'warning';
  }
  return entry.status === 'Given' ? 'success' : entry.status === 'Cancelled' ? 'informative' : 'warning';
}

/** One patient's Medication Administration Record for a day (plus older doses still waiting to be recorded). */
export function PatientMar({ admissionId, patientName }: { admissionId: string; patientName: string }) {
  const styles = useStyles();
  const canRecord = usePermission('nursing.mar');
  const today = todayIso();
  const [date, setDate] = useState(today);
  const mar = useQuery(nursingQueries.mar(admissionId, date));
  const orders = useQuery(nursingQueries.orders(admissionId, false));
  const [recording, setRecording] = useState<{ dose: DoseToRecord; outcome: MarOutcome } | null>(null);
  const [adHoc, setAdHoc] = useState(false);
  const controlled = new Set((orders.data ?? []).filter((o) => o.isControlledDrug).map((o) => o.id));

  const open = (entry: MarEntryDto, outcome: MarOutcome) =>
    setRecording({
      outcome,
      dose: {
        id: entry.id,
        drugName: entry.drugName,
        dose: entry.dose,
        route: entry.route,
        scheduledAt: entry.scheduledAt,
        version: entry.version,
        admissionId,
        patientName,
        isOverdue: entry.isOverdue,
        isControlledDrug: entry.medicationOrderId ? controlled.has(entry.medicationOrderId) : undefined,
        batchNumber: entry.batchNumber,
      },
    });

  const rows = mar.data ?? [];
  const pending = rows.filter((e) => e.status === 'Due').length;

  return (
    <div>
      <div className={styles.toolbar}>
        <div className={styles.dateNav}>
          <Button icon={<ChevronLeftRegular />} appearance="subtle" aria-label="Previous day" onClick={() => setDate(addDays(date, -1))} />
          <Text weight="semibold">{date === today ? 'Today' : formatDateOnly(date)}</Text>
          <Button icon={<ChevronRightRegular />} appearance="subtle" aria-label="Next day" disabled={date >= today} onClick={() => setDate(addDays(date, 1))} />
          {mar.data && <Caption1 className={styles.muted}>{pending ? `${pending} to record` : 'All doses recorded'}</Caption1>}
        </div>
        {canRecord && (
          <Button icon={<AddRegular />} onClick={() => setAdHoc(true)}>
            SOS / STAT dose
          </Button>
        )}
      </div>
      <DataTable
        ariaLabel="Medication administration record"
        rows={mar.data}
        loading={mar.isLoading}
        error={mar.error}
        onRetry={() => void mar.refetch()}
        getRowId={(e) => e.id}
        size="small"
        columns={[
          {
            key: 'time',
            header: 'Due',
            width: 110,
            render: (e) => (
              <span>
                <Text weight="semibold">{formatTime(e.scheduledAt)}</Text>
                {istDate(e.scheduledAt) !== date && e.status === 'Due' && <Caption1 block>{formatDateOnly(istDate(e.scheduledAt))}</Caption1>}
              </span>
            ),
          },
          {
            key: 'drug',
            header: 'Medicine',
            render: (e) => (
              <span>
                <Text weight="semibold">{e.drugName}</Text>{' '}
                {e.isAdHoc && (
                  <Badge size="small" appearance="outline">
                    SOS / STAT
                  </Badge>
                )}
                <Caption1 block>
                  {e.dose}
                  {e.route ? ` · ${e.route}` : ''}
                  {e.notes ? ` · ${e.notes}` : ''}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'status',
            header: 'Status',
            width: 130,
            render: (e) => <StatusBadge status={e.status} label={e.status === 'Due' && e.isOverdue ? 'Overdue' : MAR_STATUS_LABEL[e.status]} tone={statusTone(e)} />,
          },
          {
            key: 'by',
            header: 'Recorded',
            priority: 'low',
            render: (e) =>
              e.status === 'Due' ? (
                '—'
              ) : (
                <span>
                  {e.administeredAt ? formatTime(e.administeredAt) : ''} {e.recordedByName ?? ''}
                  {e.reason && <Caption1 block>{e.reason}</Caption1>}
                  {e.witnessName && <Caption1 block>Witness: {e.witnessName}</Caption1>}
                </span>
              ),
          },
        ]}
        rowActions={
          canRecord
            ? (e) =>
                e.status === 'Due' ? (
                  <span style={{ display: 'inline-flex', gap: 6 }}>
                    <Button size="small" appearance="primary" onClick={() => open(e, 'Given')}>
                      Given
                    </Button>
                    <Button size="small" onClick={() => open(e, 'Held')}>
                      Not given
                    </Button>
                  </span>
                ) : null
            : undefined
        }
        emptyTitle={date === today ? 'No doses charted for today' : 'No doses on this day'}
        emptyDescription="Doses are scheduled when the pharmacy issues the medicine, or when a ward order is added on the Medicines tab."
      />
      {recording && <RecordDoseDialog dose={recording.dose} initialOutcome={recording.outcome} onClose={() => setRecording(null)} />}
      {adHoc && <AdHocDoseDialog admissionId={admissionId} orders={(orders.data ?? []).filter((o) => o.status === 'Active')} onClose={() => setAdHoc(false)} />}
    </div>
  );
}

/** An SOS/STAT or otherwise unscheduled dose; "given now" records it as given in the same step. */
function AdHocDoseDialog({ admissionId, orders, onClose }: { admissionId: string; orders: MedicationOrderDto[]; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const sos = [...orders].sort((a, b) => Number(b.isAsNeeded) - Number(a.isAsNeeded));
  const [orderId, setOrderId] = useState(sos[0]?.id ?? '');
  const order = orders.find((o) => o.id === orderId) ?? null;
  const [drugName, setDrugName] = useState('');
  const [dose, setDose] = useState(sos[0]?.dose ?? '');
  const [route, setRoute] = useState(sos[0]?.route ?? '');
  const [givenNow, setGivenNow] = useState(true);
  const [limits] = useState(() => ({ now: toIstInput(new Date()) }));
  const [scheduledAt, setScheduledAt] = useState(limits.now);
  const [reason, setReason] = useState('');
  const [batch, setBatch] = useState('');
  const [witness, setWitness] = useState('');
  const [notes, setNotes] = useState('');

  const pickOrder = (id: string) => {
    setOrderId(id);
    const next = orders.find((o) => o.id === id);
    setDose(next?.dose ?? '');
    setRoute(next?.route ?? '');
  };

  const add = useMutation({
    mutationFn: () =>
      nursingApi.addAdHocDose(admissionId, {
        medicationOrderId: order?.id ?? null,
        drugName: order ? null : drugName.trim(),
        dose: dose.trim(),
        route: route.trim() || null,
        scheduledAt: givenNow ? null : fromIstInput(scheduledAt),
        givenNow,
        reason: reason.trim() || null,
        batchNumber: batch.trim() || null,
        witnessName: witness.trim() || null,
        notes: notes.trim() || null,
      }),
    onSuccess: () => {
      notify.success(givenNow ? 'Dose recorded as given' : 'Dose added to the chart');
      void queryClient.invalidateQueries({ queryKey: nursingKeys.admission(admissionId) });
      onClose();
    },
  });

  const needsWitness = givenNow && Boolean(order?.isControlledDrug);
  const valid = (order !== null || drugName.trim().length > 0) && dose.trim().length > 0 && (!needsWitness || witness.trim().length > 0) && (givenNow || fromIstInput(scheduledAt) !== null);

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !add.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !add.isPending) {
              add.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>SOS / STAT dose</DialogTitle>
            <DialogContent className={styles.stack}>
              <Field label="Medicine">
                <Select value={orderId} onChange={(_, d) => pickOrder(d.value)}>
                  {sos.map((o) => (
                    <option key={o.id} value={o.id}>
                      {o.drugName}
                      {o.strength ? ` ${o.strength}` : ''} · {o.frequency}
                      {o.isAsNeeded ? ' (SOS)' : ''}
                    </option>
                  ))}
                  <option value="">Another medicine (verbal order)</option>
                </Select>
              </Field>
              {!order && (
                <Field label="Medicine name" required hint="Record the doctor's verbal order in a nursing note as well.">
                  <Input value={drugName} onChange={(_, d) => setDrugName(d.value)} maxLength={200} autoFocus />
                </Field>
              )}
              <div className={styles.two}>
                <Field label="Dose" required>
                  <Input value={dose} onChange={(_, d) => setDose(d.value)} maxLength={64} placeholder="e.g. 500 mg" />
                </Field>
                <Field label="Route">
                  <Input value={route} onChange={(_, d) => setRoute(d.value)} maxLength={32} list="mar-routes" />
                  <datalist id="mar-routes">
                    {COMMON_ROUTES.map((r) => (
                      <option key={r} value={r} />
                    ))}
                  </datalist>
                </Field>
              </div>
              <Field label="Why was it needed?">
                <Textarea value={reason} onChange={(_, d) => setReason(d.value)} maxLength={300} rows={2} placeholder="e.g. Fever 101 °F, pain score 7" />
              </Field>
              <Switch checked={givenNow} onChange={(_, d) => setGivenNow(d.checked)} label="Given now" />
              {!givenNow && (
                <Field label="Give at" required hint="Adds the dose to the chart to be recorded later">
                  <Input type="datetime-local" value={scheduledAt} onChange={(_, d) => setScheduledAt(d.value)} />
                </Field>
              )}
              {givenNow && (
                <div className={styles.two}>
                  <Field label="Batch number">
                    <Input value={batch} onChange={(_, d) => setBatch(d.value)} maxLength={64} />
                  </Field>
                  <Field label="Witness (second nurse)" required={needsWitness} hint={needsWitness ? 'Required: controlled drug' : undefined}>
                    <Input value={witness} onChange={(_, d) => setWitness(d.value)} maxLength={200} />
                  </Field>
                </div>
              )}
              <Field label="Notes">
                <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={500} rows={2} />
              </Field>
              {add.error && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>{isApiError(add.error) ? add.error.userMessage : 'The dose could not be recorded.'}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={add.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" icon={add.isPending ? <Spinner size="tiny" /> : <PillRegular />} disabled={!valid || add.isPending}>
                {givenNow ? 'Record as given' : 'Add to chart'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
