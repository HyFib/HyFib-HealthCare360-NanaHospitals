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
  MessageBarTitle,
  Select,
  Subtitle2,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatDateTime, formatTime, isApiError } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, Section } from '@hc360/ui';
import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';
import { ReasonDialog } from '../ReasonDialog';

/**
 * Restraint of an in-patient (Mental Healthcare Act 2017 s.97, NABH COP): a doctor's time-limited order, the nurses'
 * checks at the interval the doctor set, renewal or release by a doctor, and the debrief
 * (`/api/v1/nursing/admissions/{id}/restraints`; ordering `nursing.restraint.order`, checks `nursing.chart`).
 */

export type RestraintType = 'Physical' | 'Chemical';
export type RestraintStatus = 'Active' | 'Released';

export interface RestraintCheck {
  id: string;
  checkedAt: string;
  circulationOk: boolean;
  skinIntact: boolean;
  fluidsOffered: boolean;
  toiletingOffered: boolean;
  notes: string | null;
  checkedByName: string | null;
  hasConcern: boolean;
}

export interface RestraintRenewal {
  renewedAt: string;
  until: string;
  reason: string;
  byName: string | null;
}

export interface RestraintEpisode {
  id: string;
  admissionId: string;
  type: RestraintType;
  method: string;
  reason: string;
  alternativesTried: string;
  checkIntervalMinutes: number;
  orderedAt: string;
  orderedByName: string | null;
  orderedUntil: string;
  renewals: RestraintRenewal[];
  status: RestraintStatus;
  lastCheckedAt: string | null;
  nextCheckDue: string;
  checkOverdue: boolean;
  orderLapsed: boolean;
  releasedAt: string | null;
  releasedByName: string | null;
  releaseReason: string | null;
  debrief: string | null;
  debriefedAt: string | null;
  debriefedByName: string | null;
  checks: RestraintCheck[];
}

export interface WardRestraintItem {
  episodeId: string;
  admissionId: string;
  patientId: string;
  uhid: string;
  patientName: string;
  bedLabel: string | null;
  type: RestraintType;
  method: string;
  lastCheckedAt: string | null;
  nextCheckDue: string;
  checkOverdue: boolean;
  minutesOverdue: number;
  orderedUntil: string;
  orderLapsed: boolean;
}

const RESTRAINT_MAX_HOURS = 4;
const TYPE_LABEL: Record<RestraintType, string> = {
  Physical: 'Physical',
  Chemical: 'Chemical (rapid tranquillisation)',
};

const key = (admissionId: string) => ['nursing', 'admission', admissionId, 'restraints'] as const;
const wardKey = (wardId: string) => ['nursing', 'ward', wardId, 'restraints'] as const;

const restraintQueries = {
  admission: (admissionId: string) =>
    queryOptions({
      queryKey: key(admissionId),
      queryFn: ({ signal }) =>
        api.get<RestraintEpisode[]>(`/api/v1/nursing/admissions/${admissionId}/restraints`, { signal }),
    }),
  ward: (wardId: string) =>
    queryOptions({
      queryKey: wardKey(wardId),
      queryFn: ({ signal }) => api.get<WardRestraintItem[]>(`/api/v1/nursing/wards/${wardId}/restraints`, { signal }),
      refetchInterval: 60_000,
    }),
};

const useStyles = makeStyles({
  head: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBlock: '0 8px', gap: '8px' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  episode: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '12px',
    marginBottom: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  alert: { borderLeft: `5px solid ${tokens.colorPaletteRedBorderActive}` },
  facts: { display: 'grid', gridTemplateColumns: 'max-content minmax(0, 1fr)', gap: '2px 12px' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  list: { display: 'flex', flexDirection: 'column', gap: '6px' },
  ward: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: '8px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    flexWrap: 'wrap',
  },
});

const localNow = () => {
  const d = new Date();
  return new Date(d.getTime() - d.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
};

const errorText = (error: unknown, fallback: string) => (isApiError(error) ? error.message : fallback);

type Acting = { kind: 'check' | 'renew' | 'release' | 'debrief'; episode: RestraintEpisode } | null;

/** The patient's restraints: the active one on top with its checks, then the ended ones with their debrief. */
export function Restraints({
  admissionId,
  canChart,
  canOrder,
}: {
  admissionId: string;
  canChart: boolean;
  canOrder: boolean;
}) {
  const styles = useStyles();
  const episodes = useQuery(restraintQueries.admission(admissionId));
  const [ordering, setOrdering] = useState(false);
  const [acting, setActing] = useState<Acting>(null);
  const active = episodes.data?.some((e) => e.status === 'Active') ?? false;

  return (
    <>
      <div className={styles.head}>
        <Subtitle2>Restraint</Subtitle2>
        {canOrder && !active && (
          <Button icon={<AddRegular />} onClick={() => setOrdering(true)}>
            Order a restraint
          </Button>
        )}
      </div>
      {episodes.error ? (
        <ErrorState error={episodes.error} onRetry={() => void episodes.refetch()} />
      ) : episodes.data && episodes.data.length === 0 ? (
        <EmptyState
          title="Not restrained during this stay"
          description="A restraint is the last resort, ordered by a doctor."
        />
      ) : (
        episodes.data?.map((e) => (
          <EpisodeCard
            key={e.id}
            episode={e}
            canChart={canChart}
            canOrder={canOrder}
            onAct={(kind) => setActing({ kind, episode: e })}
          />
        ))
      )}
      {ordering && <OrderRestraintDialog admissionId={admissionId} onClose={() => setOrdering(false)} />}
      {acting?.kind === 'check' && (
        <RecordCheckDialog admissionId={admissionId} episode={acting.episode} onClose={() => setActing(null)} />
      )}
      {acting && acting.kind !== 'check' && (
        <EpisodeReasonDialog
          admissionId={admissionId}
          acting={{ kind: acting.kind, episode: acting.episode }}
          onClose={() => setActing(null)}
        />
      )}
    </>
  );
}

function EpisodeCard({
  episode: e,
  canChart,
  canOrder,
  onAct,
}: {
  episode: RestraintEpisode;
  canChart: boolean;
  canOrder: boolean;
  onAct: (kind: 'check' | 'renew' | 'release' | 'debrief') => void;
}) {
  const styles = useStyles();
  const active = e.status === 'Active';
  return (
    <section
      className={active && (e.checkOverdue || e.orderLapsed) ? `${styles.episode} ${styles.alert}` : styles.episode}
      aria-label={`${e.type} restraint ordered ${formatDateTime(e.orderedAt)}`}
    >
      <div className={styles.row}>
        <Text weight="semibold">
          {TYPE_LABEL[e.type]}: {e.method}
        </Text>
        {active ? <Badge color="danger">Restrained</Badge> : <Badge appearance="outline">Ended</Badge>}
        {active && e.checkOverdue && <Badge color="danger">Check overdue</Badge>}
        {active && e.orderLapsed && <Badge color="danger">Order ran out: renew or release</Badge>}
      </div>
      <div className={styles.facts}>
        <Caption1>Why</Caption1>
        <Text>{e.reason}</Text>
        <Caption1>Tried first</Caption1>
        <Text>{e.alternativesTried}</Text>
        <Caption1>Ordered</Caption1>
        <Text>
          {formatDateTime(e.orderedAt)}
          {e.orderedByName ? ` by ${e.orderedByName}` : ''}, until {formatDateTime(e.orderedUntil)}
          {e.renewals.length > 0 ? ` (renewed ${e.renewals.length}×)` : ''}
        </Text>
        <Caption1>Checks</Caption1>
        <Text>
          Every {e.checkIntervalMinutes} min
          {active ? `; next due ${formatTime(e.nextCheckDue)}` : ''}
        </Text>
        {!active && (
          <>
            <Caption1>Ended</Caption1>
            <Text>
              {e.releasedAt ? formatDateTime(e.releasedAt) : '—'}
              {e.releasedByName ? ` by ${e.releasedByName}` : ''}: {e.releaseReason}
            </Text>
            <Caption1>Debrief</Caption1>
            <Text>{e.debrief ?? 'Not recorded yet'}</Text>
          </>
        )}
      </div>
      <div className={styles.actions}>
        {active && canChart && (
          <Button appearance="primary" onClick={() => onAct('check')}>
            Record a check
          </Button>
        )}
        {active && canOrder && <Button onClick={() => onAct('renew')}>Renew the order</Button>}
        {active && canOrder && <Button onClick={() => onAct('release')}>Release the patient</Button>}
        {!active && !e.debrief && canChart && <Button onClick={() => onAct('debrief')}>Record the debrief</Button>}
      </div>
      {e.checks.length > 0 && (
        <DataTable
          ariaLabel="Nurses' checks"
          rows={e.checks}
          getRowId={(c) => c.id}
          size="small"
          columns={[
            { key: 'when', header: 'When', width: 150, render: (c) => formatDateTime(c.checkedAt) },
            {
              key: 'found',
              header: 'Found',
              render: (c) => (
                <span>
                  {c.hasConcern ? <Badge color="danger">Concern</Badge> : null}{' '}
                  {[
                    c.circulationOk ? 'circulation good' : 'circulation poor',
                    c.skinIntact ? 'skin intact' : 'skin broken',
                    c.fluidsOffered ? 'fluids offered' : 'no fluids',
                    c.toiletingOffered ? 'toilet offered' : 'no toilet',
                  ].join(', ')}
                  {c.notes ? `. ${c.notes}` : ''}
                </span>
              ),
            },
            { key: 'by', header: 'By', width: 160, render: (c) => c.checkedByName ?? '—' },
          ]}
        />
      )}
    </section>
  );
}

export function OrderRestraintDialog({ admissionId, onClose }: { admissionId: string; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [type, setType] = useState<RestraintType>('Physical');
  const [method, setMethod] = useState('');
  const [reason, setReason] = useState('');
  const [alternatives, setAlternatives] = useState('');
  const [hours, setHours] = useState(2);
  const [checkEvery, setCheckEvery] = useState(15);
  const order = useMutation({
    mutationFn: () =>
      api.post<RestraintEpisode>(`/api/v1/nursing/admissions/${admissionId}/restraints`, {
        type,
        method: method.trim(),
        reason: reason.trim(),
        alternativesTried: alternatives.trim(),
        hours,
        checkIntervalMinutes: checkEvery,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['nursing'] });
      onClose();
    },
  });
  const valid = method.trim().length > 0 && reason.trim().length >= 10 && alternatives.trim().length >= 5;
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !order.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <DialogBody>
          <DialogTitle>Order a restraint</DialogTitle>
          <DialogContent className={styles.body}>
            <Caption1>
              A restraint is the last resort, only to prevent harm to the patient or others, and for no longer than
              needed. The order runs at most {RESTRAINT_MAX_HOURS} hours; see the patient again to renew it.
            </Caption1>
            <div className={styles.row}>
              <Field label="Type" required>
                <Select value={type} onChange={(_, data) => setType(data.value as RestraintType)}>
                  <option value="Physical">{TYPE_LABEL.Physical}</option>
                  <option value="Chemical">{TYPE_LABEL.Chemical}</option>
                </Select>
              </Field>
              <Field label="For how many hours" required>
                <Select value={String(hours)} onChange={(_, data) => setHours(Number(data.value))}>
                  {[1, 2, 3, 4].map((h) => (
                    <option key={h} value={h}>
                      {h} h
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Nurses check every" required>
                <Select value={String(checkEvery)} onChange={(_, data) => setCheckEvery(Number(data.value))}>
                  {[15, 30, 60].map((m) => (
                    <option key={m} value={m}>
                      {m} min
                    </option>
                  ))}
                </Select>
              </Field>
            </div>
            <Field
              label={type === 'Physical' ? 'What is used' : 'Medicine, dose and route'}
              hint={
                type === 'Physical'
                  ? 'For example "soft wrist holders, both wrists"'
                  : 'For example "Inj. haloperidol 5 mg IM"'
              }
              required
            >
              <Input value={method} maxLength={300} onChange={(_, data) => setMethod(data.value)} />
            </Field>
            <Field label="Why: the harm it prevents" required>
              <Textarea value={reason} maxLength={1000} onChange={(_, data) => setReason(data.value)} />
            </Field>
            <Field
              label="Less restrictive measures tried first"
              hint="Talking down, a sitter, removing the trigger"
              required
            >
              <Textarea value={alternatives} maxLength={1000} onChange={(_, data) => setAlternatives(data.value)} />
            </Field>
            {order.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  <MessageBarTitle>Not ordered</MessageBarTitle>
                  {errorText(order.error, 'The restraint could not be ordered.')}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={order.isPending}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || order.isPending} onClick={() => order.mutate()}>
              Order
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

export function RecordCheckDialog({
  admissionId,
  episode,
  onClose,
}: {
  admissionId: string;
  episode: RestraintEpisode;
  onClose: () => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [checkedAt, setCheckedAt] = useState(localNow);
  const [circulation, setCirculation] = useState(true);
  const [skin, setSkin] = useState(true);
  const [fluids, setFluids] = useState(true);
  const [toileting, setToileting] = useState(true);
  const [notes, setNotes] = useState('');
  const concern = !circulation || !skin;
  const record = useMutation({
    mutationFn: () =>
      api.post<RestraintEpisode>(`/api/v1/nursing/restraints/${episode.id}/checks`, {
        checkedAt: new Date(checkedAt).toISOString(),
        circulationOk: circulation,
        skinIntact: skin,
        fluidsOffered: fluids,
        toiletingOffered: toileting,
        notes: notes.trim() || null,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: key(admissionId) });
      void queryClient.invalidateQueries({ queryKey: ['nursing', 'ward'] });
      onClose();
    },
  });
  const valid = !Number.isNaN(new Date(checkedAt).getTime()) && (!concern || notes.trim().length > 0);
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !record.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>Record a check</DialogTitle>
          <DialogContent className={styles.body}>
            <Field label="When" required>
              <Input type="datetime-local" value={checkedAt} onChange={(_, data) => setCheckedAt(data.value)} />
            </Field>
            <Switch
              label="Circulation good beyond the holders (colour, warmth, pulse)"
              checked={circulation}
              onChange={(_, data) => setCirculation(data.checked)}
            />
            <Switch
              label="Skin intact under the holders"
              checked={skin}
              onChange={(_, data) => setSkin(data.checked)}
            />
            <Switch label="Fluids offered" checked={fluids} onChange={(_, data) => setFluids(data.checked)} />
            <Switch label="Toilet offered" checked={toileting} onChange={(_, data) => setToileting(data.checked)} />
            <Field
              label="How the patient is, and what was done"
              required={concern}
              hint={concern ? 'Poor circulation or broken skin: say what you found and what was done.' : undefined}
            >
              <Textarea value={notes} maxLength={1000} onChange={(_, data) => setNotes(data.value)} />
            </Field>
            {record.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  <MessageBarTitle>Not saved</MessageBarTitle>
                  {errorText(record.error, 'The check could not be recorded.')}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={record.isPending}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || record.isPending} onClick={() => record.mutate()}>
              Record
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function EpisodeReasonDialog({
  admissionId,
  acting,
  onClose,
}: {
  admissionId: string;
  acting: { kind: 'renew' | 'release' | 'debrief'; episode: RestraintEpisode };
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [hours, setHours] = useState(2);
  const { kind, episode } = acting;
  const act = useMutation({
    mutationFn: (text: string) =>
      kind === 'renew'
        ? api.post(`/api/v1/nursing/restraints/${episode.id}/renew`, { hours, reason: text })
        : kind === 'release'
          ? api.post(`/api/v1/nursing/restraints/${episode.id}/release`, { reason: text })
          : api.post(`/api/v1/nursing/restraints/${episode.id}/debrief`, { debrief: text }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: key(admissionId) });
      void queryClient.invalidateQueries({ queryKey: ['nursing', 'ward'] });
      onClose();
    },
  });
  const words = {
    renew: {
      title: 'Renew the restraint',
      label: 'Why it is still needed, having seen the patient',
      confirm: 'Renew',
    },
    release: { title: 'Release the patient', label: 'Why the restraint can end', confirm: 'Release' },
    debrief: {
      title: 'Record the debrief',
      label: 'What led to the restraint, and what may prevent the next one',
      confirm: 'Record',
    },
  }[kind];
  return (
    <ReasonDialog
      open
      title={words.title}
      label={words.label}
      description={
        kind === 'renew' ? (
          <Field label="For how many more hours">
            <Select value={String(hours)} onChange={(_, data) => setHours(Number(data.value))}>
              {[1, 2, 3, 4].map((h) => (
                <option key={h} value={h}>
                  {h} h
                </option>
              ))}
            </Select>
          </Field>
        ) : undefined
      }
      confirmLabel={words.confirm}
      minLength={kind === 'release' ? 3 : 10}
      maxLength={kind === 'debrief' ? 2000 : 1000}
      busy={act.isPending}
      error={act.error}
      onConfirm={(text) => act.mutate(text)}
      onCancel={onClose}
    />
  );
}

/**
 * The ward's restrained patients, an overdue check or an order that ran out on top: the nurse in charge sees at a glance
 * whose check is late.
 */
export function WardRestraints({
  wardId,
  onOpenPatient,
}: {
  wardId: string;
  onOpenPatient: (admissionId: string) => void;
}) {
  const styles = useStyles();
  const ward = useQuery(restraintQueries.ward(wardId));
  if (ward.error) {
    return (
      <Section title="Restrained patients">
        <ErrorState error={ward.error} onRetry={() => void ward.refetch()} />
      </Section>
    );
  }

  if (!ward.data || ward.data.length === 0) {
    return null;
  }

  const late = ward.data.filter((r) => r.checkOverdue || r.orderLapsed).length;
  return (
    <Section title="Restrained patients" description={late > 0 ? `${late} overdue` : 'Every check on time'}>
      <div className={styles.list}>
        {ward.data.map((r) => (
          <div
            key={r.episodeId}
            className={r.checkOverdue || r.orderLapsed ? `${styles.ward} ${styles.alert}` : styles.ward}
          >
            <div>
              <Text weight="semibold">
                {r.bedLabel ? `${r.bedLabel} · ` : ''}
                {r.patientName}
              </Text>{' '}
              <Caption1>
                {r.type === 'Physical' ? 'Physical' : 'Chemical'}: {r.method}
              </Caption1>
              <div>
                {r.checkOverdue ? (
                  <Badge color="danger">Check {r.minutesOverdue} min overdue</Badge>
                ) : (
                  <Caption1>Next check {formatTime(r.nextCheckDue)}</Caption1>
                )}{' '}
                {r.orderLapsed && <Badge color="danger">Order ran out</Badge>}
              </div>
            </div>
            <Button onClick={() => onOpenPatient(r.admissionId)}>Open</Button>
          </div>
        ))}
      </div>
    </Section>
  );
}
