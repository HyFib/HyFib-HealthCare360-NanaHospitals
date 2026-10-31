import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { todayIso } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  DateField,
  FormGrid,
  NumberField,
  SwitchField,
  TextField,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { fromZonedInput, isInFuture, toZonedInput } from './common/datetime';
import { errorMessage } from './common/errors';
import { labApi, labKeys, labQueries, type QcLotDto, type QcRunDto } from './api';
import { QcOutcomeBadge } from './badges';
import { qcOutcomeLabel, westgardRuleText } from './labels';
import { formatZ, sdZone, zScore } from './qc';
import { TestLookup } from './TestLookup';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  preview: {
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  warn: { color: tokens.colorPaletteMarigoldForeground1, fontWeight: tokens.fontWeightSemibold },
  bad: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  good: { color: tokens.colorPaletteGreenForeground1, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  timeRow: { display: 'flex', gap: '8px', alignItems: 'flex-end' },
  list: { margin: '4px 0 0', paddingLeft: '18px' },
});

// ------------------------------------------------------------------------------------------------ control lot

const lotSchema = z
  .object({
    test: z
      .object({ id: z.string(), code: z.string(), name: z.string(), unit: z.string().nullable().optional() })
      .nullable(),
    material: z
      .string()
      .trim()
      .min(1, 'Enter the control material, e.g. Bio-Rad Lyphochek Assayed Chemistry.')
      .max(200),
    level: z.enum(['1', '2', '3']),
    lotNumber: z.string().trim().min(1, 'Enter the lot number.').max(64),
    expiryDate: z.string().nullable(),
    targetMean: z.number().nullable(),
    targetSd: z.number().nullable(),
    unit: z.string().trim().max(32),
    instrumentCode: z.string().nullable(),
    isActive: z.boolean(),
  })
  .superRefine((values, ctx) => {
    if (!values.test) {
      ctx.addIssue({ code: 'custom', path: ['test'], message: 'Choose the analyte this control is run for.' });
    }
    if (!values.expiryDate) {
      ctx.addIssue({ code: 'custom', path: ['expiryDate'], message: 'Enter the expiry date.' });
    }
    if (values.targetMean === null || Number.isNaN(values.targetMean)) {
      ctx.addIssue({
        code: 'custom',
        path: ['targetMean'],
        message: 'Enter the target mean from the lot insert or your own data.',
      });
    }
    if (values.targetSd === null || Number.isNaN(values.targetSd) || values.targetSd <= 0) {
      ctx.addIssue({ code: 'custom', path: ['targetSd'], message: 'Enter an SD greater than zero.' });
    }
  });

type LotValues = z.infer<typeof lotSchema>;

/** Register or edit a QC control lot (targets are frozen by the server once runs exist). */
export function QcLotDrawer({
  lot,
  onClose,
  onSaved,
}: {
  lot: QcLotDto | null;
  onClose: () => void;
  onSaved?: (lotId: string) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const instruments = useQuery(labQueries.instruments());
  const form = useForm<LotValues>({
    resolver: zodResolver(lotSchema),
    defaultValues: {
      test: lot ? { id: lot.labTestId, code: lot.testCode, name: lot.testName, unit: lot.unit } : null,
      material: lot?.material ?? '',
      level: String(lot?.level ?? 1) as LotValues['level'],
      lotNumber: lot?.lotNumber ?? '',
      expiryDate: lot?.expiryDate ?? null,
      targetMean: lot?.targetMean ?? null,
      targetSd: lot?.targetSd ?? null,
      unit: lot?.unit ?? '',
      instrumentCode: lot?.instrumentCode ?? null,
      isActive: lot?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: (values: LotValues) =>
      labApi.saveQcLot(lot?.id ?? null, {
        labTestId: values.test!.id,
        material: values.material,
        level: Number(values.level),
        lotNumber: values.lotNumber,
        expiryDate: values.expiryDate!,
        targetMean: values.targetMean!,
        targetSd: values.targetSd!,
        unit: values.unit || null,
        instrumentCode: values.instrumentCode || null,
        isActive: values.isActive,
        version: lot?.version ?? null,
      }),
    onSuccess: (result) => {
      notify.success(lot ? 'Control lot saved' : 'Control lot registered');
      void queryClient.invalidateQueries({ queryKey: labKeys.qc });
      onSaved?.(lot?.id ?? result?.id ?? '');
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the control lot');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {lot ? `Control lot ${lot.lotNumber}` : 'Register a control lot'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            {lot && (
              <MessageBar intent="info">
                <MessageBarBody>
                  Once runs are recorded, the target mean and SD cannot change. Register a new lot for new targets.
                </MessageBarBody>
              </MessageBar>
            )}
            <Controller
              control={form.control}
              name="test"
              render={({ field, fieldState }) => (
                <TestLookup
                  label="Analyte"
                  required
                  value={field.value}
                  disabled={Boolean(lot)}
                  filter={(test) => test.resultType === 'Numeric' && !test.isPanel}
                  validationMessage={fieldState.error?.message}
                  onChange={(test) => {
                    field.onChange(test ? { id: test.id, code: test.code, name: test.name, unit: test.unit } : null);
                    if (test?.unit && !form.getValues('unit')) {
                      form.setValue('unit', test.unit);
                    }
                  }}
                />
              )}
            />
            <TextField control={form.control} name="material" label="Control material" required />
            <FormGrid columns={2}>
              <ChoiceField
                control={form.control}
                name="level"
                label="Level"
                options={[
                  { value: '1', label: 'Level 1 (low)' },
                  { value: '2', label: 'Level 2' },
                  { value: '3', label: 'Level 3 (high)' },
                ]}
              />
              <TextField control={form.control} name="lotNumber" label="Lot number" required />
              <DateField
                control={form.control}
                name="expiryDate"
                label="Expiry date"
                required
                min={lot ? undefined : todayIso()}
              />
              <Controller
                control={form.control}
                name="instrumentCode"
                render={({ field }) => (
                  <Field
                    label="Analyser"
                    hint="QC is judged per analyser: only QC run on an analyser lets its results be auto-verified"
                  >
                    <Select value={field.value ?? ''} onChange={(_, data) => field.onChange(data.value || null)}>
                      <option value="">None (manual method)</option>
                      {instruments.data?.map((instrument) => (
                        <option key={instrument.id} value={instrument.code}>
                          {instrument.name} ({instrument.code})
                        </option>
                      ))}
                    </Select>
                  </Field>
                )}
              />
              <NumberField control={form.control} name="targetMean" label="Target mean" required step={0.0001} />
              <NumberField control={form.control} name="targetSd" label="Target SD" required step={0.0001} min={0} />
              <TextField control={form.control} name="unit" label="Unit" />
            </FormGrid>
            <SwitchField control={form.control} name="isActive" label="Lot is in use" />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {lot ? 'Save' : 'Register lot'}
          </Button>
          <Button onClick={guard.requestClose}>Cancel</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}

// ------------------------------------------------------------------------------------------------ QC run

export type RunLot = Pick<
  QcLotDto,
  'id' | 'testName' | 'level' | 'lotNumber' | 'unit' | 'targetMean' | 'targetSd' | 'material'
>;

const ZONE_TEXT = {
  within1: { text: 'Within 1 SD', tone: 'good' },
  within2: { text: 'Within 2 SD', tone: 'good' },
  within3: { text: 'Beyond 2 SD: at least a 1-2s warning', tone: 'warn' },
  beyond3: { text: 'Beyond 3 SD: the run will be rejected (1-3s)', tone: 'bad' },
} as const;

/**
 * QC run entry with a live z-score, then the Westgard verdict from the server. A rejected run locks verification of
 * the assay, so the dialog goes straight on to the corrective action.
 */
export function QcRunDialog({ lot, onClose }: { lot: RunLot; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [value, setValue] = useState('');
  const [runAt, setRunAt] = useState(() => toZonedInput(new Date()));
  const [recorded, setRecorded] = useState<QcRunDto | null>(null);
  const [reviewing, setReviewing] = useState(false);

  const numeric = value.trim() === '' ? null : Number(value.replace(/,/g, ''));
  const valid = numeric !== null && Number.isFinite(numeric);
  const runIso = fromZonedInput(runAt);
  const timeProblem = !runIso
    ? 'Enter the run time.'
    : isInFuture(runIso, new Date())
      ? 'The run time cannot be in the future.'
      : null;
  const z = valid ? zScore(numeric, lot.targetMean, lot.targetSd) : null;
  const zone = z !== null ? ZONE_TEXT[sdZone(z)] : null;

  const record = useMutation({
    mutationFn: () => labApi.recordQcRun(lot.id, numeric!, runIso),
    onSuccess: (run) => {
      setRecorded(run);
      void queryClient.invalidateQueries({ queryKey: labKeys.qc });
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ value, runAt }) && recorded === null,
    busy: record.isPending,
    onClose,
  });

  if (recorded && reviewing) {
    return <QcReviewDialog run={recorded} testName={lot.testName} onClose={onClose} />;
  }

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        {recorded ? (
          <DialogBody>
            <DialogTitle>QC run recorded</DialogTitle>
            <DialogContent className={styles.content}>
              <div>
                <QcOutcomeBadge outcome={recorded.outcome} />{' '}
                <Text>
                  {recorded.value} {lot.unit ?? ''} · {formatZ(recorded.zScore)}
                </Text>
              </div>
              {recorded.violations.length > 0 && (
                <MessageBar intent={recorded.outcome === 'Rejected' ? 'error' : 'warning'} layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>{recorded.outcome === 'Rejected' ? 'Run rejected' : 'Warning'}</MessageBarTitle>
                    <ul className={styles.list}>
                      {recorded.violations.map((rule) => (
                        <li key={rule}>{westgardRuleText[rule] ?? rule}</li>
                      ))}
                    </ul>
                    {recorded.outcome === 'Rejected'
                      ? `Verification of ${lot.testName} is locked until QC passes or a pathologist overrides it. Record what you did to correct it.`
                      : 'Patient results may be reported. Watch the next runs closely.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button
                onClick={() => {
                  setRecorded(null);
                  setValue('');
                  setRunAt(toZonedInput(new Date()));
                }}
              >
                Record another run
              </Button>
              {recorded.outcome === 'Rejected' ? (
                <Button appearance="primary" onClick={() => setReviewing(true)} autoFocus>
                  Record corrective action
                </Button>
              ) : (
                <Button appearance="primary" onClick={onClose} autoFocus>
                  Done
                </Button>
              )}
            </DialogActions>
          </DialogBody>
        ) : (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              if (valid && !timeProblem && !record.isPending) {
                record.mutate();
              }
            }}
          >
            <DialogBody>
              <DialogTitle>
                Record QC run
                <Caption1 block className={styles.muted}>
                  {lot.testName} · level {lot.level} · lot {lot.lotNumber} · target {lot.targetMean} ± {lot.targetSd}{' '}
                  {lot.unit ?? ''}
                </Caption1>
              </DialogTitle>
              <DialogContent className={styles.content}>
                {record.error && (
                  <MessageBar intent="error">
                    <MessageBarBody>{errorMessage(record.error, 'The QC run could not be recorded.')}</MessageBarBody>
                  </MessageBar>
                )}
                <Field
                  label="Control value"
                  required
                  validationMessage={value && !valid ? 'Enter a number.' : undefined}
                  validationState={value && !valid ? 'error' : 'none'}
                >
                  <Input
                    value={value}
                    onChange={(_, data) => setValue(data.value)}
                    inputMode="decimal"
                    autoFocus
                    contentAfter={lot.unit ? <Caption1>{lot.unit}</Caption1> : undefined}
                  />
                </Field>
                {zone && z !== null && (
                  <div className={styles.preview} aria-live="polite">
                    <Text>{formatZ(z)} · </Text>
                    <Text className={styles[zone.tone]}>{zone.text}</Text>
                    <Caption1 block className={styles.muted}>
                      Rules that depend on earlier runs (2-2s, R-4s, 4-1s, 10x) are checked when you record.
                    </Caption1>
                  </div>
                )}
                <Field
                  label="Run time (hospital time)"
                  required
                  validationMessage={timeProblem ?? undefined}
                  validationState={timeProblem ? 'error' : 'none'}
                >
                  <div className={styles.timeRow}>
                    <Input
                      type="datetime-local"
                      value={runAt}
                      onChange={(_, data) => setRunAt(data.value)}
                      style={{ flex: 1 }}
                    />
                    <Button onClick={() => setRunAt(toZonedInput(new Date()))}>Now</Button>
                  </div>
                </Field>
              </DialogContent>
              <DialogActions>
                <Button onClick={guard.requestClose} disabled={record.isPending}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={!valid || Boolean(timeProblem) || record.isPending}
                  // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                  disabledFocusable={record.isPending}
                  icon={record.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
                >
                  Record run
                </Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

// ------------------------------------------------------------------------------------------------ review

/** Review of a QC run; a rejected run needs the corrective action taken (recalibration, new reagent, rerun). */
export function QcReviewDialog({ run, testName, onClose }: { run: QcRunDto; testName: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [note, setNote] = useState(run.reviewNote ?? '');
  const [action, setAction] = useState(run.correctiveAction ?? '');
  const needsAction = run.outcome === 'Rejected';
  const valid = !needsAction || action.trim().length > 0;

  const review = useMutation({
    mutationFn: () => labApi.reviewQcRun(run.id, note.trim() || null, action.trim() || null),
    onSuccess: () => {
      notify.success('QC review saved');
      void queryClient.invalidateQueries({ queryKey: labKeys.qc });
      onClose();
    },
  });

  const guard = useUnsavedChanges({ dirty: useValuesChanged({ note, action }), busy: review.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !review.isPending) {
              review.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Review QC run
              <Caption1 block className={styles.muted}>
                {testName} · level {run.level} · lot {run.lotNumber} · {run.value} ({formatZ(run.zScore)}) ·{' '}
                {qcOutcomeLabel[run.outcome]}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {review.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(review.error, 'The review could not be saved.')}</MessageBarBody>
                </MessageBar>
              )}
              {run.violations.length > 0 && (
                <Caption1>Rules: {run.violations.map((rule) => westgardRuleText[rule] ?? rule).join('; ')}</Caption1>
              )}
              <Field
                label="Corrective action"
                required={needsAction}
                hint={
                  needsAction
                    ? 'Required for a rejected run, e.g. recalibrated, new reagent pack, control repeated within limits.'
                    : 'Optional'
                }
              >
                <Textarea
                  value={action}
                  onChange={(_, data) => setAction(data.value.slice(0, 1000))}
                  rows={3}
                  resize="vertical"
                  autoFocus={needsAction}
                />
              </Field>
              <Field label="Review note">
                <Textarea
                  value={note}
                  onChange={(_, data) => setNote(data.value.slice(0, 1000))}
                  rows={2}
                  resize="vertical"
                  autoFocus={!needsAction}
                />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={review.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || review.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={review.isPending}
                icon={review.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Save review
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
