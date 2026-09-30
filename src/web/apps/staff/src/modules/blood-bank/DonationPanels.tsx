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
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { BeakerRegular, DeleteRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, type BloodGroup } from '@hc360/core';
import { ConfirmDialog, DataTable, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { formatDuration } from '@/modules/ot/shared/datetime';
import { ActionError } from '@/modules/ot/shared/feedback';
import {
  bloodApi,
  bloodKeys,
  COMPONENT_TYPES,
  KNOWN_GROUPS,
  type BloodComponentType,
  type BloodUnitDto,
  type DonationDetail,
  type TtiPanel,
  type TtiResult,
} from './api';
import { BloodGroupBadge, ExpiryText, TtiBadge, UnitStatusBadge } from './BloodBadges';
import { componentLabel, groupLabel, ttiMarkers, ttiResultLabel } from './labels';
import { printUnitLabels } from './print';
import {
  canDiscardUnit,
  defaultVolumeMl,
  expiryFor,
  maxComponents,
  needsPromptSeparation,
  separationCheck,
  separationMinutesLeft,
  SHELF_LIFE_DAYS,
  ttiState,
} from './rules';
import { DiscardUnitDialog } from './UnitActions';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  markers: { display: 'flex', flexDirection: 'column', gap: '6px' },
  marker: {
    display: 'grid',
    gridTemplateColumns: 'minmax(160px, 1fr) auto',
    gap: '8px',
    alignItems: 'center',
    padding: '6px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  reactive: {
    border: `2px solid ${tokens.colorPaletteRedBorder2}`,
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  clean: { border: `1px solid ${tokens.colorPaletteGreenBorder2}` },
  resultText: { fontWeight: tokens.fontWeightSemibold },
  reactiveText: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightBold },
  footer: { display: 'flex', gap: '8px', justifyContent: 'flex-end', flexWrap: 'wrap', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  plan: { display: 'flex', flexDirection: 'column', gap: '6px' },
  planRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(220px, 1.4fr) minmax(90px, 0.6fr) minmax(140px, 1fr) minmax(120px, 1fr)',
    gap: '8px',
    alignItems: 'center',
    padding: '6px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  window: { fontWeight: tokens.fontWeightSemibold },
  late: { color: tokens.colorPaletteRedForeground1 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
});

// ---------------------------------------------------------------- grouping and TTI results

/**
 * ABO/Rh(D) group and the five mandatory TTI markers (HIV, HBsAg, HCV, syphilis, malaria). Units leave quarantine
 * only when grouped and every marker is non-reactive; any reactive marker discards every unit of the donation and
 * defers the donor. Results can be completed later but never changed once all five are in.
 */
export function LabResultsPanel({ donation, canManage }: { donation: DonationDetail; canManage: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const state = ttiState(donation.tti);
  const editable = canManage && !state.complete && donation.status !== 'Discarded';
  const [group, setGroup] = useState<BloodGroup | ''>(donation.bloodGroup === 'Unknown' ? '' : donation.bloodGroup);
  const [tti, setTti] = useState<TtiPanel>(donation.tti);
  const [confirmReactive, setConfirmReactive] = useState(false);
  const draft = ttiState(tti);

  const save = useMutation({
    mutationFn: () =>
      bloodApi.recordLabResults(donation.id, {
        bloodGroup: group || 'Unknown',
        hiv: tti.hiv,
        hbsAg: tti.hbsAg,
        hcv: tti.hcv,
        syphilis: tti.syphilis,
        malaria: tti.malaria,
      }),
    onSuccess: () => {
      setConfirmReactive(false);
      notify.success(
        draft.reactive.length > 0
          ? 'Reactive result recorded'
          : draft.cleared
            ? 'Results recorded — units released to stock'
            : 'Results saved',
        draft.reactive.length > 0
          ? 'All units from this donation were discarded and the donor was deferred.'
          : undefined,
      );
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
    },
    onError: () => setConfirmReactive(false),
  });

  if (!editable) {
    return (
      <div className={styles.stack}>
        {state.reactive.length > 0 && (
          <MessageBar intent="error" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Reactive screening result</MessageBarTitle>
              All units from this donation were discarded and the donor was deferred. Recall the donor for confirmatory
              testing and counselling.
            </MessageBarBody>
          </MessageBar>
        )}
        <div>
          <BloodGroupBadge group={donation.bloodGroup} note={donation.labResultsAt ? 'laboratory group' : undefined} />
        </div>
        <div className={styles.markers}>
          {ttiMarkers.map((m) => (
            <div
              key={m.key}
              className={mergeClasses(
                styles.marker,
                donation.tti[m.key] === 'Reactive' && styles.reactive,
                donation.tti[m.key] === 'NonReactive' && styles.clean,
              )}
            >
              <Text>{m.label}</Text>
              <Text className={donation.tti[m.key] === 'Reactive' ? styles.reactiveText : styles.resultText}>
                {ttiResultLabel[donation.tti[m.key]]}
              </Text>
            </div>
          ))}
        </div>
        <Caption1 className={styles.muted}>
          {donation.labResultsAt ? `Recorded ${formatDateTime(donation.labResultsAt)}` : 'No results yet.'}
        </Caption1>
      </div>
    );
  }

  const submit = () => (draft.reactive.length > 0 ? setConfirmReactive(true) : save.mutate());

  return (
    <div className={styles.stack}>
      <Field label="ABO and Rh(D) group of the donation" required>
        <Select value={group} onChange={(_, d) => setGroup(d.value as BloodGroup | '')}>
          <option value="">Choose the group</option>
          {KNOWN_GROUPS.map((g) => (
            <option key={g} value={g}>
              {groupLabel(g)}
            </option>
          ))}
        </Select>
      </Field>
      <div className={styles.markers} role="group" aria-label="Transfusion-transmitted infection screening">
        {ttiMarkers.map((m) => (
          <div key={m.key} className={mergeClasses(styles.marker, tti[m.key] === 'Reactive' && styles.reactive)}>
            <Text weight="semibold">{m.label}</Text>
            <RadioGroup
              layout="horizontal"
              aria-label={m.label}
              value={tti[m.key]}
              onChange={(_, d) => setTti((current) => ({ ...current, [m.key]: d.value as TtiResult }))}
            >
              <Radio value="Pending" label="Pending" />
              <Radio value="NonReactive" label="Non-reactive" />
              <Radio value="Reactive" label="Reactive" />
            </RadioGroup>
          </div>
        ))}
      </div>
      {draft.reactive.length > 0 ? (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>A reactive result discards every unit from this donation</MessageBarTitle>
            The donor is deferred (
            {draft.reactive.includes('hiv') || draft.reactive.includes('hbsAg') || draft.reactive.includes('hcv')
              ? 'permanently'
              : 'for a period'}
            ) and flagged for recall. Check the result before saving.
          </MessageBarBody>
        </MessageBar>
      ) : draft.cleared ? (
        <MessageBar intent="success">
          <MessageBarBody>All five markers non-reactive: the units leave quarantine when saved.</MessageBarBody>
        </MessageBar>
      ) : (
        <Caption1 className={styles.muted}>
          Pending markers keep every unit in quarantine. You can save now and complete the results later.
        </Caption1>
      )}
      <ActionError error={save.error} />
      <div className={styles.footer}>
        <Button
          appearance="primary"
          icon={save.isPending ? <Spinner size="tiny" /> : <BeakerRegular />}
          disabled={!group || save.isPending}
          onClick={submit}
        >
          Save results
        </Button>
      </div>
      <ConfirmDialog
        open={confirmReactive}
        title="Record a reactive result?"
        confirmLabel="Record reactive result"
        destructive
        busy={save.isPending}
        onConfirm={() => save.mutate()}
        onCancel={() => setConfirmReactive(false)}
      >
        Reactive: {draft.reactive.join(', ')}. Every unit from bag {donation.bagNumber} is discarded, the donor is
        deferred and officers are alerted for recall. This cannot be undone.
      </ConfirmDialog>
    </div>
  );
}

// ---------------------------------------------------------------- component separation

interface PlanRow {
  checked: boolean;
  volume: string;
  location: string;
}

/**
 * Component separation within the bag's limits: whole blood alone, or PRBC plus plasma/platelets/cryo up to the
 * number of satellite bags — and plasma or platelets only within 8 hours of collection.
 */
export function SeparationPanel({ donation, now }: { donation: DonationDetail; now: number }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const minutesLeft = separationMinutesLeft(donation.collectedAt, now);
  const [plan, setPlan] = useState<Record<BloodComponentType, PlanRow>>(() => {
    const initial = {} as Record<BloodComponentType, PlanRow>;
    for (const type of COMPONENT_TYPES) {
      const checked =
        donation.bagType === 'Single' ? type === 'WholeBlood' : type === 'PRBC' || (type === 'FFP' && minutesLeft >= 0);
      initial[type] = { checked, volume: String(defaultVolumeMl(type, donation.volumeMl)), location: '' };
    }
    return initial;
  });
  const chosen = COMPONENT_TYPES.filter((t) => plan[t].checked);
  const problem = separationCheck(donation.bagType, chosen, donation.collectedAt, now);
  const volumesValid = chosen.every((t) => {
    const volume = Number(plan[t].volume);
    return plan[t].volume === '' || (volume >= 5 && volume <= 600);
  });

  const separate = useMutation({
    mutationFn: () =>
      bloodApi.separate(
        donation.id,
        chosen.map((type) => ({
          type,
          volumeMl: plan[type].volume ? Number(plan[type].volume) : null,
          storageLocation: plan[type].location.trim() || null,
        })),
      ),
    onSuccess: (units) => {
      notify.success(
        `${units.length} unit${units.length === 1 ? '' : 's'} prepared (in quarantine)`,
        'Print the labels now; units are released when the screening results are in.',
      );
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
      void printUnitLabels(units).catch((error: unknown) => notify.error(error, 'Printing the labels failed'));
    },
  });

  const update = (type: BloodComponentType, next: Partial<PlanRow>) =>
    setPlan((current) => ({ ...current, [type]: { ...current[type], ...next } }));

  return (
    <div className={styles.stack}>
      <Text>
        {donation.bagType} bag: up to {maxComponents(donation.bagType)} component
        {maxComponents(donation.bagType) === 1 ? '' : 's'}.{' '}
        <span className={mergeClasses(styles.window, minutesLeft < 0 && styles.late)}>
          {minutesLeft >= 0
            ? `${formatDuration(minutesLeft)} left to separate plasma or platelets (8-hour window).`
            : 'The 8-hour window for plasma and platelets has passed: red cells or whole blood only.'}
        </span>
      </Text>
      <div className={styles.plan}>
        {COMPONENT_TYPES.map((type) => {
          const row = plan[type];
          const blockedByTime = needsPromptSeparation(type) && minutesLeft < 0;
          return (
            <div key={type} className={styles.planRow}>
              <Checkbox
                checked={row.checked}
                disabled={blockedByTime}
                onChange={(_, d) => update(type, { checked: d.checked === true })}
                label={
                  <span>
                    {componentLabel[type]}
                    <Caption1 block className={styles.muted}>
                      Keeps {SHELF_LIFE_DAYS[type]} days · expires{' '}
                      {formatDateTime(expiryFor(type, donation.collectedAt))}
                    </Caption1>
                  </span>
                }
              />
              <Input
                aria-label={`${componentLabel[type]} volume (ml)`}
                type="number"
                value={row.volume}
                disabled={!row.checked}
                onChange={(_, d) => update(type, { volume: d.value })}
                contentAfter={<Caption1>ml</Caption1>}
              />
              <Input
                aria-label={`${componentLabel[type]} storage location`}
                placeholder="Storage (e.g. Fridge 2, shelf B)"
                value={row.location}
                disabled={!row.checked}
                maxLength={64}
                onChange={(_, d) => update(type, { location: d.value })}
              />
              <Caption1 className={styles.muted}>
                {blockedByTime ? 'Too late to separate' : row.checked ? 'Will be prepared' : ''}
              </Caption1>
            </div>
          );
        })}
      </div>
      {problem && (
        <MessageBar intent="warning">
          <MessageBarBody>{problem}</MessageBarBody>
        </MessageBar>
      )}
      {!volumesValid && <Caption1 className={styles.late}>Volumes must be between 5 and 600 ml.</Caption1>}
      <ActionError error={separate.error} />
      <div className={styles.footer}>
        <Caption1 className={styles.muted}>Units start in quarantine and inherit the donation’s results.</Caption1>
        <Button
          appearance="primary"
          disabled={Boolean(problem) || !volumesValid || separate.isPending}
          icon={separate.isPending ? <Spinner size="tiny" /> : undefined}
          onClick={() => separate.mutate()}
        >
          Prepare {chosen.length} unit{chosen.length === 1 ? '' : 's'} and print labels
        </Button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- components of a donation

export function ComponentsTable({ units, now, canManage }: { units: BloodUnitDto[]; now: number; canManage: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const [discarding, setDiscarding] = useState<BloodUnitDto | null>(null);
  return (
    <>
      <DataTable
        ariaLabel="Blood units from this donation"
        rows={units}
        getRowId={(u) => u.id}
        size="small"
        columns={[
          { key: 'number', header: 'Unit number', render: (u) => <Text className={styles.mono}>{u.unitNumber}</Text> },
          {
            key: 'component',
            header: 'Component',
            render: (u) => `${componentLabel[u.componentType]} · ${u.volumeMl} ml`,
          },
          { key: 'group', header: 'Group', width: 110, render: (u) => groupLabel(u.bloodGroup) },
          { key: 'expiry', header: 'Expiry', render: (u) => <ExpiryText expiresAt={u.expiresAt} now={now} /> },
          { key: 'status', header: 'Status', render: (u) => <UnitStatusBadge status={u.status} /> },
          { key: 'tti', header: 'Screening', priority: 'low', render: (u) => <TtiBadge tti={u.tti} /> },
          { key: 'store', header: 'Stored at', priority: 'low', render: (u) => u.storageLocation ?? '—' },
        ]}
        rowActions={(u) => (
          <span style={{ display: 'inline-flex', gap: 4 }}>
            <Button
              size="small"
              appearance="subtle"
              icon={<PrintRegular />}
              aria-label={`Print label for ${u.unitNumber}`}
              onClick={() =>
                void printUnitLabels([u]).catch((error: unknown) => notify.error(error, 'Printing failed'))
              }
            />
            {canManage && canDiscardUnit(u.status) && (
              <Button
                size="small"
                appearance="subtle"
                icon={<DeleteRegular />}
                aria-label={`Discard ${u.unitNumber}`}
                onClick={() => setDiscarding(u)}
              />
            )}
          </span>
        )}
        emptyTitle="No components prepared yet"
      />
      <DiscardUnitDialog unit={discarding} onClose={() => setDiscarding(null)} />
    </>
  );
}
