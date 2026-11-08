import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { formatTime } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { WitnessPanel } from '@/modules/witness/WitnessPanel';
import type { WitnessConfirmed } from '@/modules/witness/api';
import {
  COUNT_CATEGORIES,
  COUNT_STAGES,
  DISCREPANCY_RESOLUTIONS,
  INCIDENT_HARMS,
  otApi,
  otKeys,
  type CountCategory,
  type CountDiscrepancy,
  type CountStage,
  type DiscrepancyResolution,
  type IncidentHarm,
  type SurgeryDetail,
} from './api';
import { countCategoryLabel, countStageLabel, discrepancyResolutionLabel, incidentHarmLabel } from './labels';
import { countsGate } from './logic';
import { ActionError } from './shared/feedback';

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: '16px' },
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '12px', alignItems: 'end' },
  actions: { display: 'flex', justifyContent: 'flex-end', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
  bad: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  good: { color: tokens.colorPaletteGreenForeground1 },
});

/**
 * The swab, needle and instrument counts (maker-checker slice 10). Each count is made by the signed-in member of the team
 * and witnessed by a second at this screen (the scrub and the circulating nurse); a final count that does not tally opens a
 * discrepancy, settled here (found, an X-ray with its result, or a retained item reported as a never event) before the WHO
 * Sign-out can complete.
 */
export function CountsPanel({ booking }: { booking: SurgeryDetail }) {
  const styles = useStyles();
  const canRecord = usePermission('ot.checklists.record');
  const gate = countsGate(booking);
  const tallies = booking.countTallies ?? [];
  const discrepancies = booking.countDiscrepancies ?? [];
  const open = discrepancies.filter((d) => d.isOpen);

  return (
    <div className={styles.panel}>
      {open.length > 0 && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>A count did not tally</MessageBarTitle>
            The WHO Sign-out waits until it is settled: found, an X-ray with its result, or a retained item reported.
          </MessageBarBody>
        </MessageBar>
      )}

      {tallies.length === 0 ? (
        <Text className={styles.muted}>
          Nothing counted yet. Record the initial count of each category before the incision.
        </Text>
      ) : (
        <Table aria-label="Counts so far" size="small">
          <TableHeader>
            <TableRow>
              <TableHeaderCell>What</TableHeaderCell>
              <TableHeaderCell>Initial</TableHeaderCell>
              <TableHeaderCell>Added</TableHeaderCell>
              <TableHeaderCell>Expected back</TableHeaderCell>
              <TableHeaderCell>Final</TableHeaderCell>
              <TableHeaderCell>Tallies</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {tallies.map((t) => (
              <TableRow key={t.category}>
                <TableCell>{countCategoryLabel[t.category]}</TableCell>
                <TableCell>{t.initial ?? '—'}</TableCell>
                <TableCell>{t.added}</TableCell>
                <TableCell>{t.expected ?? '—'}</TableCell>
                <TableCell>{t.final ?? '—'}</TableCell>
                <TableCell>
                  {!t.isFinal ? (
                    <span className={styles.muted}>Not counted at the end yet</span>
                  ) : t.tallies ? (
                    <span className={styles.good}>Yes</span>
                  ) : (
                    <span className={styles.bad}>No</span>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}

      {discrepancies.map((d) => (
        <DiscrepancyCard key={d.id} booking={booking} discrepancy={d} canResolve={canRecord && d.isOpen} />
      ))}

      {!gate.allowed ? (
        <MessageBar intent="info">
          <MessageBarBody>{gate.reason}</MessageBarBody>
        </MessageBar>
      ) : canRecord ? (
        <RecordCountForm booking={booking} />
      ) : (
        <MessageBar intent="info">
          <MessageBarBody>Only the theatre's nurses and doctors record counts. You can view them.</MessageBarBody>
        </MessageBar>
      )}

      {(booking.counts?.length ?? 0) > 0 && (
        <Table aria-label="Counts recorded" size="small">
          <TableHeader>
            <TableRow>
              <TableHeaderCell>Time</TableHeaderCell>
              <TableHeaderCell>What</TableHeaderCell>
              <TableHeaderCell>Count</TableHeaderCell>
              <TableHeaderCell>Number</TableHeaderCell>
              <TableHeaderCell>Counted by</TableHeaderCell>
              <TableHeaderCell>Witnessed by</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {booking.counts!.map((c) => (
              <TableRow key={c.id}>
                <TableCell>{formatTime(c.at)}</TableCell>
                <TableCell>{countCategoryLabel[c.category]}</TableCell>
                <TableCell>{countStageLabel[c.stage]}</TableCell>
                <TableCell>{c.number}</TableCell>
                <TableCell>{c.countedByName}</TableCell>
                <TableCell>{c.witnessName}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function RecordCountForm({ booking }: { booking: SurgeryDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [category, setCategory] = useState<CountCategory>('Swabs');
  const [stage, setStage] = useState<CountStage>('Initial');
  const [number, setNumber] = useState('');
  const [witness, setWitness] = useState<WitnessConfirmed | null>(null);
  const value = Number(number);
  const valid =
    number.trim() !== '' && Number.isInteger(value) && value >= (stage === 'Additional' ? 1 : 0) && value <= 9999;

  const record = useMutation({
    mutationFn: () =>
      otApi.recordCount(booking.id, {
        category,
        stage,
        number: value,
        witnessConfirmationId: witness!.confirmationId,
        remarks: null,
      }),
    onSuccess: () => {
      notify.success(`${countStageLabel[stage]} of ${countCategoryLabel[category].toLowerCase()} recorded`);
      setNumber('');
      // A witness's confirmation is good for one count only.
      setWitness(null);
      void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id) });
    },
    onError: () => setWitness(null),
  });

  return (
    <div className={styles.form} role="group" aria-label="Record a count">
      <Text weight="semibold">Record a count</Text>
      <div className={styles.row}>
        <Field label="What is counted">
          <Select value={category} onChange={(_, d) => setCategory(d.value as CountCategory)}>
            {COUNT_CATEGORIES.map((c) => (
              <option key={c} value={c}>
                {countCategoryLabel[c]}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Count">
          <Select value={stage} onChange={(_, d) => setStage(d.value as CountStage)}>
            {COUNT_STAGES.map((s) => (
              <option key={s} value={s}>
                {countStageLabel[s]}
              </option>
            ))}
          </Select>
        </Field>
        <Field
          label={stage === 'Additional' ? 'How many were added' : 'How many were counted'}
          required
          validationState={number.trim() !== '' && !valid ? 'error' : 'none'}
          validationMessage={
            number.trim() !== '' && !valid ? 'Enter a whole number from 0 to 9999 (at least 1 when added).' : undefined
          }
        >
          <Input
            type="number"
            inputMode="numeric"
            min={0}
            max={9999}
            value={number}
            onChange={(_, d) => setNumber(d.value)}
          />
        </Field>
      </div>
      <WitnessPanel
        purpose="ot.count"
        patientId={booking.patientId}
        witness={witness}
        onChange={setWitness}
        legend="Second member of the team (scrub or circulating nurse), at this screen"
        description="Every count is made by two: the second types their own user name and password here. Nothing is signed in, and the password is not kept."
      />
      <ActionError error={record.error} />
      <div className={styles.actions}>
        <Button
          appearance="primary"
          disabled={!valid || !witness || record.isPending}
          icon={record.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          onClick={() => record.mutate()}
        >
          {witness ? 'Record the count' : 'Record the count (witness first)'}
        </Button>
      </div>
    </div>
  );
}

function DiscrepancyCard({
  booking,
  discrepancy: d,
  canResolve,
}: {
  booking: SurgeryDetail;
  discrepancy: CountDiscrepancy;
  canResolve: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [resolution, setResolution] = useState<DiscrepancyResolution>('Found');
  const [note, setNote] = useState('');
  const [xray, setXray] = useState('');
  const [harm, setHarm] = useState<IncidentHarm | ''>('');
  const ready =
    note.trim().length > 0 &&
    (resolution !== 'XrayNoRetainedItem' || xray.trim().length > 0) &&
    (resolution !== 'RetainedItem' || harm !== '');

  const resolve = useMutation({
    mutationFn: () =>
      otApi.resolveDiscrepancy(booking.id, d.id, {
        resolution,
        note: note.trim(),
        xrayResult: xray.trim() || null,
        harm: resolution === 'RetainedItem' && harm ? harm : null,
      }),
    onSuccess: (settled) => {
      notify.success(
        settled.incidentNumber
          ? `Count settled; incident ${settled.incidentNumber} raised for the quality team`
          : 'Count settled',
      );
      void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id) });
    },
  });

  const title = `${countCategoryLabel[d.category]}: ${d.counted} counted, ${d.expected} expected`;
  if (!d.isOpen) {
    return (
      <MessageBar intent={d.resolution === 'RetainedItem' ? 'error' : 'success'} layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>{title}</MessageBarTitle>
          Settled as {d.resolution ? discrepancyResolutionLabel[d.resolution].toLowerCase() : '—'} by {d.resolvedByName}{' '}
          at {formatTime(d.resolvedAt)}: {d.resolutionNote}
          {d.xrayResult ? ` X-ray: ${d.xrayResult}.` : ''}
          {d.incidentNumber ? ` Incident ${d.incidentNumber} raised.` : ''}
        </MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div
      className={styles.form}
      role="group"
      aria-label={`Settle the ${countCategoryLabel[d.category].toLowerCase()} count`}
    >
      <Text weight="semibold" className={styles.bad}>
        {title}
      </Text>
      {canResolve ? (
        <>
          <RadioGroup
            aria-label="How the count was settled"
            value={resolution}
            onChange={(_, data) => setResolution(data.value as DiscrepancyResolution)}
          >
            {DISCREPANCY_RESOLUTIONS.map((r) => (
              <Radio key={r} value={r} label={discrepancyResolutionLabel[r]} />
            ))}
          </RadioGroup>
          <Field label={resolution === 'Found' ? 'Where it was found' : 'What was done'} required>
            <Textarea value={note} maxLength={1000} onChange={(_, data) => setNote(data.value)} />
          </Field>
          {resolution !== 'Found' && (
            <Field label="X-ray result" required={resolution === 'XrayNoRetainedItem'}>
              <Input value={xray} maxLength={1000} onChange={(_, data) => setXray(data.value)} />
            </Field>
          )}
          {resolution === 'RetainedItem' && (
            <Field
              label="Harm to the patient"
              required
              hint="A retained item is a never event: it is raised in the incident register, sentinel, and the quality team is alerted."
            >
              <Select value={harm} onChange={(_, data) => setHarm(data.value as IncidentHarm | '')}>
                <option value="">Choose…</option>
                {INCIDENT_HARMS.map((h) => (
                  <option key={h} value={h}>
                    {incidentHarmLabel[h]}
                  </option>
                ))}
              </Select>
            </Field>
          )}
          <ActionError error={resolve.error} />
          <div className={styles.actions}>
            <Button
              appearance="primary"
              disabled={!ready || resolve.isPending}
              icon={resolve.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => resolve.mutate()}
            >
              {resolution === 'RetainedItem' ? 'Report and settle' : 'Settle the count'}
            </Button>
          </div>
        </>
      ) : (
        <Caption1 className={styles.muted}>Waiting to be settled by the theatre team.</Caption1>
      )}
    </div>
  );
}
