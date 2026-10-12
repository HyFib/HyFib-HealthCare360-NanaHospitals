import {
  Button,
  Caption1,
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
  Subtitle2,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { ArrowResetRegular, CheckmarkCircleRegular, DismissCircleRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateOnly, isApiError } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  NumberField,
  SelectField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Controller, useForm, useWatch, type Control, type UseFormSetValue } from 'react-hook-form';
import { numberingApi, numberingKeys, numberingQueries, type NumberSeriesItem, type NumberSeriesList } from './api';
import {
  editorContext,
  numberSeriesFormSchema,
  toChange,
  toForm,
  toSaveRequest,
  type EditorContext,
  type NumberSeriesForm,
} from './form';
import {
  FORMAT_COLUMN_LENGTH,
  FORMAT_TOKENS,
  formatChecks,
  formatProblems,
  hospitalDate,
  insertToken,
  nextRestart,
  renderNumber,
  RESTART_LABELS,
  RESTART_OPTIONS,
  restartSentence,
  toIsoDate,
  type HospitalDate,
} from './numberFormat';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '14px' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  tokens: { display: 'flex', flexWrap: 'wrap', gap: '6px', marginTop: '-6px' },
  preview: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  example: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    wordBreak: 'break-all',
  },
  checks: { listStyleType: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '6px' },
  check: { display: 'flex', gap: '8px', alignItems: 'flex-start' },
  passed: { color: tokens.colorPaletteGreenForeground1, fontSize: '18px', flexShrink: 0 },
  failed: { color: tokens.colorPaletteRedForeground1, fontSize: '18px', flexShrink: 0 },
  restore: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  hidden: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    overflow: 'hidden',
    clipPath: 'inset(50%)',
    whiteSpace: 'nowrap',
  },
});

/**
 * Change one series. The editor works on a copy of the series taken when it opened, so a refresh of the list never
 * changes what is being typed. If someone else saved a change first, it starts again from their version.
 */
export function NumberSeriesDrawer({
  series,
  list,
  onClose,
}: {
  series: NumberSeriesItem;
  list: NumberSeriesList;
  onClose: () => void;
}) {
  const [base, setBase] = useState(series);
  const [round, setRound] = useState(0);
  const closing = useCloseRequests({ onClose });
  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={closing.onOpenChange}>
      <CloseRequestScope value={closing}>
        <NumberSeriesEditor
          key={round}
          series={base}
          context={editorContext(list, base.key)}
          onClose={onClose}
          onChangedElsewhere={(latest) => {
            setBase(latest);
            setRound((value) => value + 1);
          }}
        />
      </CloseRequestScope>
    </OverlayDrawer>
  );
}

function NumberSeriesEditor({
  series,
  context,
  onClose,
  onChangedElsewhere,
}: {
  series: NumberSeriesItem;
  context: EditorContext;
  onClose: () => void;
  onChangedElsewhere: (latest: NumberSeriesItem) => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<NumberSeriesForm>({
    resolver: zodResolver(numberSeriesFormSchema(series, context)),
    defaultValues: toForm(series),
  });

  const save = useMutation({
    mutationFn: (values: NumberSeriesForm) => numberingApi.save(series.key, toSaveRequest(values, series)),
    onSuccess: () => {
      notify.success('Number format saved', 'Numbers issued from now on follow it.');
      void queryClient.invalidateQueries({ queryKey: numberingKeys.all });
      onClose();
    },
    onError: async (error) => {
      if (isApiError(error) && error.code === 'numbering.changed') {
        notify.warning(
          'Someone else changed this number format',
          'The editor now shows the format as it is. Check it and make your change again.',
        );
        const fresh = await queryClient.fetchQuery({ ...numberingQueries.list(), staleTime: 0 });
        const latest = fresh.series.find((s) => s.key === series.key);
        if (latest) {
          onChangedElsewhere(latest);
        }
      } else if (isApiError(error) && error.code === 'numbering.busy') {
        notify.warning('Numbers of this kind are being issued right now', 'Save again in a moment.');
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the number format');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
          }
        >
          {series.name}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <NumberSeriesFields control={form.control} setValue={form.setValue} series={series} context={context} />
      </DrawerBody>
      <DrawerFooter>
        <Button type="submit" appearance="primary" disabled={save.isPending || !form.formState.isDirty}>
          Save
        </Button>
        <Button onClick={guard.requestClose}>Cancel</Button>
      </DrawerFooter>
      {guard.dialogs}
    </form>
  );
}

/**
 * The editor's fields: the format (with buttons that insert its parts where the cursor is), when numbers start again,
 * the next number, the number the next allocation would make, and the rules the format must follow. A locked series
 * shows why its format is fixed and offers the next number only.
 */
export function NumberSeriesFields({
  control,
  setValue,
  series,
  context,
  today = hospitalDate(),
}: {
  control: Control<NumberSeriesForm>;
  setValue: UseFormSetValue<NumberSeriesForm>;
  series: NumberSeriesItem;
  context: EditorContext;
  /** The hospital's date for the examples (today in India). */
  today?: HospitalDate;
}) {
  const styles = useStyles();
  const [format = series.format, resetPolicy = series.resetPolicy, startAt = null] = useWatch({
    control,
    name: ['format', 'resetPolicy', 'startAt'],
  });
  const change = toChange(series, context, {
    format,
    resetPolicy,
    startAt: typeof startAt === 'number' && !Number.isNaN(startAt) ? startAt : null,
  });
  const next = change.startAt !== null && change.startAt > series.lastNumber ? change.startAt : series.lastNumber + 1;
  const wellFormed = formatProblems(change.format).length === 0;
  const restart = nextRestart(change.resetPolicy, today);
  const isDefault = change.format === series.defaultFormat && change.resetPolicy === series.defaultResetPolicy;
  const restoreDefault = () => {
    setValue('format', series.defaultFormat, { shouldDirty: true, shouldValidate: true });
    setValue('resetPolicy', series.defaultResetPolicy, { shouldDirty: true, shouldValidate: true });
  };

  return (
    <div className={styles.form}>
      <Caption1 className={styles.muted}>
        {series.area} · {series.scope === 'global' ? 'Shared by all facilities' : "This facility's numbers"}
      </Caption1>

      {series.isLocked ? (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>The format is fixed</MessageBarTitle>
            {series.lockedReason} You can still set the next number, for example to carry on from an older system.
          </MessageBarBody>
        </MessageBar>
      ) : (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            A change applies to numbers issued from now on. Numbers already issued keep their format.
          </MessageBarBody>
        </MessageBar>
      )}

      {series.isLocked ? (
        <>
          <Field label="Format">
            <Text className={styles.mono}>{series.format}</Text>
          </Field>
          <Field label="Start again from 1">
            <Text>{RESTART_LABELS[series.resetPolicy]}</Text>
          </Field>
        </>
      ) : (
        <>
          <FormatInput control={control} />
          <SelectField
            control={control}
            name="resetPolicy"
            label="Start again from 1"
            required
            options={RESTART_OPTIONS.map((option) => ({ value: option.value, label: option.label }))}
            hint="Numbers that start again must contain the date of their period, so that no number is issued twice."
          />
        </>
      )}

      <NumberField
        control={control}
        name="startAt"
        label="Next number"
        min={1}
        hint={
          series.lastNumber > 0
            ? `Leave it empty to carry on from ${series.lastNumber + 1}. To continue from an older system, enter its next number; a later number leaves a gap.`
            : 'Leave it empty to start from 1. To continue from an older system, enter its next number.'
        }
      />

      <div className={styles.preview} aria-live="polite">
        <Caption1 className={styles.muted}>The next number will be</Caption1>
        {wellFormed ? (
          <Text className={styles.example}>{renderNumber(change.format, next, series.facilityCode, today)}</Text>
        ) : (
          <Text>Correct the format to see the next number.</Text>
        )}
        {wellFormed && restart && (
          <Caption1>
            From {formatDateOnly(toIsoDate(restart))} numbers start again:{' '}
            <span className={styles.mono}>{renderNumber(change.format, 1, series.facilityCode, restart)}</span>
          </Caption1>
        )}
      </div>

      {!series.isLocked && (
        <section aria-label="Checks">
          <Subtitle2 as="h3">Checks</Subtitle2>
          <ul className={styles.checks}>
            {formatChecks(change).map((check) => (
              <li key={check.text} className={styles.check}>
                {check.ok ? (
                  <CheckmarkCircleRegular className={styles.passed} aria-hidden />
                ) : (
                  <DismissCircleRegular className={styles.failed} aria-hidden />
                )}
                <Text>
                  <span className={styles.hidden}>{check.ok ? 'Passed: ' : 'To fix: '}</span>
                  {check.text}
                </Text>
              </li>
            ))}
          </ul>
        </section>
      )}

      {!series.isLocked && !isDefault && (
        <div className={styles.restore}>
          <Button icon={<ArrowResetRegular />} onClick={restoreDefault}>
            Restore default
          </Button>
          <Caption1 className={styles.muted}>
            Default: <span className={styles.mono}>{series.defaultFormat}</span>,{' '}
            {restartSentence(series.defaultResetPolicy)}
          </Caption1>
        </div>
      )}
    </div>
  );
}

/**
 * The format, in capitals as the administrator types, and buttons that put a part where the cursor is. {SEQ:n} goes in
 * as {SEQ:6} with the 6 selected, so typing a digit sets n.
 */
function FormatInput({ control }: { control: Control<NumberSeriesForm> }) {
  const styles = useStyles();
  const input = useRef<HTMLInputElement | null>(null);
  return (
    <Controller
      control={control}
      name="format"
      render={({ field, fieldState }) => {
        const insert = (token: string) => {
          const element = input.current;
          const value = field.value ?? '';
          const next = insertToken(
            value,
            element?.selectionStart ?? value.length,
            element?.selectionEnd ?? value.length,
            token,
          );
          field.onChange(next.value);
          // Once React has written the new value, put the cursor after the part (or select the n of {SEQ:n}).
          requestAnimationFrame(() => {
            element?.focus();
            element?.setSelectionRange(next.selectionStart, next.selectionEnd);
          });
        };
        return (
          <>
            <Field
              label="Format"
              required
              hint="Parts in braces are filled in for each number. Between them use capital letters, digits, - or /."
              validationMessage={fieldState.error?.message}
              validationState={fieldState.error ? 'error' : 'none'}
            >
              <Input
                ref={(element) => {
                  field.ref(element);
                  input.current = element;
                }}
                name={field.name}
                value={field.value ?? ''}
                onBlur={field.onBlur}
                onChange={(event, data) => {
                  // Capitals as typed, keeping the cursor where it is.
                  const upper = data.value.toUpperCase();
                  if (upper !== data.value) {
                    const { selectionStart, selectionEnd } = event.target;
                    event.target.value = upper;
                    event.target.setSelectionRange(selectionStart, selectionEnd);
                  }
                  field.onChange(upper);
                }}
                input={{ className: styles.mono }}
                maxLength={FORMAT_COLUMN_LENGTH}
                autoComplete="off"
                spellCheck={false}
                autoFocus
              />
            </Field>
            <div className={styles.tokens} role="group" aria-label="Insert a part">
              {FORMAT_TOKENS.map((part) => (
                <Tooltip key={part.token} content={part.meaning} relationship="description">
                  <Button size="small" className={styles.mono} onClick={() => insert(part.token)}>
                    {part.label}
                  </Button>
                </Tooltip>
              ))}
            </div>
          </>
        );
      }}
    />
  );
}
