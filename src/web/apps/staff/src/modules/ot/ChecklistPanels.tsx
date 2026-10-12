import {
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  ProgressBar,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, CircleRegular, LockClosedRegular, SubtractCircleRegular } from '@fluentui/react-icons';
import { formatTime } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import {
  otApi,
  otKeys,
  type ChecklistInput,
  type ChecklistItemDto,
  type ChecklistItemState,
  type SafetyPhase,
  type SurgeryDetail,
} from './api';
import { anaesthesiaLabel, phaseLabel, phaseMoment } from './labels';
import { phaseGate, preOpGate } from './logic';
import { SideBadge } from './OtBadges';
import { ActionError, WhoWhen } from './shared/feedback';

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: '12px' },
  summary: { display: 'flex', flexDirection: 'column', gap: '4px' },
  items: { display: 'flex', flexDirection: 'column', gap: '8px' },
  item: {
    display: 'grid',
    gridTemplateColumns: '28px minmax(0, 1fr)',
    gap: '4px 10px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  itemDone: {
    backgroundColor: tokens.colorPaletteGreenBackground1,
    border: `1px solid ${tokens.colorPaletteGreenBorder2}`,
  },
  itemNa: { backgroundColor: tokens.colorNeutralBackground3 },
  itemEdited: { border: `1px solid ${tokens.colorBrandStroke1}` },
  icon: { fontSize: '22px', display: 'flex', paddingTop: '2px' },
  done: { color: tokens.colorPaletteGreenForeground1 },
  na: { color: tokens.colorNeutralForeground3 },
  pending: { color: tokens.colorNeutralForeground3 },
  itemBody: { display: 'flex', flexDirection: 'column', gap: '6px', minWidth: 0 },
  controls: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'center' },
  remarks: { minWidth: '260px', flex: '1 1 260px' },
  footer: { display: 'flex', gap: '12px', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  identity: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))',
    gap: '8px 20px',
    padding: '12px 16px',
    borderRadius: tokens.borderRadiusLarge,
    border: `2px solid ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorBrandBackground2,
  },
  identityLabel: { color: tokens.colorNeutralForeground3, display: 'block' },
  identityValue: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
});

function stateIcon(state: ChecklistItemState, styles: ReturnType<typeof useStyles>) {
  if (state === 'Done') {
    return <CheckmarkCircleRegular className={styles.done} aria-label="Done" />;
  }
  if (state === 'NotApplicable') {
    return <SubtractCircleRegular className={styles.na} aria-label="Not applicable" />;
  }
  return <CircleRegular className={styles.pending} aria-label="Not done yet" />;
}

function recordedText(item: ChecklistItemDto): string {
  return item.state === 'NotApplicable'
    ? `Not applicable${item.remarks ? `: ${item.remarks}` : ''} — marked by`
    : 'Confirmed by';
}

// ---------------------------------------------------------------- pre-operative checklist

interface PreOpEdit {
  state: ChecklistItemState;
  remarks: string;
}

/**
 * Pre-operative checklist (ward / pre-op holding). Items can be corrected until the WHO Sign-in, which locks it.
 * "Not applicable" is only offered where the hospital allows it and always needs a reason.
 */
export function PreOpChecklistPanel({
  booking,
  canRecord,
  onComplete,
}: {
  booking: SurgeryDetail;
  canRecord: boolean;
  onComplete?: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const gate = preOpGate(booking);
  const editable = gate.allowed && canRecord;
  const [edits, setEdits] = useState<Record<string, PreOpEdit>>({});

  const items = booking.preOpChecklist;
  const valueOf = (item: ChecklistItemDto): PreOpEdit =>
    edits[item.key] ?? { state: item.state, remarks: item.remarks ?? '' };
  const changed = items.filter((item) => {
    const edit = edits[item.key];
    return edit && (edit.state !== item.state || edit.remarks.trim() !== (item.remarks ?? ''));
  });
  const missingReason = changed.filter(
    (item) => valueOf(item).state === 'NotApplicable' && !valueOf(item).remarks.trim(),
  );
  const doneCount = items.filter((item) => valueOf(item).state !== 'Pending').length;

  const willComplete = items.every((item) => valueOf(item).state !== 'Pending');
  const save = useMutation({
    mutationFn: (inputs: ChecklistInput[]) => otApi.recordPreOp(booking.id, inputs),
    onSuccess: () => {
      setEdits({});
      notify.success(willComplete ? 'Pre-operative checklist complete' : 'Pre-operative checklist saved');
      void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id) });
      if (willComplete) {
        onComplete?.();
      }
    },
  });

  const submit = () =>
    save.mutate(
      changed.map((item) => ({
        key: item.key,
        state: valueOf(item).state,
        remarks: valueOf(item).remarks.trim() || null,
      })),
    );

  const setItem = (item: ChecklistItemDto, next: Partial<PreOpEdit>) =>
    setEdits((current) => ({ ...current, [item.key]: { ...valueOf(item), ...next } }));

  return (
    <div className={styles.panel}>
      <div className={styles.summary}>
        <Text weight="semibold">
          {doneCount} of {items.length} checked{booking.preOpComplete ? ' · complete' : ''}
        </Text>
        <ProgressBar
          value={items.length ? doneCount / items.length : 0}
          color={doneCount === items.length ? 'success' : 'brand'}
          thickness="large"
          aria-label="Pre-operative checklist progress"
        />
      </div>
      {!gate.allowed && (
        <MessageBar intent="info" icon={<LockClosedRegular />}>
          <MessageBarBody>{gate.reason}</MessageBarBody>
        </MessageBar>
      )}
      {gate.allowed && !canRecord && (
        <MessageBar intent="info">
          <MessageBarBody>
            Only nurses, doctors and OT coordinators record the checklist. You can view it.
          </MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.items} role="list" aria-label="Pre-operative checklist">
        {items.map((item) => {
          const value = valueOf(item);
          const isEdited = changed.includes(item);
          return (
            <div
              key={item.key}
              role="listitem"
              className={mergeClasses(
                styles.item,
                value.state === 'Done' && styles.itemDone,
                value.state === 'NotApplicable' && styles.itemNa,
                isEdited && styles.itemEdited,
              )}
            >
              <span className={styles.icon}>{stateIcon(value.state, styles)}</span>
              <div className={styles.itemBody}>
                <Text weight="semibold">{item.text}</Text>
                {editable && (
                  <div className={styles.controls}>
                    <RadioGroup
                      layout="horizontal"
                      aria-label={item.text}
                      value={value.state}
                      onChange={(_, data) => setItem(item, { state: data.value as ChecklistItemState })}
                    >
                      <Radio value="Done" label="Done" />
                      {item.allowsNotApplicable && <Radio value="NotApplicable" label="Not applicable" />}
                      <Radio value="Pending" label="Not done yet" />
                    </RadioGroup>
                    {(value.state === 'NotApplicable' || value.remarks) && (
                      <Field
                        className={styles.remarks}
                        validationState={value.state === 'NotApplicable' && !value.remarks.trim() ? 'error' : 'none'}
                        validationMessage={
                          value.state === 'NotApplicable' && !value.remarks.trim()
                            ? 'Say why it does not apply.'
                            : undefined
                        }
                      >
                        <Input
                          aria-label={`Remarks for ${item.text}`}
                          placeholder={value.state === 'NotApplicable' ? 'Why it does not apply (required)' : 'Remarks'}
                          value={value.remarks}
                          maxLength={500}
                          onChange={(_, data) => setItem(item, { remarks: data.value })}
                        />
                      </Field>
                    )}
                  </div>
                )}
                {!editable && item.state === 'NotApplicable' && item.remarks && (
                  <Caption1>Reason: {item.remarks}</Caption1>
                )}
                {item.state !== 'Pending' && (
                  <WhoWhen prefix={recordedText(item)} by={item.recordedBy} at={item.recordedAt} />
                )}
              </div>
            </div>
          );
        })}
      </div>
      <ActionError
        error={save.error}
        onReload={() => void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id) })}
      />
      {editable && (
        <div className={styles.footer}>
          <Caption1 className={styles.muted}>
            Your name and the time are recorded against every item you change.
          </Caption1>
          <Button
            appearance="primary"
            disabled={changed.length === 0 || missingReason.length > 0 || save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
            onClick={submit}
          >
            {changed.length === 0
              ? 'No changes to save'
              : `Save checklist (${changed.length} change${changed.length === 1 ? '' : 's'})`}
          </Button>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- WHO surgical safety checklist

interface PhaseSelection {
  confirmed: boolean;
  notApplicable: boolean;
  remarks: string;
}

/** Patient, procedure and side read aloud before the checklist items (Sign-in and Time-out). */
export function IdentityCheck({ booking }: { booking: SurgeryDetail }) {
  const styles = useStyles();
  const patient = booking.patient;
  return (
    <div className={styles.identity} aria-label="Confirm patient, procedure and site">
      <div>
        <Caption1 className={styles.identityLabel}>Patient</Caption1>
        <span className={styles.identityValue}>{patient?.fullName ?? '—'}</span>
        <Caption1 block>{patient ? `${patient.uhid} · ${patient.age ?? '—'} / ${patient.gender}` : ''}</Caption1>
      </div>
      <div>
        <Caption1 className={styles.identityLabel}>Procedure</Caption1>
        <span className={styles.identityValue}>{booking.procedureName}</span>
      </div>
      <div>
        <Caption1 className={styles.identityLabel}>Operative side</Caption1>
        {booking.laterality === 'NotApplicable' ? (
          <span className={styles.identityValue}>No side</span>
        ) : (
          <SideBadge laterality={booking.laterality} size="large" />
        )}
      </div>
      <div>
        <Caption1 className={styles.identityLabel}>Team</Caption1>
        <Text block>Surgeon: {booking.surgeonName ?? '—'}</Text>
        <Text block>
          Anaesthetist: {booking.anaesthetistName ?? (booking.anaesthesiaType === 'Local' ? 'Not needed (local)' : '—')}
        </Text>
        <Caption1>{anaesthesiaLabel[booking.anaesthesiaType]}</Caption1>
      </div>
    </div>
  );
}

/**
 * One pause of the WHO Surgical Safety Checklist. Each item is confirmed on its own (read aloud, ticked); a
 * confirmed item cannot be withdrawn and shows who confirmed it and when. When every item is done the phase locks.
 */
export function SafetyPhasePanel({
  booking,
  phase,
  canRecord,
  onComplete,
}: {
  booking: SurgeryDetail;
  phase: SafetyPhase;
  canRecord: boolean;
  onComplete?: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const phaseDto = booking.safetyChecklist.find((p) => p.phase === phase);
  const gate = phaseGate(booking, phase);
  const editable = gate.allowed && canRecord;
  const [selection, setSelection] = useState<Record<string, PhaseSelection>>({});

  const items = phaseDto?.items ?? [];
  const pending = items.filter((item) => item.state === 'Pending');
  const selectionOf = (key: string): PhaseSelection =>
    selection[key] ?? { confirmed: false, notApplicable: false, remarks: '' };
  const chosen = pending.filter((item) => selectionOf(item.key).confirmed || selectionOf(item.key).notApplicable);
  const missingReason = chosen.filter(
    (item) => selectionOf(item.key).notApplicable && !selectionOf(item.key).remarks.trim(),
  );
  const confirmedCount = items.length - pending.length;
  const completesPhase = chosen.length === pending.length && pending.length > 0;

  const confirm = useMutation({
    mutationFn: (inputs: ChecklistInput[]) => otApi.confirmSafety(booking.id, phase, inputs),
    onSuccess: () => {
      notify.success(
        completesPhase
          ? `${phaseLabel[phase]} complete`
          : `${chosen.length} item${chosen.length === 1 ? '' : 's'} confirmed`,
      );
      setSelection({});
      void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id) });
      void queryClient.invalidateQueries({ queryKey: otKeys.calendars });
      if (completesPhase) {
        onComplete?.();
      }
    },
  });

  const submit = () =>
    confirm.mutate(
      chosen.map((item) => {
        const s = selectionOf(item.key);
        return { key: item.key, state: s.notApplicable ? 'NotApplicable' : 'Done', remarks: s.remarks.trim() || null };
      }),
    );

  const update = (key: string, next: Partial<PhaseSelection>) =>
    setSelection((current) => ({ ...current, [key]: { ...selectionOf(key), ...next } }));

  return (
    <div className={styles.panel}>
      <div className={styles.summary}>
        <Text weight="semibold">
          {phaseLabel[phase]} · {phaseMoment[phase].toLowerCase()}
        </Text>
        <ProgressBar
          value={items.length ? confirmedCount / items.length : 0}
          color={phaseDto?.isComplete ? 'success' : 'brand'}
          thickness="large"
          aria-label={`${phaseLabel[phase]}: ${confirmedCount} of ${items.length} confirmed`}
        />
        <Caption1 className={styles.muted}>
          {confirmedCount} of {items.length} confirmed
        </Caption1>
      </div>

      {phaseDto?.isComplete ? (
        <MessageBar intent="success" icon={<LockClosedRegular />}>
          <MessageBarBody>
            <MessageBarTitle>{phaseLabel[phase]} complete</MessageBarTitle>
            Completed at {formatTime(phaseDto.completedAt)} and locked. Items cannot be changed.
          </MessageBarBody>
        </MessageBar>
      ) : gate.reason ? (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Not yet</MessageBarTitle>
            {gate.reason}
          </MessageBarBody>
        </MessageBar>
      ) : !canRecord ? (
        <MessageBar intent="info">
          <MessageBarBody>
            Only nurses, doctors and OT coordinators confirm checklist items. You can view them.
          </MessageBarBody>
        </MessageBar>
      ) : null}

      {phase !== 'SignOut' && !phaseDto?.isComplete && <IdentityCheck booking={booking} />}

      <div className={styles.items} role="list" aria-label={`${phaseLabel[phase]} items`}>
        {items.map((item) => {
          if (item.state !== 'Pending') {
            return (
              <div
                key={item.key}
                role="listitem"
                className={mergeClasses(styles.item, item.state === 'Done' ? styles.itemDone : styles.itemNa)}
              >
                <span className={styles.icon}>{stateIcon(item.state, styles)}</span>
                <div className={styles.itemBody}>
                  <Text weight="semibold">{item.text}</Text>
                  <WhoWhen prefix={recordedText(item)} by={item.recordedBy} at={item.recordedAt} locked />
                </div>
              </div>
            );
          }
          const s = selectionOf(item.key);
          return (
            <div
              key={item.key}
              role="listitem"
              className={mergeClasses(styles.item, (s.confirmed || s.notApplicable) && styles.itemEdited)}
            >
              <span className={styles.icon}>
                {stateIcon(s.confirmed ? 'Done' : s.notApplicable ? 'NotApplicable' : 'Pending', styles)}
              </span>
              <div className={styles.itemBody}>
                <Checkbox
                  size="large"
                  label={<Text weight="semibold">{item.text}</Text>}
                  checked={s.confirmed}
                  disabled={!editable || s.notApplicable}
                  onChange={(_, data) => update(item.key, { confirmed: data.checked === true })}
                />
                {editable && item.allowsNotApplicable && (
                  <div className={styles.controls}>
                    <Checkbox
                      label="Not applicable to this patient"
                      checked={s.notApplicable}
                      disabled={s.confirmed}
                      onChange={(_, data) => update(item.key, { notApplicable: data.checked === true })}
                    />
                    {s.notApplicable && (
                      <Field
                        className={styles.remarks}
                        validationState={!s.remarks.trim() ? 'error' : 'none'}
                        validationMessage={!s.remarks.trim() ? 'Say why it does not apply.' : undefined}
                      >
                        <Input
                          aria-label={`Why "${item.text}" does not apply`}
                          placeholder="Why it does not apply (required)"
                          value={s.remarks}
                          maxLength={500}
                          onChange={(_, data) => update(item.key, { remarks: data.value })}
                        />
                      </Field>
                    )}
                  </div>
                )}
              </div>
            </div>
          );
        })}
      </div>

      <ActionError
        error={confirm.error}
        onReload={() => void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id) })}
      />

      {editable && pending.length > 0 && (
        <div className={styles.footer}>
          <Caption1 className={styles.muted}>
            Read each item aloud with the team. Confirmed items cannot be withdrawn; your name and the time are
            recorded.
          </Caption1>
          <Button
            appearance="primary"
            size="large"
            disabled={chosen.length === 0 || missingReason.length > 0 || confirm.isPending}
            icon={confirm.isPending ? <Spinner size="tiny" aria-hidden /> : <CheckmarkCircleRegular />}
            onClick={submit}
          >
            {chosen.length === 0
              ? 'Tick the items confirmed'
              : completesPhase
                ? `Confirm and complete ${phaseLabel[phase]}`
                : `Confirm ${chosen.length} item${chosen.length === 1 ? '' : 's'}`}
          </Button>
        </div>
      )}
    </div>
  );
}
