import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  mergeClasses,
  Spinner,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
  ToggleButton,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowDownloadRegular,
  ArrowUploadRegular,
  ChevronLeftRegular,
  ChevronRightRegular,
  DeleteRegular,
  DropRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatTime, isApiError } from '@hc360/core';
import { DataTable, ErrorState, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import {
  balanceTone,
  formatBalance,
  formatMl,
  QUICK_VOLUMES,
  ROUTE_LABEL,
  routesFor,
  runningBalance,
  type FluidDirection,
  type FluidRoute,
} from '../fluidBalance';
import { nursingApi, nursingKeys, nursingQueries, type IntakeOutputDto } from '../nursingApi';
import { ReasonDialog } from '../ReasonDialog';
import { addDays, dutyDate, fromIstInput, SHIFT_LABEL, toIstInput } from '../time';

const useStyles = makeStyles({
  summary: {
    display: 'grid',
    gap: '10px',
    gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
    '@media (max-width: 560px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  total: {
    padding: '10px 14px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  big: {
    fontSize: tokens.fontSizeBase600,
    fontWeight: tokens.fontWeightSemibold,
    fontFamily: tokens.fontFamilyNumeric,
  },
  positive: { color: tokens.colorPaletteBlueForeground2 },
  negative: { color: tokens.colorPaletteRedForeground1 },
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    margin: '16px 0',
  },
  chips: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  chip: { minHeight: '40px', minWidth: '64px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'flex-end' },
  nav: { display: 'flex', alignItems: 'center', gap: '4px', marginBottom: '12px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  shifts: { marginTop: '12px' },
});

/**
 * Intake/output chart for the nursing day (08:00–08:00 IST) with totals, shift subtotals and a running balance.
 * Entries are never edited: a wrong entry is voided with a reason and a new one added.
 */
export function IntakeOutput({ admissionId, canWrite }: { admissionId: string; canWrite: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [today] = useState(() => dutyDate(new Date()));
  const [date, setDate] = useState(today);
  const chart = useQuery(nursingQueries.intakeOutput(admissionId, date));
  const [voiding, setVoiding] = useState<IntakeOutputDto | null>(null);

  const voidEntry = useMutation({
    mutationFn: ({ entry, reason }: { entry: IntakeOutputDto; reason: string }) =>
      nursingApi.voidIntakeOutput(entry.id, reason),
    onSuccess: () => {
      notify.success('Entry voided');
      setVoiding(null);
      void queryClient.invalidateQueries({ queryKey: nursingKeys.admission(admissionId) });
    },
  });

  const data = chart.data;
  const running = new Map(runningBalance(data?.entries ?? []).map((r) => [r.entry.id, r.balanceMl]));
  const total = data?.balance.total;

  return (
    <div>
      <div className={styles.nav}>
        <Button
          icon={<ChevronLeftRegular />}
          appearance="subtle"
          aria-label="Previous day"
          onClick={() => setDate(addDays(date, -1))}
        />
        <Text weight="semibold">
          {date === today ? 'Today (from 8 am)' : `${formatDateOnly(date)} 8 am to next day 8 am`}
        </Text>
        <Button
          icon={<ChevronRightRegular />}
          appearance="subtle"
          aria-label="Next day"
          disabled={date >= today}
          onClick={() => setDate(addDays(date, 1))}
        />
      </div>

      {chart.error ? (
        <ErrorState error={chart.error} onRetry={() => void chart.refetch()} />
      ) : (
        <>
          <div className={styles.summary} aria-live="polite">
            <div className={styles.total}>
              <Caption1 className={styles.muted}>Intake</Caption1>
              <span className={styles.big}>{total ? formatMl(total.intakeMl) : '…'}</span>
            </div>
            <div className={styles.total}>
              <Caption1 className={styles.muted}>Output</Caption1>
              <span className={styles.big}>{total ? formatMl(total.outputMl) : '…'}</span>
            </div>
            <div className={styles.total}>
              <Caption1 className={styles.muted}>Balance (intake − output)</Caption1>
              <span
                className={mergeClasses(
                  styles.big,
                  total && balanceTone(total.balanceMl) === 'positive' && styles.positive,
                  total && balanceTone(total.balanceMl) === 'negative' && styles.negative,
                )}
              >
                {total ? formatBalance(total.balanceMl) : '…'}
              </span>
            </div>
          </div>

          {data && (
            <Table size="extra-small" aria-label="Balance by shift" className={styles.shifts}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Shift</TableHeaderCell>
                  <TableHeaderCell>Intake</TableHeaderCell>
                  <TableHeaderCell>Output</TableHeaderCell>
                  <TableHeaderCell>Balance</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.balance.byShift.map((s) => (
                  <TableRow key={s.shift}>
                    <TableCell>{SHIFT_LABEL[s.shift]}</TableCell>
                    <TableCell>{formatMl(s.intakeMl)}</TableCell>
                    <TableCell>{formatMl(s.outputMl)}</TableCell>
                    <TableCell>{formatBalance(s.balanceMl)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </>
      )}

      {canWrite && date === today && <AddEntryForm admissionId={admissionId} />}

      <DataTable
        ariaLabel="Intake and output entries"
        rows={data?.entries}
        loading={chart.isLoading}
        getRowId={(e) => e.id}
        size="small"
        columns={[
          { key: 'time', header: 'Time', width: 90, render: (e) => formatTime(e.recordedAt) },
          {
            key: 'what',
            header: 'Entry',
            render: (e) => (
              <span>
                {e.direction === 'Intake' ? (
                  <ArrowDownloadRegular aria-label="Intake" />
                ) : (
                  <ArrowUploadRegular aria-label="Output" />
                )}{' '}
                <Text weight="semibold">{e.direction === 'Intake' ? 'In' : 'Out'}</Text> ·{' '}
                {ROUTE_LABEL[e.route] ?? e.route}
                {e.description ? ` · ${e.description}` : ''}
                {e.notes && <Caption1 block>{e.notes}</Caption1>}
              </span>
            ),
          },
          { key: 'volume', header: 'Volume', width: 100, align: 'end', render: (e) => formatMl(e.volumeMl) },
          {
            key: 'balance',
            header: 'Running balance',
            width: 140,
            align: 'end',
            priority: 'low',
            render: (e) => formatBalance(running.get(e.id) ?? 0),
          },
          { key: 'by', header: 'By', priority: 'low', render: (e) => e.recordedByName ?? '—' },
        ]}
        rowActions={
          canWrite
            ? (e) => (
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  aria-label={`Void ${formatMl(e.volumeMl)} at ${formatTime(e.recordedAt)}`}
                  onClick={() => setVoiding(e)}
                />
              )
            : undefined
        }
        emptyTitle="Nothing charted for this day"
        emptyDescription={
          canWrite && date === today ? 'Add oral intake, IV fluids, urine and drain output as they happen.' : undefined
        }
      />

      <ReasonDialog
        open={voiding !== null}
        title="Void this entry?"
        description={
          voiding
            ? `${voiding.direction === 'Intake' ? 'Intake' : 'Output'} of ${formatMl(voiding.volumeMl)} at ${formatTime(voiding.recordedAt)} is removed from the balance. Add the correct entry afterwards.`
            : undefined
        }
        placeholder="e.g. Entered on the wrong patient"
        confirmLabel="Void entry"
        destructive
        busy={voidEntry.isPending}
        error={voidEntry.error}
        onConfirm={(reason) => voiding && voidEntry.mutate({ entry: voiding, reason })}
        onCancel={() => {
          setVoiding(null);
          voidEntry.reset();
        }}
      />
    </div>
  );
}

function AddEntryForm({ admissionId }: { admissionId: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [direction, setDirection] = useState<FluidDirection>('Intake');
  const [route, setRoute] = useState<FluidRoute>('Oral');
  const [volume, setVolume] = useState('');
  const [description, setDescription] = useState('');
  const [earlier, setEarlier] = useState(false);
  const [limits] = useState(() => ({ now: toIstInput(new Date()) }));
  const [recordedAt, setRecordedAt] = useState(limits.now);

  const add = useMutation({
    mutationFn: () =>
      nursingApi.addIntakeOutput(admissionId, {
        direction,
        route,
        volumeMl: Number(volume),
        recordedAt: earlier ? fromIstInput(recordedAt) : null,
        description: description.trim() || null,
        notes: null,
      }),
    onSuccess: (entry) => {
      notify.success(`${entry.direction === 'Intake' ? 'Intake' : 'Output'} of ${formatMl(entry.volumeMl)} charted`);
      setVolume('');
      setDescription('');
      void queryClient.invalidateQueries({ queryKey: nursingKeys.admission(admissionId) });
    },
  });

  const volumeNumber = Number(volume);
  const valid =
    Number.isInteger(volumeNumber) &&
    volumeNumber >= 1 &&
    volumeNumber <= 10_000 &&
    (!earlier || fromIstInput(recordedAt) !== null);

  const switchDirection = (next: FluidDirection) => {
    setDirection(next);
    setRoute(next === 'Intake' ? 'Oral' : 'Urine');
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (valid && !add.isPending) {
      add.mutate();
    }
  };

  return (
    <form className={styles.form} onSubmit={submit} aria-label="Add intake or output">
      <div className={styles.chips} role="radiogroup" aria-label="Direction">
        <ToggleButton
          className={styles.chip}
          checked={direction === 'Intake'}
          icon={<ArrowDownloadRegular />}
          onClick={() => switchDirection('Intake')}
        >
          Intake
        </ToggleButton>
        <ToggleButton
          className={styles.chip}
          checked={direction === 'Output'}
          icon={<ArrowUploadRegular />}
          onClick={() => switchDirection('Output')}
        >
          Output
        </ToggleButton>
      </div>
      <div className={styles.chips} role="radiogroup" aria-label="Route">
        {routesFor(direction).map((r) => (
          <ToggleButton key={r} size="small" shape="circular" checked={route === r} onClick={() => setRoute(r)}>
            {ROUTE_LABEL[r]}
          </ToggleButton>
        ))}
      </div>
      <div className={styles.row}>
        <Field label="Volume (ml)" required>
          <Input
            type="number"
            inputMode="numeric"
            min={1}
            max={10000}
            value={volume}
            onChange={(_, d) => setVolume(d.value)}
            contentBefore={<DropRegular />}
            style={{ width: 150 }}
          />
        </Field>
        <div className={styles.chips} aria-label="Quick volumes">
          {QUICK_VOLUMES.map((v) => (
            <Button
              key={v}
              className={styles.chip}
              appearance={volume === String(v) ? 'primary' : 'secondary'}
              onClick={() => setVolume(String(v))}
            >
              {v}
            </Button>
          ))}
        </div>
      </div>
      <div className={styles.row}>
        <Field label="Details" style={{ flex: '1 1 220px' }}>
          <Input
            value={description}
            onChange={(_, d) => setDescription(d.value)}
            maxLength={200}
            placeholder={direction === 'Intake' ? 'e.g. RL 500 ml, tea, milk' : 'e.g. Catheter bag, right chest drain'}
          />
        </Field>
        <Switch checked={earlier} onChange={(_, d) => setEarlier(d.checked)} label="Earlier time" />
        {earlier && (
          <Field label="At">
            <Input
              type="datetime-local"
              value={recordedAt}
              max={limits.now}
              onChange={(_, d) => setRecordedAt(d.value)}
            />
          </Field>
        )}
        <Button
          type="submit"
          appearance="primary"
          icon={add.isPending ? <Spinner size="tiny" aria-hidden /> : <AddRegular />}
          disabled={!valid || add.isPending}
          style={{ minHeight: 40 }}
        >
          Add {direction === 'Intake' ? 'intake' : 'output'}
        </Button>
      </div>
      {add.error && (
        <MessageBar intent="error">
          <MessageBarBody>
            {isApiError(add.error) ? add.error.userMessage : 'The entry could not be saved.'}
          </MessageBarBody>
        </MessageBar>
      )}
    </form>
  );
}
