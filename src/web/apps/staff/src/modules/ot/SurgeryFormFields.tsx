import {
  Button,
  Caption1,
  Combobox,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Option,
  Spinner,
  Tag,
  TagGroup,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, WarningRegular } from '@fluentui/react-icons';
import { formatInr, formatTime, todayIso, useDebouncedValue, type LookupItem } from '@hc360/core';
import {
  ChoiceField,
  DateField,
  FormGrid,
  FullRow,
  LookupField,
  NumberField,
  SelectField,
  TextAreaField,
  TextField,
} from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useWatch, type UseFormReturn } from 'react-hook-form';
import { searchPractitioners } from '@/lib/lookups';
import {
  ANAESTHESIA_TYPES,
  LATERALITIES,
  otQueries,
  searchProcedures,
  SURGERY_PRIORITIES,
  type OtCalendarBooking,
} from './api';
import { anaesthesiaLabel, lateralityLabel, priorityLabel, sideShort, surgeryStatusLabel } from './labels';
import { DURATION_PRESETS, localClashes } from './logic';
import { formatDuration } from './shared/datetime';
import { TimeField } from './shared/fields';
import { slotOf, type SurgeryFormValues } from './surgeryForm';

const useStyles = makeStyles({
  presets: { display: 'flex', gap: '6px', flexWrap: 'wrap', marginTop: '4px' },
  muted: { color: tokens.colorNeutralForeground3 },
  preview: { display: 'flex', flexDirection: 'column', gap: '6px' },
  row: {
    display: 'grid',
    gridTemplateColumns: 'auto minmax(0, 1fr)',
    gap: '2px 8px',
    alignItems: 'center',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  clash: { border: `1px solid ${tokens.colorPaletteRedBorder2}`, backgroundColor: tokens.colorPaletteRedBackground1 },
  clashIcon: { color: tokens.colorPaletteRedForeground1 },
  okIcon: { color: tokens.colorPaletteGreenForeground1 },
  sideHint: { fontWeight: tokens.fontWeightSemibold },
});

type Form = UseFormReturn<SurgeryFormValues>;

/** Planned procedure: pick from the service catalogue (it sets the procedure charge) or type it in. */
export function ProcedurePicker({ form }: { form: Form }) {
  const [term, setTerm] = useState('');
  const search = useDebouncedValue(term.trim(), 300);
  const procedures = useQuery({
    queryKey: ['ot', 'procedures', search],
    queryFn: () => searchProcedures(search),
    staleTime: 5 * 60_000,
  });
  const [itemId] = useWatch({ control: form.control, name: ['procedureServiceItemId'] });
  const picked = procedures.data?.find((p) => p.id === itemId);

  return (
    <Controller
      control={form.control}
      name="procedureName"
      render={({ field, fieldState }) => (
        <Field
          label="Planned procedure"
          required
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
          hint={
            itemId
              ? `From the service catalogue${picked ? ` · ${formatInr(picked.basePrice, { whole: true })}` : ''}. The procedure charge is posted when surgery is completed.`
              : 'Type to search the service catalogue, or enter the procedure in words.'
          }
        >
          <Combobox
            freeform
            autoFocus={false}
            placeholder="e.g. Laparoscopic cholecystectomy"
            value={field.value}
            selectedOptions={itemId ? [itemId] : []}
            onChange={(event) => {
              field.onChange(event.target.value);
              setTerm(event.target.value);
              form.setValue('procedureServiceItemId', null);
            }}
            onOptionSelect={(_, data) => {
              const item = procedures.data?.find((p) => p.id === data.optionValue);
              if (item) {
                field.onChange(item.name);
                form.setValue('procedureServiceItemId', item.id, { shouldDirty: true });
                form.clearErrors('procedureName');
              }
            }}
            onBlur={field.onBlur}
            expandIcon={procedures.isFetching ? <Spinner size="extra-tiny" /> : undefined}
          >
            {(procedures.data ?? []).map((item) => (
              <Option key={item.id} value={item.id} text={item.name}>
                {item.name} · {item.code}
              </Option>
            ))}
          </Combobox>
        </Field>
      )}
    />
  );
}

/** Up to 10 assistant surgeons, searched by name; chosen ones show as removable tags. */
export function AssistantsPicker({ form, knownNames }: { form: Form; knownNames?: Record<string, string> }) {
  const [term, setTerm] = useState('');
  const search = useDebouncedValue(term.trim(), 300);
  const results = useQuery({
    queryKey: ['lookups', 'practitioners', 'search', search],
    queryFn: () => searchPractitioners(search),
    staleTime: 5 * 60_000,
  });
  const [names, setNames] = useState<Record<string, string>>(knownNames ?? {});
  const [surgeonId] = useWatch({ control: form.control, name: ['surgeonId'] });

  return (
    <Controller
      control={form.control}
      name="assistantIds"
      render={({ field, fieldState }) => {
        const selected = field.value;
        const options = (results.data ?? []).filter((p: LookupItem) => p.id !== surgeonId);
        return (
          <Field
            label="Assistant surgeons"
            hint="Optional"
            validationMessage={fieldState.error?.message}
            validationState={fieldState.error ? 'error' : 'none'}
          >
            <Combobox
              multiselect
              placeholder={selected.length ? `${selected.length} chosen · type to add` : 'Type a name to add'}
              value={term}
              selectedOptions={selected}
              onChange={(event) => setTerm(event.target.value)}
              onOptionSelect={(_, data) => {
                const chosen = results.data?.filter((p) => data.selectedOptions.includes(p.id)) ?? [];
                setNames((current) => ({ ...current, ...Object.fromEntries(chosen.map((p) => [p.id, p.name])) }));
                field.onChange(data.selectedOptions.slice(0, 10));
                setTerm('');
              }}
            >
              {options.map((p) => (
                <Option key={p.id} value={p.id} text={p.name}>
                  {p.name}
                </Option>
              ))}
            </Combobox>
            {selected.length > 0 && (
              <TagGroup
                aria-label="Chosen assistant surgeons"
                onDismiss={(_, data) => field.onChange(selected.filter((id) => id !== data.value))}
                style={{ marginTop: 6, flexWrap: 'wrap' }}
              >
                {selected.map((id) => (
                  <Tag key={id} value={id} dismissible dismissIcon={{ 'aria-label': 'Remove' }} size="small">
                    {names[id] ?? 'Assistant from the original booking'}
                  </Tag>
                ))}
              </TagGroup>
            )}
          </Field>
        );
      }}
    />
  );
}

/**
 * Procedure, operative side, team, anaesthesia and priority. The side is asked explicitly (no default) because it
 * is read back at the WHO Time-out to prevent wrong-site surgery.
 */
export function PlanFields({
  form,
  surgeonName,
  anaesthetistName,
  assistantNames,
  showPreferredDate,
}: {
  form: Form;
  surgeonName?: string | null;
  anaesthetistName?: string | null;
  assistantNames?: Record<string, string>;
  showPreferredDate: boolean;
}) {
  const styles = useStyles();
  const { control } = form;
  const [anaesthesiaType, laterality] = useWatch({ control, name: ['anaesthesiaType', 'laterality'] });
  // The current team's names, so the look-ups show them before any search (used once, at mount).
  const [surgeonItems] = useState<LookupItem[] | undefined>(() => {
    const id = form.getValues('surgeonId');
    return id && surgeonName ? [{ id, code: '', name: surgeonName }] : undefined;
  });
  const [anaesthetistItems] = useState<LookupItem[] | undefined>(() => {
    const id = form.getValues('anaesthetistId');
    return id && anaesthetistName ? [{ id, code: '', name: anaesthetistName }] : undefined;
  });
  const side = laterality ? sideShort(laterality) : null;

  return (
    <FormGrid columns={2}>
      <FullRow>
        <ProcedurePicker form={form} />
      </FullRow>
      <FullRow>
        <ChoiceField
          control={control}
          name="laterality"
          label="Operative side"
          required
          hint={
            side ? (
              <span>
                Side <span className={styles.sideHint}>{side}</span> is read back at the WHO Time-out. Mark the site
                before surgery.
              </span>
            ) : (
              'Checked again at the WHO Time-out to prevent wrong-site surgery.'
            )
          }
          options={LATERALITIES.map((value) => ({ value, label: lateralityLabel[value] }))}
        />
      </FullRow>
      <FullRow>
        <TextField control={control} name="diagnosis" label="Diagnosis" maxLength={500} />
      </FullRow>
      <LookupField
        control={control}
        name="surgeonId"
        label="Operating surgeon"
        load={searchPractitioners}
        initialItems={surgeonItems}
        placeholder="Type a name"
      />
      <SelectField
        control={control}
        name="anaesthesiaType"
        label="Anaesthesia"
        options={ANAESTHESIA_TYPES.map((value) => ({ value, label: anaesthesiaLabel[value] }))}
      />
      <LookupField
        control={control}
        name="anaesthetistId"
        label="Anaesthetist"
        load={searchPractitioners}
        initialItems={anaesthetistItems}
        placeholder="Type a name"
        hint={
          anaesthesiaType === 'Local'
            ? 'Not needed for local anaesthesia given by the surgeon.'
            : 'Must be a different doctor from the surgeon.'
        }
      />
      <AssistantsPicker form={form} knownNames={assistantNames} />
      <ChoiceField
        control={control}
        name="priority"
        label="Priority"
        options={SURGERY_PRIORITIES.map((value) => ({ value, label: priorityLabel[value] }))}
        hint="Emergency cases are listed first everywhere."
      />
      {showPreferredDate && (
        <DateField
          control={control}
          name="preferredDate"
          label="Preferred date"
          min={todayIso()}
          hint="The OT coordinator gives the slot."
        />
      )}
      <FullRow>
        <TextAreaField
          control={control}
          name="notes"
          label="Notes for the OT team"
          rows={2}
          maxLength={2000}
          placeholder="Special equipment, implants to arrange, isolation, blood to reserve…"
        />
      </FullRow>
    </FormGrid>
  );
}

/**
 * Theatre, date, start time and duration, with what is already booked that day in the chosen theatre and for the
 * chosen surgeon and patient. Clashes are flagged here as a hint; the server makes the final check under a lock.
 */
export function SlotFields({ form, bookingId }: { form: Form; bookingId?: string | null }) {
  const styles = useStyles();
  const { control } = form;
  const theatres = useQuery(otQueries.theatres());
  const [theatreId, date, time, durationMinutes, surgeonId, patient] = useWatch({
    control,
    name: ['theatreId', 'date', 'time', 'durationMinutes', 'surgeonId', 'patient'],
  });
  const slot = slotOf({ date, time, durationMinutes });

  return (
    <FormGrid columns={2}>
      <SelectField
        control={control}
        name="theatreId"
        label="Theatre"
        required
        placeholder={theatres.isLoading ? 'Loading…' : 'Choose theatre'}
        options={(theatres.data ?? []).map((t) => ({ value: t.id, label: `${t.code} · ${t.name}` }))}
      />
      <DateField control={control} name="date" label="Date" required min={todayIso()} />
      <TimeField control={control} name="time" label="Start time" required hint="Hospital time (IST)" />
      <div>
        <NumberField
          control={control}
          name="durationMinutes"
          label="Expected duration (minutes)"
          required
          min={15}
          max={1440}
          step={15}
        />
        <div className={styles.presets} role="group" aria-label="Common durations">
          {DURATION_PRESETS.map((minutes) => (
            <Button
              key={minutes}
              size="small"
              appearance={durationMinutes === minutes ? 'primary' : 'secondary'}
              onClick={() => form.setValue('durationMinutes', minutes, { shouldValidate: true, shouldDirty: true })}
            >
              {formatDuration(minutes)}
            </Button>
          ))}
        </div>
      </div>
      <FullRow>
        <Caption1 className={styles.muted}>
          {slot
            ? `From ${formatTime(slot.start)} to ${formatTime(slot.end)} (${formatDuration(durationMinutes)}).`
            : 'Choose the date, start time and duration.'}
        </Caption1>
      </FullRow>
      {date && (
        <FullRow>
          <DayPreview
            date={date}
            theatreId={theatreId}
            surgeonId={surgeonId}
            patientId={patient?.id ?? null}
            slot={slot}
            bookingId={bookingId ?? null}
          />
        </FullRow>
      )}
    </FormGrid>
  );
}

function DayPreview({
  date,
  theatreId,
  surgeonId,
  patientId,
  slot,
  bookingId,
}: {
  date: string;
  theatreId: string | null;
  surgeonId: string | null;
  patientId: string | null;
  slot: { start: string; end: string } | null;
  bookingId: string | null;
}) {
  const styles = useStyles();
  const calendar = useQuery(otQueries.calendar({ date, view: 'day' }));
  if (calendar.isLoading) {
    return <Spinner size="tiny" label="Checking what is booked that day…" labelPosition="after" />;
  }
  const all =
    calendar.data?.theatres.flatMap((t) => t.bookings.map((b) => ({ booking: b, theatreCode: t.code }))) ?? [];
  const relevant = all.filter(
    ({ booking }) =>
      booking.id !== bookingId &&
      (booking.theatreId === theatreId ||
        (surgeonId && booking.surgeonId === surgeonId) ||
        (patientId && booking.patientId === patientId)),
  );
  const clashing = new Set(
    slot
      ? localClashes(
          { ...slot, bookingId },
          relevant.map((r) => r.booking),
        ).map((b) => b.id)
      : [],
  );

  if (relevant.length === 0) {
    return (
      <Caption1 className={styles.muted}>
        <CheckmarkCircleRegular className={styles.okIcon} /> Nothing else is booked that day for this theatre
        {surgeonId ? ', surgeon' : ''}
        {patientId ? ' or patient' : ''}.
      </Caption1>
    );
  }

  return (
    <div className={styles.preview}>
      {clashing.size > 0 && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              The chosen time overlaps {clashing.size === 1 ? 'a booking' : `${clashing.size} bookings`}
            </MessageBarTitle>
            The same theatre, surgeon or patient cannot be booked twice at once. Change the time or the theatre.
          </MessageBarBody>
        </MessageBar>
      )}
      <Text weight="semibold">Already booked that day</Text>
      {relevant.map(({ booking, theatreCode }) => (
        <PreviewRow
          key={booking.id}
          booking={booking}
          theatreCode={theatreCode}
          clash={clashing.has(booking.id)}
          theatreId={theatreId}
          surgeonId={surgeonId}
          patientId={patientId}
        />
      ))}
    </div>
  );
}

function PreviewRow({
  booking,
  theatreCode,
  clash,
  theatreId,
  surgeonId,
  patientId,
}: {
  booking: OtCalendarBooking;
  theatreCode: string;
  clash: boolean;
  theatreId: string | null;
  surgeonId: string | null;
  patientId: string | null;
}) {
  const styles = useStyles();
  const why = [
    booking.theatreId === theatreId ? 'same theatre' : null,
    surgeonId && booking.surgeonId === surgeonId ? 'same surgeon' : null,
    patientId && booking.patientId === patientId ? 'same patient' : null,
  ]
    .filter(Boolean)
    .join(', ');
  return (
    <div className={clash ? `${styles.row} ${styles.clash}` : styles.row}>
      {clash ? (
        <WarningRegular className={styles.clashIcon} aria-label="Clashes with the chosen time" />
      ) : (
        <CheckmarkCircleRegular className={styles.okIcon} aria-label="No clash" />
      )}
      <Text>
        <Text weight="semibold">
          {formatTime(booking.start)}–{formatTime(booking.end)}
        </Text>{' '}
        · {theatreCode} · {booking.procedureName} · {booking.patientName} · {booking.surgeonName ?? 'surgeon not set'}
      </Text>
      <span />
      <Caption1 className={styles.muted}>
        {surgeryStatusLabel[booking.status]} · {why}
      </Caption1>
    </div>
  );
}
