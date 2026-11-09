import {
  Button,
  Caption1,
  Checkbox,
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
  MessageBarTitle,
  Select,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { formatDate, formatDateTime, isApiError } from '@hc360/core';
import { DataTable, LoadingState, StatusBadge } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import {
  DEVICE_LABELS,
  DEVICES,
  HAI_LABELS,
  haiApi,
  haiKeys,
  haiQueries,
  isoToLocal,
  localToIso,
  REMOVAL_LABELS,
  REMOVAL_REASONS,
  STATUS_LABELS,
  type DeviceEpisode,
  type DeviceRemovalReason,
  type HaiCaseDetail,
  type HaiCaseStatus,
  type InvasiveDevice,
} from './haiApi';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
  muted: { color: tokens.colorNeutralForeground3 },
  group: { display: 'flex', flexDirection: 'column', gap: '2px' },
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: '8px' },
});

const problemOf = (error: unknown, fallback: string) => (isApiError(error) ? error.message : fallback);

const statusTone = (status: HaiCaseStatus) =>
  status === 'Confirmed' ? 'danger' : status === 'RuledOut' ? 'success' : 'warning';

const GROUP_LABELS: Record<string, string> = {
  organism: 'Organism (one of)',
  symptom: 'Symptom',
  attribution: 'Attribution',
  culture: 'Culture',
  sign: 'Signs (at least one)',
  'after-removal': 'Signs that count only once the catheter is out',
  imaging: 'Imaging',
  systemic: 'Systemic signs (at least one)',
  pulmonary: 'Pulmonary signs (at least two)',
  window: 'Surveillance window',
  depth: 'Depth (exactly one)',
  finding: 'Findings (at least one)',
  site: 'Organ/space site',
};

/** The cases under surveillance, those to review first; a row opens the review. */
export function HaiCasesTable({ status, onOpen }: { status?: HaiCaseStatus; onOpen: (id: string) => void }) {
  const styles = useStyles();
  const cases = useQuery(haiQueries.cases(status));
  return (
    <DataTable
      ariaLabel="Infection surveillance cases"
      rows={cases.data}
      loading={cases.isLoading}
      error={cases.error}
      onRetry={() => void cases.refetch()}
      getRowId={(c) => c.id}
      onRowClick={(c) => onOpen(c.id)}
      columns={[
        {
          key: 'case',
          header: 'Case',
          render: (c) => (
            <div className={styles.cell}>
              <Text weight="semibold">
                {c.caseNumber} · {HAI_LABELS[c.type]}
              </Text>
              <Caption1 className={styles.muted}>
                {c.uhid ?? 'Patient'}
                {c.origin === 'CultureAlert' ? ' · raised from a positive culture' : ' · raised by the team'}
              </Caption1>
            </div>
          ),
        },
        { key: 'event', header: 'Date of event', width: 140, render: (c) => formatDate(c.eventOn) },
        {
          key: 'organism',
          header: 'Organism',
          priority: 'low',
          render: (c) => c.organism ?? '—',
        },
        {
          key: 'status',
          header: 'Status',
          width: 210,
          render: (c) => (
            <div className={styles.cell}>
              <StatusBadge status={c.status} tone={statusTone(c.status)} label={STATUS_LABELS[c.status]} />
              {c.status === 'Suspected' && (
                <Caption1 className={styles.muted}>
                  {c.meetsDefinition ? 'Checklist meets the definition' : 'Checklist incomplete'}
                </Caption1>
              )}
            </div>
          ),
        },
      ]}
      emptyTitle="No cases"
      emptyDescription="A verified culture that grows an organism on a patient with a device, or after an operation, raises a case here."
      size="small"
    />
  );
}

/** Reviewing a case against its NHSN checklist, then confirming it, ruling it out, or reopening it. */
export function HaiCaseDialog({ caseId, onClose }: { caseId: string; onClose: () => void }) {
  const detail = useQuery(haiQueries.case(caseId));
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 760 }}>
        {detail.data ? (
          <CaseReview key={detail.data.version} detail={detail.data} onClose={onClose} />
        ) : (
          <DialogBody>
            <DialogTitle>Infection surveillance case</DialogTitle>
            <DialogContent>
              {detail.error ? (
                <MessageBar intent={'error'}>
                  <MessageBarBody>
                    <MessageBarTitle>Not saved</MessageBarTitle>
                    {problemOf(detail.error, 'The case could not be opened.')}
                  </MessageBarBody>
                </MessageBar>
              ) : (
                <LoadingState />
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose}>Close</Button>
            </DialogActions>
          </DialogBody>
        )}
      </DialogSurface>
    </Dialog>
  );
}

function CaseReview({ detail: c, onClose }: { detail: HaiCaseDetail; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const open = c.status === 'Suspected';
  const [met, setMet] = useState(() => new Set(c.criteria.filter((x) => x.met).map((x) => x.key)));
  const [eventOn, setEventOn] = useState(c.eventOn);
  const [deviceId, setDeviceId] = useState(c.device?.id ?? '');
  const [operatedOn, setOperatedOn] = useState(c.operatedOn ?? '');
  const [surgery, setSurgery] = useState(c.surgeryReference ?? '');
  const [note, setNote] = useState('');
  const surgeries = useQuery({ ...haiQueries.surgeries(c.id), enabled: c.type === 'Ssi' && open });
  const done = () => void queryClient.invalidateQueries({ queryKey: haiKeys.all });
  const save = useMutation({
    mutationFn: () =>
      haiApi.review(c.id, {
        criteriaMet: [...met],
        eventOn,
        deviceEpisodeId: c.type === 'Ssi' ? null : deviceId || null,
        surgeryBookingId:
          c.type === 'Ssi'
            ? (surgeries.data?.find((s) => s.reference === surgery)?.bookingId ?? c.surgeryBookingId)
            : null,
        surgeryReference: c.type === 'Ssi' ? surgery.trim() || null : null,
        operatedOn: c.type === 'Ssi' ? operatedOn || null : null,
        notes: c.notes,
        version: c.version,
      }),
    onSuccess: done,
  });
  const decide = useMutation({
    mutationFn: (decision: 'confirm' | 'rule-out' | 'reopen') =>
      haiApi.decide(c.id, decision, note.trim() || null, c.version),
    onSuccess: () => {
      done();
      onClose();
    },
  });
  const dirty =
    eventOn !== c.eventOn ||
    deviceId !== (c.device?.id ?? '') ||
    operatedOn !== (c.operatedOn ?? '') ||
    surgery !== (c.surgeryReference ?? '') ||
    c.criteria.some((x) => x.met !== met.has(x.key));
  const busy = save.isPending || decide.isPending;
  const groups = [...new Set(c.criteria.map((x) => x.group))];
  const error = save.error ?? decide.error;

  return (
    <DialogBody>
      <DialogTitle>
        {c.caseNumber} · {HAI_LABELS[c.type]}
      </DialogTitle>
      <DialogContent className={styles.body}>
        <div className={styles.facts}>
          <Text>
            <Text weight="semibold">Patient</Text> {c.patientName ?? '—'} {c.uhid ? `(${c.uhid})` : ''}
          </Text>
          <Text>
            <Text weight="semibold">Status</Text> {STATUS_LABELS[c.status]}
          </Text>
          {c.organism && (
            <Text>
              <Text weight="semibold">Culture</Text> {c.specimen ?? 'Culture'}: {c.organism}
              {c.specimenCollectedAt ? `, collected ${formatDateTime(c.specimenCollectedAt)}` : ''}
            </Text>
          )}
          {c.type !== 'Ssi' && (
            <Text>
              <Text weight="semibold">Device</Text>{' '}
              {c.device
                ? `${DEVICE_LABELS[c.device.device]} in from ${formatDateTime(c.device.insertedAt)}; device day ${c.dayOfDevice} on the date of event`
                : 'None chosen'}
            </Text>
          )}
          {c.type === 'Ssi' && (
            <Text>
              <Text weight="semibold">Operation</Text> {c.surgeryReference ?? 'None chosen'}
              {c.daysAfterOperation !== null ? `, day ${c.daysAfterOperation} after it` : ''}
            </Text>
          )}
        </div>
        <div className={styles.row}>
          <Field label="Date of event" hint="The day the first element of the definition was met" required>
            <Input type="date" value={eventOn} disabled={!open} onChange={(_, data) => setEventOn(data.value)} />
          </Field>
          {c.type !== 'Ssi' ? (
            <Field label="Device episode">
              <Select value={deviceId} disabled={!open} onChange={(_, data) => setDeviceId(data.value)}>
                <option value="">None</option>
                {c.patientDevices.map((d) => (
                  <option key={d.id} value={d.id}>
                    {DEVICE_LABELS[d.device]} from {formatDate(d.insertedAt)}
                    {d.removedAt ? ` to ${formatDate(d.removedAt)}` : ' (in place)'}
                  </option>
                ))}
              </Select>
            </Field>
          ) : (
            <>
              <Field label="Operation">
                <Select
                  value={surgery}
                  disabled={!open}
                  onChange={(_, data) => {
                    setSurgery(data.value);
                    const chosen = surgeries.data?.find((s) => s.reference === data.value);
                    if (chosen) {
                      setOperatedOn(chosen.operatedOn);
                    }
                  }}
                >
                  <option value="">None</option>
                  {c.surgeryReference && !surgeries.data?.some((s) => s.reference === c.surgeryReference) && (
                    <option value={c.surgeryReference}>{c.surgeryReference}</option>
                  )}
                  {(surgeries.data ?? []).map((s) => (
                    <option key={s.bookingId} value={s.reference}>
                      {s.reference} ({formatDate(s.operatedOn)})
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Operated on">
                <Input
                  type="date"
                  value={operatedOn}
                  disabled={!open}
                  onChange={(_, data) => setOperatedOn(data.value)}
                />
              </Field>
            </>
          )}
        </div>
        {c.type !== 'Ssi' && c.deviceEligible === false && (
          <MessageBar intent={'warning'}>
            <MessageBarBody>
              <MessageBarTitle>Please note</MessageBarTitle>
              The device was not in place for more than two days on the date of event (or was out for more than a day):
              it cannot be device-associated.
            </MessageBarBody>
          </MessageBar>
        )}
        {groups.map((group) => (
          <fieldset key={group} className={styles.group} style={{ border: 0, padding: 0, margin: 0 }}>
            <legend>
              <Text weight="semibold">{GROUP_LABELS[group] ?? group}</Text>
            </legend>
            {c.criteria
              .filter((x) => x.group === group)
              .map((x) => (
                <Checkbox
                  key={x.key}
                  label={x.text}
                  checked={met.has(x.key)}
                  disabled={!open}
                  onChange={(_, data) =>
                    setMet((prev) => {
                      const next = new Set(prev);
                      if (data.checked) {
                        next.add(x.key);
                      } else {
                        next.delete(x.key);
                      }
                      return next;
                    })
                  }
                />
              ))}
          </fieldset>
        ))}
        <MessageBar intent={c.meetsDefinition ? 'success' : 'info'}>
          <MessageBarBody>
            <MessageBarTitle>Checklist</MessageBarTitle>
            {c.meetsDefinition
              ? 'As saved, the checklist meets the surveillance definition.'
              : `As saved, still needed: ${c.missing.join('; ')}.`}
            {dirty ? ' Save the checklist to check it again.' : ''}
          </MessageBarBody>
        </MessageBar>
        {c.decisionNote && (
          <Caption1 className={styles.muted}>
            {c.reviewedByName ? `${c.reviewedByName}: ` : ''}
            {c.decisionNote}
          </Caption1>
        )}
        <Field label={open ? 'Decision note' : 'Why reopen'} hint={open ? 'Needed to rule out' : undefined}>
          <Textarea value={note} maxLength={1000} onChange={(_, data) => setNote(data.value)} />
        </Field>
        {error && (
          <MessageBar intent={'error'}>
            <MessageBarBody>
              <MessageBarTitle>Not saved</MessageBarTitle>
              {problemOf(error, 'The case could not be saved.')}
            </MessageBarBody>
          </MessageBar>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>
          Close
        </Button>
        {open ? (
          <>
            <Button disabled={busy || !dirty} onClick={() => save.mutate()}>
              Save checklist
            </Button>
            <Button disabled={busy || dirty || !note.trim()} onClick={() => decide.mutate('rule-out')}>
              Rule out
            </Button>
            <Button
              appearance="primary"
              disabled={busy || dirty || !c.meetsDefinition}
              onClick={() => decide.mutate('confirm')}
            >
              Confirm
            </Button>
          </>
        ) : (
          <Button disabled={busy || !note.trim()} onClick={() => decide.mutate('reopen')}>
            Reopen
          </Button>
        )}
      </DialogActions>
    </DialogBody>
  );
}

/** Devices in place (or every recorded one), with their device days; a nurse takes one out from here. */
export function DevicesTable({ inPlace, onRemove }: { inPlace: boolean; onRemove?: (d: DeviceEpisode) => void }) {
  const styles = useStyles();
  const devices = useQuery(haiQueries.devices(inPlace ? true : undefined));
  return (
    <DataTable
      ariaLabel={inPlace ? 'Devices in place' : 'Devices recorded'}
      rows={devices.data}
      loading={devices.isLoading}
      error={devices.error}
      onRetry={() => void devices.refetch()}
      getRowId={(d) => d.id}
      columns={[
        {
          key: 'device',
          header: 'Device',
          render: (d) => (
            <div className={styles.cell}>
              <Text weight="semibold">{DEVICE_LABELS[d.device]}</Text>
              <Caption1 className={styles.muted}>{d.detail ?? '—'}</Caption1>
            </div>
          ),
        },
        {
          key: 'patient',
          header: 'Patient',
          render: (d) => (
            <div className={styles.cell}>
              <Text>{d.uhid ?? '—'}</Text>
              <Caption1 className={styles.muted}>
                {d.admissionNumber}
                {d.wardName ? ` · ${d.wardName}` : ''}
              </Caption1>
            </div>
          ),
        },
        { key: 'in', header: 'In since', width: 180, render: (d) => formatDateTime(d.insertedAt) },
        {
          key: 'days',
          header: 'Device days',
          width: 120,
          align: 'end',
          render: (d) => d.deviceDays.toLocaleString('en-IN'),
        },
        {
          key: 'out',
          header: 'Out',
          priority: 'low',
          render: (d) => (d.removedAt ? `${formatDateTime(d.removedAt)}` : 'In place'),
        },
      ]}
      rowActions={
        onRemove
          ? (d) =>
              d.removedAt ? null : (
                <Button size="small" onClick={() => onRemove(d)}>
                  {d.device === 'Ventilator' ? 'Extubate' : 'Remove'}
                </Button>
              )
          : undefined
      }
      emptyTitle={inPlace ? 'No devices in place' : 'No devices recorded'}
      emptyDescription="Record central lines, urinary catheters and ventilation as they go in: they are the device days of the HAI rates."
      size="small"
    />
  );
}

/** Recording a device put in for an in-patient, by the admission number on the wristband. */
export function InsertDeviceDialog({ onClose }: { onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [admission, setAdmission] = useState('');
  const [device, setDevice] = useState<InvasiveDevice>('CentralLine');
  const [detail, setDetail] = useState('');
  const [insertedAt, setInsertedAt] = useState(() => isoToLocal(new Date()));
  const insert = useMutation({
    mutationFn: () =>
      haiApi.insertDevice({
        admissionId: null,
        admissionNumber: admission.trim(),
        device,
        detail: detail.trim() || null,
        insertedAt: localToIso(insertedAt)!,
        notes: null,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: haiKeys.all });
      onClose();
    },
  });
  const valid = admission.trim().length > 0 && localToIso(insertedAt) !== null;
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !insert.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>Record a device put in</DialogTitle>
          <DialogContent className={styles.body}>
            <Field label="Admission number" hint="As on the wristband" required>
              <Input value={admission} maxLength={32} onChange={(_, data) => setAdmission(data.value)} />
            </Field>
            <div className={styles.row}>
              <Field label="Device" required>
                <Select value={device} onChange={(_, data) => setDevice(data.value as InvasiveDevice)}>
                  {DEVICES.map((d) => (
                    <option key={d} value={d}>
                      {DEVICE_LABELS[d]}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label={device === 'Ventilator' ? 'Ventilated from' : 'Put in'} required>
                <Input type="datetime-local" value={insertedAt} onChange={(_, data) => setInsertedAt(data.value)} />
              </Field>
            </div>
            <Field label="Site and size" hint='For example "Right internal jugular, triple lumen" or "Foley 14 Fr"'>
              <Input value={detail} maxLength={200} onChange={(_, data) => setDetail(data.value)} />
            </Field>
            {insert.error && (
              <MessageBar intent={'error'}>
                <MessageBarBody>
                  <MessageBarTitle>Not saved</MessageBarTitle>
                  {problemOf(insert.error, 'The device could not be recorded.')}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={insert.isPending}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || insert.isPending} onClick={() => insert.mutate()}>
              Record
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** Taking a device out (or extubating), with why. */
export function RemoveDeviceDialog({ device, onClose }: { device: DeviceEpisode; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [removedAt, setRemovedAt] = useState(() => isoToLocal(new Date()));
  const [reason, setReason] = useState<DeviceRemovalReason>('Planned');
  const remove = useMutation({
    mutationFn: () => haiApi.removeDevice(device.id, localToIso(removedAt)!, reason, device.version),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: haiKeys.all });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !remove.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>
            {device.device === 'Ventilator' ? 'Extubate' : 'Remove'}: {DEVICE_LABELS[device.device]}
          </DialogTitle>
          <DialogContent className={styles.body}>
            <Text>
              {device.uhid} · {device.admissionNumber}, in since {formatDateTime(device.insertedAt)}.
            </Text>
            <Field label="Taken out" required>
              <Input type="datetime-local" value={removedAt} onChange={(_, data) => setRemovedAt(data.value)} />
            </Field>
            <Field label="Why" required>
              <Select value={reason} onChange={(_, data) => setReason(data.value as DeviceRemovalReason)}>
                {REMOVAL_REASONS.map((r) => (
                  <option key={r} value={r}>
                    {REMOVAL_LABELS[r]}
                  </option>
                ))}
              </Select>
            </Field>
            {remove.error && (
              <MessageBar intent={'error'}>
                <MessageBarBody>
                  <MessageBarTitle>Not saved</MessageBarTitle>
                  {problemOf(remove.error, 'The removal could not be recorded.')}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={remove.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={localToIso(removedAt) === null || remove.isPending}
              onClick={() => remove.mutate()}
            >
              Record
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

const rate = (value: number | null) =>
  value === null ? '—' : value.toLocaleString('en-IN', { maximumFractionDigits: 2 });

/** The last 12 months: device days, confirmed infections and rates per 1,000 device days (SSI per 100 operations). */
export function HaiRatesTable() {
  const rates = useQuery(haiQueries.rates());
  return (
    <DataTable
      ariaLabel="HAI rates by month"
      rows={rates.data ? [...rates.data].reverse() : undefined}
      loading={rates.isLoading}
      error={rates.error}
      onRetry={() => void rates.refetch()}
      getRowId={(r) => r.month}
      columns={[
        { key: 'month', header: 'Month', render: (r) => <Text weight="semibold">{r.label}</Text> },
        {
          key: 'clabsi',
          header: 'CLABSI / 1,000 line days',
          align: 'end',
          render: (r) => `${rate(r.clabsiRate)} (${r.clabsi} / ${r.centralLineDays})`,
        },
        {
          key: 'cauti',
          header: 'CAUTI / 1,000 catheter days',
          align: 'end',
          render: (r) => `${rate(r.cautiRate)} (${r.cauti} / ${r.urinaryCatheterDays})`,
        },
        {
          key: 'vap',
          header: 'VAP / 1,000 ventilator days',
          align: 'end',
          render: (r) => `${rate(r.vapRate)} (${r.vap} / ${r.ventilatorDays})`,
        },
        {
          key: 'ssi',
          header: 'SSI per 100 operations',
          align: 'end',
          render: (r) => `${rate(r.ssiRate)} (${r.ssi} / ${r.operations})`,
        },
        {
          key: 'dur',
          header: 'Device use (line, catheter, ventilator)',
          priority: 'low',
          render: (r) =>
            `${rate(r.centralLineUtilisation)} · ${rate(r.urinaryCatheterUtilisation)} · ${rate(r.ventilatorUtilisation)}`,
        },
        {
          key: 'open',
          header: 'To review',
          align: 'end',
          width: 100,
          render: (r) => r.suspected.toLocaleString('en-IN'),
        },
      ]}
      emptyTitle="No months"
      size="small"
    />
  );
}
