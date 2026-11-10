import {
  Badge,
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
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Switch,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular, CheckmarkRegular, EditRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { errorMessage } from '@/modules/lab/common/errors';
import {
  checklistProblem,
  HOUSEKEEPING_PERMISSIONS,
  housekeepingApi,
  housekeepingKeys,
  housekeepingQueries,
  KIND_LABEL,
  REQUEST_KINDS,
  REQUEST_STATUS_LABEL,
  RISK_LABEL,
  RISK_ZONES,
  WEEK_DAYS,
  type ChecklistResult,
  type CleaningArea,
  type CleaningFrequency,
  type CleaningRequest,
  type CleaningRequestKind,
  type CleaningRiskZone,
  type CleaningSlot,
  type DayOfWeek,
} from './api';

function DialogError({ error, fallback }: { error: unknown; fallback: string }) {
  return error ? (
    <MessageBar intent="error">
      <MessageBarBody>
        <MessageBarTitle>Not saved</MessageBarTitle>
        {errorMessage(error, fallback)}
      </MessageBarBody>
    </MessageBar>
  ) : null;
}

/** Cleaning asked for by the wards, oldest first by kind, with the response time against the kind's target. */
export function RequestsPanel() {
  const canRequest = usePermission(HOUSEKEEPING_PERMISSIONS.request);
  const canWork = usePermission(HOUSEKEEPING_PERMISSIONS.work);
  const canVerify = usePermission(HOUSEKEEPING_PERMISSIONS.verify);
  const queryClient = useQueryClient();
  const requests = useQuery(housekeepingQueries.requests());
  const [raising, setRaising] = useState(false);
  const [completing, setCompleting] = useState<CleaningRequest | null>(null);
  const [verifying, setVerifying] = useState<CleaningRequest | null>(null);
  const acknowledge = useMutation({
    mutationFn: (r: CleaningRequest) => housekeepingApi.acknowledge(r.id, r.version),
    onSettled: () => void queryClient.invalidateQueries({ queryKey: housekeepingKeys.all }),
  });

  return (
    <Section
      flush
      actions={
        canRequest && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setRaising(true)}>
            Ask for a cleaning
          </Button>
        )
      }
    >
      <DataTable<CleaningRequest>
        ariaLabel="Cleaning asked for"
        rows={requests.data}
        loading={requests.isLoading}
        error={requests.error}
        onRetry={() => void requests.refetch()}
        getRowId={(r) => r.id}
        size="small"
        columns={[
          {
            key: 'what',
            header: 'What and where',
            render: (r) => (
              <span>
                <Text weight="semibold">{KIND_LABEL[r.kind]}</Text> · {r.wardName ? `${r.wardName}, ` : ''}
                {r.location}
                {r.details && <Caption1 block>{r.details}</Caption1>}
              </span>
            ),
          },
          {
            key: 'asked',
            header: 'Asked',
            width: 170,
            render: (r) => `${formatDateTime(r.requestedAt)} · ${r.requestedByName}`,
          },
          {
            key: 'response',
            header: 'Response',
            width: 170,
            render: (r) => (
              <span>
                {r.responseMinutes === null ? 'Nobody yet' : `${r.responseMinutes} min`} (target {r.targetMinutes})
                {r.responseLate && (
                  <Badge size="small" appearance="filled" color="danger" style={{ marginLeft: 6 }}>
                    Late
                  </Badge>
                )}
              </span>
            ),
          },
          {
            key: 'status',
            header: 'Status',
            width: 170,
            render: (r) => (
              <span>
                {REQUEST_STATUS_LABEL[r.status]}
                {r.verificationRemarks && r.status === 'Redo' && <Caption1 block>{r.verificationRemarks}</Caption1>}
              </span>
            ),
          },
        ]}
        rowActions={(r) => (
          <>
            {canWork && r.status === 'Open' && (
              <Button size="small" disabled={acknowledge.isPending} onClick={() => acknowledge.mutate(r)}>
                On my way
              </Button>
            )}
            {canWork && (r.status === 'Open' || r.status === 'Acknowledged' || r.status === 'Redo') && (
              <Button size="small" icon={<CheckmarkRegular />} onClick={() => setCompleting(r)}>
                Done
              </Button>
            )}
            {canVerify && r.status === 'AwaitingVerification' && (
              <Button
                size="small"
                appearance="primary"
                disabled={r.completedByYou}
                title={r.completedByYou ? 'You did this cleaning: another supervisor verifies it.' : undefined}
                onClick={() => setVerifying(r)}
              >
                Verify
              </Button>
            )}
          </>
        )}
        emptyTitle="Nothing waiting"
        emptyDescription="Spills, terminal cleaning and other requests from the wards appear here."
      />
      {raising && <RaiseRequestDialog onClose={() => setRaising(false)} />}
      {completing && (
        <ChecklistDialog
          title={`${KIND_LABEL[completing.kind]} done: ${completing.location}`}
          items={completing.checklistItems}
          onSave={(checklist, notes) => housekeepingApi.complete(completing.id, checklist, notes, completing.version)}
          onClose={() => setCompleting(null)}
        />
      )}
      {verifying && (
        <VerifyDialog
          what={`${KIND_LABEL[verifying.kind]} at ${verifying.location}, done by ${verifying.completedByName ?? 'housekeeping'}`}
          onSave={(passed, remarks) => housekeepingApi.verifyRequest(verifying.id, passed, remarks, verifying.version)}
          onClose={() => setVerifying(null)}
        />
      )}
    </Section>
  );
}

/** A ward asks for a cleaning: a spill (15 minutes), a terminal cleaning (an hour), or anything else (two hours). */
export function RaiseRequestDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<CleaningRequestKind>('Spill');
  const [location, setLocation] = useState('');
  const [details, setDetails] = useState('');
  const [tried, setTried] = useState(false);
  const save = useMutation({
    mutationFn: () =>
      housekeepingApi.raise({ kind, wardId: null, location: location.trim(), details: details.trim() || null }),
    onSuccess: (r) => {
      void queryClient.invalidateQueries({ queryKey: housekeepingKeys.all });
      notify.success('Housekeeping asked', `Someone should be on the way within ${r.targetMinutes} minutes.`);
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            setTried(true);
            if (location.trim() && !save.isPending) {
              save.mutate();
            }
          }}
          noValidate
        >
          <DialogBody>
            <DialogTitle>Ask for a cleaning</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <DialogError error={save.error} fallback="The request could not be sent." />
              <Field label="What" required>
                <RadioGroup value={kind} onChange={(_, d) => setKind(d.value as CleaningRequestKind)}>
                  {REQUEST_KINDS.map((k) => (
                    <Radio key={k} value={k} label={KIND_LABEL[k]} />
                  ))}
                </RadioGroup>
              </Field>
              <Field label="Where" required validationMessage={tried && !location.trim() ? 'Say where.' : undefined}>
                <Input
                  value={location}
                  maxLength={200}
                  onChange={(_, d) => setLocation(d.value)}
                  placeholder="Ward 4, bed 12"
                />
              </Field>
              <Field label="Details" hint="For a spill: blood or body fluid, chemical, mercury.">
                <Textarea value={details} maxLength={500} onChange={(_, d) => setDetails(d.value)} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={save.isPending}>
                Send
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** A cleaning recorded against its checklist: every item answered, one not done says why. */
export function ChecklistDialog({
  title,
  items,
  onSave,
  onClose,
}: {
  title: string;
  items: readonly string[];
  onSave: (checklist: ChecklistResult[], notes: string | null) => Promise<unknown>;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [answers, setAnswers] = useState<ChecklistResult[]>(items.map((item) => ({ item, done: false, note: null })));
  const [notes, setNotes] = useState('');
  const [tried, setTried] = useState(false);
  const problem = checklistProblem(items, answers);
  const save = useMutation({
    mutationFn: () => onSave(answers, notes.trim() || null),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: housekeepingKeys.all });
      notify.success('Cleaning recorded', 'A supervisor who did not do it verifies it.');
      onClose();
    },
  });
  const set = (index: number, change: Partial<ChecklistResult>) =>
    setAnswers((a) => a.map((answer, i) => (i === index ? { ...answer, ...change } : answer)));

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            setTried(true);
            if (!problem && !save.isPending) {
              save.mutate();
            }
          }}
          noValidate
        >
          <DialogBody>
            <DialogTitle>{title}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              <DialogError error={save.error} fallback="The cleaning could not be recorded." />
              {answers.map((answer, index) => (
                <div key={answer.item} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                  <Checkbox
                    checked={answer.done}
                    label={answer.item}
                    onChange={(_, d) => set(index, { done: d.checked === true })}
                  />
                  {!answer.done && (
                    <Input
                      aria-label={`Why "${answer.item}" was not done`}
                      placeholder="Why not done"
                      value={answer.note ?? ''}
                      maxLength={200}
                      onChange={(_, d) => set(index, { note: d.value })}
                    />
                  )}
                </div>
              ))}
              <Field label="Notes">
                <Textarea value={notes} maxLength={500} onChange={(_, d) => setNotes(d.value)} />
              </Field>
              {tried && problem && (
                <MessageBar intent="warning">
                  <MessageBarBody>
                    <MessageBarTitle>Not complete</MessageBarTitle>
                    {problem}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** A supervisor, never the cleaner, checks the cleaning; one sent back says what was not clean. */
export function VerifyDialog({
  what,
  onSave,
  onClose,
}: {
  what: string;
  onSave: (passed: boolean, remarks: string | null) => Promise<unknown>;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [passed, setPassed] = useState(true);
  const [remarks, setRemarks] = useState('');
  const [tried, setTried] = useState(false);
  const save = useMutation({
    mutationFn: () => onSave(passed, remarks.trim() || null),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: housekeepingKeys.all });
      notify.success(passed ? 'Cleaning verified' : 'Sent back to be cleaned again');
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            setTried(true);
            if ((passed || remarks.trim()) && !save.isPending) {
              save.mutate();
            }
          }}
          noValidate
        >
          <DialogBody>
            <DialogTitle>Verify the cleaning</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Text>{what}</Text>
              <DialogError error={save.error} fallback="The verification could not be recorded." />
              <Field label="Found">
                <RadioGroup value={passed ? 'clean' : 'redo'} onChange={(_, d) => setPassed(d.value === 'clean')}>
                  <Radio value="clean" label="Clean" />
                  <Radio value="redo" label="Not clean: clean again" />
                </RadioGroup>
              </Field>
              <Field
                label="What was not clean"
                required={!passed}
                validationMessage={tried && !passed && !remarks.trim() ? 'Say what was not clean.' : undefined}
              >
                <Textarea value={remarks} maxLength={500} onChange={(_, d) => setRemarks(d.value)} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={save.isPending}>
                Record
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** The day's scheduled cleanings, each with what was done about it. */
export function BoardPanel() {
  const canWork = usePermission(HOUSEKEEPING_PERMISSIONS.work);
  const canVerify = usePermission(HOUSEKEEPING_PERMISSIONS.verify);
  const [date, setDate] = useState(todayIso());
  const board = useQuery(housekeepingQueries.board(date));
  const [recording, setRecording] = useState<CleaningSlot | null>(null);
  const [verifying, setVerifying] = useState<CleaningSlot | null>(null);
  const data = board.data;

  return (
    <Section flush>
      <FilterBar>
        <Field label="Day">
          <Input type="date" value={date} max={todayIso()} onChange={(_, d) => setDate(d.value)} />
        </Field>
      </FilterBar>
      {data && (
        <div style={{ padding: '0 16px 12px', display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <Badge appearance="tint">{data.due} due</Badge>
          <Badge appearance="tint" color={data.notDone > 0 ? 'warning' : 'success'}>
            {data.notDone} not done
          </Badge>
          <Badge appearance="tint">{data.awaitingVerification} to verify</Badge>
          <Badge appearance="tint" color={data.redo > 0 ? 'danger' : 'informative'}>
            {data.redo} sent back
          </Badge>
        </div>
      )}
      <DataTable<CleaningSlot>
        ariaLabel="Scheduled cleaning"
        rows={data?.slots}
        loading={board.isLoading}
        error={board.error}
        onRetry={() => void board.refetch()}
        getRowId={(s) => `${s.areaId}:${s.slot}`}
        size="small"
        columns={[
          {
            key: 'area',
            header: 'Area',
            render: (s) => (
              <span>
                <Text weight="semibold">{s.areaName}</Text> · cleaning {s.slot}
                <Caption1 block>{RISK_LABEL[s.riskZone]}</Caption1>
              </span>
            ),
          },
          {
            key: 'done',
            header: 'Done',
            render: (s) =>
              s.record ? (
                <span>
                  {formatDateTime(s.record.cleanedAt)} · {s.record.cleanedByName}
                  <Caption1 block>
                    {s.record.status === 'Verified'
                      ? `Verified by ${s.record.verifiedByName ?? ''}`
                      : s.record.status === 'Redo'
                        ? `Sent back: ${s.record.verificationRemarks ?? ''}`
                        : 'Waiting for a supervisor'}
                  </Caption1>
                </span>
              ) : (
                'Not yet'
              ),
          },
        ]}
        rowActions={(s) => (
          <>
            {canWork && (!s.record || s.record.status === 'Redo') && (
              <Button size="small" icon={<CheckmarkRegular />} onClick={() => setRecording(s)}>
                Record
              </Button>
            )}
            {canVerify && s.record?.status === 'AwaitingVerification' && (
              <Button
                size="small"
                appearance="primary"
                disabled={s.record.cleanedByYou}
                title={s.record.cleanedByYou ? 'You did this cleaning: another supervisor verifies it.' : undefined}
                onClick={() => setVerifying(s)}
              >
                Verify
              </Button>
            )}
          </>
        )}
        emptyTitle="Nothing scheduled for this day"
      />
      {recording && (
        <ChecklistDialog
          title={`${recording.areaName}: cleaning ${recording.slot}`}
          items={recording.checklist}
          onSave={(checklist, notes) =>
            housekeepingApi.record(recording.areaId, { date, slot: recording.slot, checklist, notes })
          }
          onClose={() => setRecording(null)}
        />
      )}
      {verifying?.record && (
        <VerifyDialog
          what={`${verifying.areaName}, cleaning ${verifying.slot}, done by ${verifying.record.cleanedByName}`}
          onSave={(passed, remarks) =>
            housekeepingApi.verifyRecord(verifying.record!.id, passed, remarks, verifying.record!.version)
          }
          onClose={() => setVerifying(null)}
        />
      )}
    </Section>
  );
}

/** The areas cleaned on a schedule, their frequency and checklists. */
export function AreasPanel() {
  const canManage = usePermission(HOUSEKEEPING_PERMISSIONS.manage);
  const areas = useQuery(housekeepingQueries.areas());
  const [editing, setEditing] = useState<CleaningArea | 'new' | null>(null);

  return (
    <Section
      flush
      actions={
        canManage && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
            Add an area
          </Button>
        )
      }
    >
      <DataTable<CleaningArea>
        ariaLabel="Areas cleaned on a schedule"
        rows={areas.data}
        loading={areas.isLoading}
        error={areas.error}
        onRetry={() => void areas.refetch()}
        getRowId={(a) => a.id}
        size="small"
        columns={[
          { key: 'code', header: 'Code', width: 120, render: (a) => a.code },
          { key: 'name', header: 'Area', render: (a) => `${a.name}${a.isActive ? '' : ' (not in use)'}` },
          { key: 'zone', header: 'Risk', width: 200, render: (a) => RISK_LABEL[a.riskZone] },
          {
            key: 'when',
            header: 'Cleaned',
            width: 200,
            render: (a) =>
              a.frequency === 'Daily' ? `${a.timesPerDay} times a day` : `Weekly: ${a.weekDays.join(', ')}`,
          },
          { key: 'checklist', header: 'Checklist', priority: 'low', render: (a) => a.checklist.join('; ') },
        ]}
        rowActions={
          canManage
            ? (a) => (
                <Button size="small" icon={<EditRegular />} onClick={() => setEditing(a)}>
                  Edit
                </Button>
              )
            : undefined
        }
        emptyTitle="No areas set up"
      />
      {editing && <AreaDialog area={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Section>
  );
}

function AreaDialog({ area, onClose }: { area: CleaningArea | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [code, setCode] = useState(area?.code ?? '');
  const [name, setName] = useState(area?.name ?? '');
  const [zone, setZone] = useState<CleaningRiskZone>(area?.riskZone ?? 'Moderate');
  const [frequency, setFrequency] = useState<CleaningFrequency>(area?.frequency ?? 'Daily');
  const [times, setTimes] = useState(String(area?.timesPerDay ?? 2));
  const [days, setDays] = useState<DayOfWeek[]>(area?.weekDays ?? []);
  const [checklist, setChecklist] = useState((area?.checklist ?? []).join('\n'));
  const [active, setActive] = useState(area?.isActive ?? true);
  const save = useMutation({
    mutationFn: () =>
      housekeepingApi.saveArea(area?.id ?? null, {
        code: code.trim(),
        name: name.trim(),
        wardId: area?.wardId ?? null,
        riskZone: zone,
        frequency,
        timesPerDay: Number(times) || 1,
        weekDays: frequency === 'Weekly' ? days : null,
        checklist: checklist
          .split('\n')
          .map((c) => c.trim())
          .filter(Boolean),
        isActive: active,
        version: area?.version ?? null,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: housekeepingKeys.all });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (!save.isPending) {
              save.mutate();
            }
          }}
          noValidate
        >
          <DialogBody>
            <DialogTitle>{area ? `Edit ${area.name}` : 'Add an area'}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <DialogError error={save.error} fallback="The area could not be saved." />
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: 12 }}>
                <Field label="Code" required>
                  <Input value={code} maxLength={24} onChange={(_, d) => setCode(d.value)} />
                </Field>
                <Field label="Name" required>
                  <Input value={name} maxLength={120} onChange={(_, d) => setName(d.value)} />
                </Field>
                <Field label="Risk">
                  <Select value={zone} onChange={(_, d) => setZone(d.value as CleaningRiskZone)}>
                    {RISK_ZONES.map((z) => (
                      <option key={z} value={z}>
                        {RISK_LABEL[z]}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="How often">
                  <Select value={frequency} onChange={(_, d) => setFrequency(d.value as CleaningFrequency)}>
                    <option value="Daily">Daily</option>
                    <option value="Weekly">Weekly</option>
                  </Select>
                </Field>
              </div>
              {frequency === 'Daily' ? (
                <Field label="Times a day">
                  <Input type="number" min={1} max={6} value={times} onChange={(_, d) => setTimes(d.value)} />
                </Field>
              ) : (
                <Field label="Days">
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
                    {WEEK_DAYS.map((day) => (
                      <Checkbox
                        key={day}
                        label={day}
                        checked={days.includes(day)}
                        onChange={(_, d) =>
                          setDays((all) => (d.checked ? [...all, day] : all.filter((x) => x !== day)))
                        }
                      />
                    ))}
                  </div>
                </Field>
              )}
              <Field label="Checklist" hint="One item a line." required>
                <Textarea value={checklist} rows={5} onChange={(_, d) => setChecklist(d.value)} />
              </Field>
              <Switch checked={active} label="In use" onChange={(_, d) => setActive(d.checked)} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={save.isPending}>
                Save
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
