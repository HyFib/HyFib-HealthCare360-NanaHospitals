import {
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
  SpinButton,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime, isApiError } from '@hc360/core';
import { DataTable, KpiCard, StatusBadge } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import {
  CAUSE_LABELS,
  DOWNTIME_CAUSES,
  DOWNTIME_KINDS,
  downtimeApi,
  downtimeKeys,
  downtimeQueries,
  durationText,
  isoToLocal,
  KIND_LABELS,
  localToIso,
  STATUS_LABELS,
  type DowntimeCause,
  type DowntimeEvent,
  type DowntimeKind,
} from './downtimeApi';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))' },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const problemOf = (error: unknown, fallback: string) => (isApiError(error) ? error.message : fallback);

/** The last 12 months' downtime figures, as the quality committee reviews them. */
export function DowntimeFigures() {
  const styles = useStyles();
  const summary = useQuery(downtimeQueries.summary());
  const s = summary.data;
  if (!s) {
    return null;
  }

  return (
    <div className={styles.kpis}>
      <KpiCard
        label="Unplanned downtime"
        value={s.unplanned}
        hint={`${durationText(s.unplannedMinutes)} in all; longest ${durationText(s.longestUnplannedMinutes)}`}
      />
      <KpiCard label="Available (unplanned downtime only)" value={`${s.unplannedAvailabilityPercent}%`} />
      <KpiCard
        label="Mean time to restore"
        value={s.meanMinutesToRestore === null ? '—' : durationText(s.meanMinutesToRestore)}
      />
      <KpiCard
        label="Mean time to back-entry"
        value={s.meanHoursToBackEntry === null ? '—' : `${s.meanHoursToBackEntry} h`}
        hint={s.backEntryPending > 0 ? `${s.backEntryPending} waiting for back-entry` : undefined}
      />
      <KpiCard
        label="Drills"
        value={s.drills}
        hint={s.lastDrillAt ? `Last ${formatDateTime(s.lastDrillAt)}` : 'No drill recorded yet'}
      />
    </div>
  );
}

/** The downtime of the last 12 months, newest first; a row opens it to end it or sign off its back-entry. */
export function DowntimeTable({ onOpen }: { onOpen: (event: DowntimeEvent) => void }) {
  const styles = useStyles();
  const list = useQuery(downtimeQueries.list());
  return (
    <DataTable
      ariaLabel="Downtime"
      rows={list.data}
      loading={list.isLoading}
      error={list.error}
      onRetry={() => void list.refetch()}
      getRowId={(e) => e.id}
      onRowClick={(e) => e.status !== 'Closed' && onOpen(e)}
      columns={[
        {
          key: 'downtime',
          header: 'Downtime',
          render: (e) => (
            <div className={styles.cell}>
              <Text weight="semibold">
                {e.number} · {KIND_LABELS[e.kind]}, {CAUSE_LABELS[e.cause].toLowerCase()}
              </Text>
              <Caption1 className={styles.muted}>{e.affected}</Caption1>
            </div>
          ),
        },
        {
          key: 'when',
          header: 'When',
          width: 220,
          render: (e) => (
            <div className={styles.cell}>
              <Text>{formatDateTime(e.startedAt)}</Text>
              <Caption1 className={styles.muted}>
                {e.endedAt ? durationText(e.minutes) : `Ongoing, ${durationText(e.minutes)} so far`}
              </Caption1>
            </div>
          ),
        },
        {
          key: 'status',
          header: 'Status',
          width: 200,
          render: (e) => (
            <div className={styles.cell}>
              <StatusBadge
                status={STATUS_LABELS[e.status]}
                tone={e.status === 'Closed' ? 'success' : e.status === 'Ongoing' ? 'danger' : 'warning'}
              />
              {e.status === 'Closed' && (
                <Caption1 className={styles.muted}>{e.paperRecords} paper records entered</Caption1>
              )}
            </div>
          ),
        },
      ]}
      emptyTitle="No downtime recorded"
      emptyDescription="Record each spell of working without HC360, and each drill of the downtime procedure."
      size="small"
    />
  );
}

/** Recording a downtime, usually afterwards (HC360 was down), with its end when it is over. */
export function RecordDowntimeDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<DowntimeKind>('Unplanned');
  const [cause, setCause] = useState<DowntimeCause>('ServerFailure');
  const [affected, setAffected] = useState('All of HC360');
  const [description, setDescription] = useState('');
  const [startedAt, setStartedAt] = useState(() => isoToLocal(new Date(Date.now() - 60 * 60_000)));
  const [endedAt, setEndedAt] = useState('');
  const [actions, setActions] = useState('');
  const record = useMutation({
    mutationFn: () =>
      downtimeApi.record({
        kind,
        cause,
        affected: affected.trim(),
        description: description.trim() || null,
        startedAt: localToIso(startedAt)!,
        endedAt: endedAt ? localToIso(endedAt) : null,
        actionsTaken: actions.trim() || null,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: downtimeKeys.all });
      onClose();
    },
  });
  const busy = record.isPending;
  const valid =
    affected.trim().length > 0 && localToIso(startedAt) !== null && (!endedAt || localToIso(endedAt) !== null);

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>Record downtime</DialogTitle>
          <DialogContent className={styles.body}>
            <div className={styles.row}>
              <Field label="Kind" required>
                <Select value={kind} onChange={(_, data) => setKind(data.value as DowntimeKind)}>
                  {DOWNTIME_KINDS.map((k) => (
                    <option key={k} value={k}>
                      {KIND_LABELS[k]}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Cause" required>
                <Select value={cause} onChange={(_, data) => setCause(data.value as DowntimeCause)}>
                  {DOWNTIME_CAUSES.map((c) => (
                    <option key={c} value={c}>
                      {CAUSE_LABELS[c]}
                    </option>
                  ))}
                </Select>
              </Field>
            </div>
            <Field
              label="What could not be used"
              hint='For example "All of HC360" or "Laboratory and pharmacy"'
              required
            >
              <Input value={affected} maxLength={300} onChange={(_, data) => setAffected(data.value)} />
            </Field>
            <Field label="What happened">
              <Textarea value={description} maxLength={2000} onChange={(_, data) => setDescription(data.value)} />
            </Field>
            <div className={styles.row}>
              <Field label="Started" required>
                <Input type="datetime-local" value={startedAt} onChange={(_, data) => setStartedAt(data.value)} />
              </Field>
              <Field label="Ended" hint="Leave empty while it lasts">
                <Input type="datetime-local" value={endedAt} onChange={(_, data) => setEndedAt(data.value)} />
              </Field>
            </div>
            {endedAt && (
              <Field label="What was done">
                <Textarea value={actions} maxLength={2000} onChange={(_, data) => setActions(data.value)} />
              </Field>
            )}
            {record.error && (
              <MessageBar intent="error">
                <MessageBarBody>{problemOf(record.error, 'The downtime could not be recorded.')}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || busy} onClick={() => record.mutate()}>
              Record
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** Ending an ongoing downtime, or signing off the back-entry of one that has ended. */
export function DowntimeStepDialog({ downtime, onClose }: { downtime: DowntimeEvent; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const ending = downtime.status === 'Ongoing';
  const [endedAt, setEndedAt] = useState(() => isoToLocal(new Date()));
  const [actions, setActions] = useState('');
  const [records, setRecords] = useState(0);
  const [note, setNote] = useState('');
  const step = useMutation({
    mutationFn: () =>
      ending
        ? downtimeApi.end(downtime.id, {
            endedAt: localToIso(endedAt)!,
            actionsTaken: actions.trim() || null,
            version: downtime.version,
          })
        : downtimeApi.signOff(downtime.id, {
            paperRecords: records,
            note: note.trim() || null,
            version: downtime.version,
          }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: downtimeKeys.all });
      onClose();
    },
  });
  const busy = step.isPending;
  const valid = ending ? localToIso(endedAt) !== null : Number.isInteger(records) && records >= 0;

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>
            {ending ? 'End' : 'Sign off the back-entry of'} {downtime.number}
          </DialogTitle>
          <DialogContent className={styles.body}>
            <Text>
              {KIND_LABELS[downtime.kind]} downtime of {downtime.affected}, from {formatDateTime(downtime.startedAt)}
              {downtime.endedAt ? ` to ${formatDateTime(downtime.endedAt)}` : ''}.
            </Text>
            {ending ? (
              <>
                <Field label="Ended" required>
                  <Input type="datetime-local" value={endedAt} onChange={(_, data) => setEndedAt(data.value)} />
                </Field>
                <Field label="What was done">
                  <Textarea value={actions} maxLength={2000} onChange={(_, data) => setActions(data.value)} />
                </Field>
              </>
            ) : (
              <>
                <Text>
                  Sign off once every paper record kept during the downtime has been entered in HC360 and checked
                  against the paper.
                </Text>
                <Field label="Paper records entered" required>
                  <SpinButton
                    value={records}
                    min={0}
                    max={100000}
                    onChange={(_, data) => {
                      const next = data.value ?? Number(data.displayValue);
                      if (Number.isFinite(next)) {
                        setRecords(Math.trunc(next));
                      }
                    }}
                  />
                </Field>
                <Field label="Note">
                  <Textarea value={note} maxLength={1000} onChange={(_, data) => setNote(data.value)} />
                </Field>
              </>
            )}
            {step.error && (
              <MessageBar intent="error">
                <MessageBarBody>{problemOf(step.error, 'The downtime could not be updated.')}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || busy} onClick={() => step.mutate()}>
              {ending ? 'End downtime' : 'Sign off'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
