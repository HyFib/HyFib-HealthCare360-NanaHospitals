import {
  Avatar,
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Select,
  Skeleton,
  SkeletonItem,
  Spinner,
  Text,
  tokens,
  useArrowNavigationGroup,
} from '@fluentui/react-components';
import { CalendarAddRegular, PersonAddRegular, PersonClockRegular, VideoRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatTime, isApiError, useHotkey } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  EmptyState,
  Page,
  PageHeader,
  Section,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useFeatureOn } from '@/lib/features';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { patientQueries } from '@/modules/patients/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import {
  schedulingApi,
  schedulingKeys,
  schedulingQueries,
  STAFF_CHANNELS,
  type AppointmentDetail,
  type PractitionerAvailability,
  type SlotDto,
} from '@/modules/scheduling/api';
import { useAppointmentDesk } from '@/modules/scheduling/useAppointmentDesk';
import { holdsSeat } from '@/modules/scheduling/appointmentRules';
import { BookingSuccessDialog } from '@/modules/scheduling/BookingSuccessDialog';
import { addDays, formatDayLong, isIsoDate, istDate } from '@/modules/scheduling/dates';
import { appointmentStatusText, channelLabel } from '@/modules/scheduling/labels';
import { countFree, slotsForMode, type VisitMode } from '@/modules/scheduling/slots';
import { SlotPicker } from '@/modules/scheduling/SlotPicker';
import { useToday } from '@/modules/scheduling/useNow';
import { AddToWaitlistDialog } from '@/modules/scheduling/WaitlistDialogs';

interface BookSearch {
  /** Pre-selects a patient (an id only, never patient data). */
  patientId?: string;
  departmentId?: string;
  practitionerId?: string;
  date?: string;
  video?: boolean;
  /** Pre-selects a slot, for example a free slot clicked on the calendar. */
  startsAt?: string;
}

export const Route = createFileRoute('/_app/scheduling/book')({
  validateSearch: (s: Record<string, unknown>): BookSearch => ({
    patientId: typeof s.patientId === 'string' && s.patientId ? s.patientId : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    practitionerId: typeof s.practitionerId === 'string' && s.practitionerId ? s.practitionerId : undefined,
    date: isIsoDate(s.date) ? s.date : undefined,
    video: s.video === true || s.video === 'true' ? true : undefined,
    startsAt: typeof s.startsAt === 'string' && !Number.isNaN(Date.parse(s.startsAt)) ? s.startsAt : undefined,
  }),
  component: BookAppointmentPage,
});

const CHANNEL_KEY = 'hc360:booking-channel';

type StaffChannel = (typeof STAFF_CHANNELS)[number];

/** The "booked through" channel this workstation used last (a desk mostly books walk-ins or phone calls). */
function savedChannel(): StaffChannel {
  try {
    const value = localStorage.getItem(CHANNEL_KEY);
    return STAFF_CHANNELS.find((channel) => channel === value) ?? 'WalkIn';
  } catch {
    return 'WalkIn';
  }
}

const detailsSchema = z.object({
  visitType: z.enum(['New', 'FollowUp']),
  channel: z.enum(STAFF_CHANNELS),
  reason: z.string().trim().max(300, 'Use at most 300 characters.'),
  notes: z.string().trim().max(1000, 'Use at most 1000 characters.'),
  notifyPatient: z.boolean(),
});

type DetailsValues = z.infer<typeof detailsSchema>;

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(300px, 360px)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  steps: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  summary: { position: 'sticky', top: '0', '@media (max-width: 1100px)': { position: 'static' } },
  patientRow: { display: 'flex', gap: '12px', alignItems: 'end', flexWrap: 'wrap' },
  picker: { flex: '1 1 320px', minWidth: 0 },
  upcoming: { display: 'flex', flexDirection: 'column', gap: '4px', marginTop: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
  doctors: {
    display: 'grid',
    gap: '8px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(230px, 1fr))',
    marginTop: '12px',
  },
  doctor: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    textAlign: 'start',
    cursor: 'pointer',
    fontFamily: 'inherit',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '1px' },
  },
  doctorSelected: { border: `2px solid ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorBrandBackground2 },
  doctorText: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0, flex: 1 },
  free: { color: tokens.colorPaletteGreenForeground1 },
  summaryList: {
    display: 'grid',
    gridTemplateColumns: 'auto 1fr',
    gap: '6px 12px',
    alignItems: 'baseline',
    marginBottom: '12px',
  },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  bookButton: { width: '100%', minHeight: '44px', fontSize: tokens.fontSizeBase400 },
  sectionHeaderExtra: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
});

/**
 * Online-Doctor-Appointment booking at the desk (PMS-02). The usual path is: find the patient → pick the department
 * and doctor → click a slot → Enter. The first day with a free slot is chosen automatically and the "booked through"
 * channel is remembered per workstation.
 */
function BookAppointmentPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const desk = useAppointmentDesk();
  const canManage = usePermission('scheduling.appointments.manage');
  // While video consultations are switched off, every booking is in person.
  const videoOn = useFeatureOn('telehealth');
  const today = useToday();
  const date = search.date && search.date >= today ? search.date : today;
  const departments = useDepartments('Clinical');
  const patientBox = useRef<HTMLDivElement>(null);

  // Patient: chosen in the picker, or pre-selected by id from another screen.
  const [picked, setPicked] = useState<PickedPatient | null | undefined>(undefined);
  const prefill = useQuery({
    ...patientQueries.detail(search.patientId ?? ''),
    enabled: Boolean(search.patientId) && picked === undefined,
  });
  const patient: PickedPatient | null =
    picked !== undefined
      ? picked
      : prefill.data
        ? { id: prefill.data.id, uhid: prefill.data.uhid, fullName: prefill.data.fullName }
        : null;

  // The chosen doctor's own availability tells us the department when only a doctor was given.
  const chosenDoctor = useQuery({
    ...schedulingQueries.slots({ practitionerId: search.practitionerId ?? null, from: date, to: date }),
    enabled: Boolean(search.practitionerId) && !search.departmentId,
  });
  const departmentId = search.departmentId ?? chosenDoctor.data?.[0]?.departmentId ?? undefined;
  const departmentDoctors = useQuery({
    ...schedulingQueries.slots({ departmentId: departmentId ?? null, from: date, to: date }),
    enabled: Boolean(departmentId),
  });
  const doctors = departmentDoctors.data ?? chosenDoctor.data ?? [];
  // One doctor in the department: choose them straight away.
  const practitionerId = search.practitionerId ?? (doctors.length === 1 ? doctors[0]!.practitionerId : undefined);
  const doctor = doctors.find((d) => d.practitionerId === practitionerId);
  const offersVideo = videoOn && Boolean(doctor?.offersTelehealth);
  const mode: VisitMode = offersVideo && search.video ? 'Telehealth' : 'InPerson';

  const [slot, setSlot] = useState<SlotDto | null>(null);
  const [problem, setProblem] = useState<{ title: string; message: string; appointmentId?: string } | null>(null);
  const [booked, setBooked] = useState<AppointmentDetail | null>(null);
  const [waitlistOpen, setWaitlistOpen] = useState(false);

  const upcoming = useQuery({
    ...schedulingQueries.appointments({ patientId: patient?.id, from: today, to: addDays(today, 180), pageSize: 5 }),
    enabled: Boolean(patient),
  });
  const upcomingOpen = (upcoming.data?.items ?? []).filter(
    (a) => holdsSeat(a.status) && a.status !== 'Completed' && a.status !== 'NoShow',
  );

  const form = useForm<DetailsValues>({
    resolver: zodResolver(detailsSchema),
    defaultValues: { visitType: 'New', channel: savedChannel(), reason: '', notes: '', notifyPatient: true },
  });

  // Start typing the patient's name, UHID or mobile straight away.
  useEffect(() => {
    if (!search.patientId) {
      patientBox.current?.querySelector('input')?.focus();
    }
  }, [search.patientId]);

  const set = (patch: Partial<BookSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  const book = useMutation({
    mutationFn: (values: DetailsValues) =>
      schedulingApi.book({
        patientId: patient!.id,
        practitionerId: practitionerId!,
        startsAt: slot!.startsAt,
        channel: values.channel,
        visitType: values.visitType,
        isTelehealth: mode === 'Telehealth',
        reason: values.reason || null,
        notes: values.notes || null,
        overbook: slot!.available <= 0,
        notifyPatient: values.notifyPatient,
        waitlistEntryId: null,
      }),
    onSuccess: (appointment, values) => {
      try {
        localStorage.setItem(CHANNEL_KEY, values.channel);
      } catch {
        // The channel preference is a convenience only.
      }
      setProblem(null);
      setBooked(appointment);
      void queryClient.invalidateQueries({ queryKey: schedulingKeys.all });
    },
    onError: (error) => {
      if (applyServerErrors(error, form.setError)) {
        return;
      }
      if (isApiError(error) && error.code === 'appointment.slot_full') {
        setSlot(null);
        void queryClient.invalidateQueries({ queryKey: schedulingKeys.slots });
      }
      const data = isApiError(error) ? (error.data as { appointmentId?: string } | undefined) : undefined;
      setProblem({
        title: isApiError(error) ? error.title : 'Could not book the appointment',
        message: isApiError(error) ? error.userMessage : 'Please try again.',
        appointmentId: data?.appointmentId,
      });
    },
  });

  const ready = Boolean(patient && practitionerId && slot);
  // The Book button stays disabled until patient, doctor and slot are chosen; the shortcut checks the same.
  const submit = form.handleSubmit((values) => {
    if (ready) {
      book.mutate(values);
    } else {
      notify.warning('Choose the patient, the doctor and a time slot first');
    }
  });
  useHotkey('mod+enter', () => ready && !book.isPending && void submit(), { allowInInputs: true });

  const reset = (keepPatient: boolean) => {
    setBooked(null);
    setSlot(null);
    setProblem(null);
    form.reset({ visitType: 'New', channel: form.getValues('channel'), reason: '', notes: '', notifyPatient: true });
    if (!keepPatient) {
      setPicked(null);
      void navigate({ search: {}, replace: true });
    }
  };

  return (
    <Page>
      <PageHeader
        title="Book appointment"
        subtitle="Find the patient, choose the doctor and a free slot. Ctrl+Enter books."
        breadcrumbs={[
          { label: 'Appointments', onClick: () => void navigate({ to: '/scheduling' }) },
          { label: 'Book appointment' },
        ]}
      />

      <div className={styles.layout}>
        <div className={styles.steps}>
          <Section title="1. Patient">
            <div className={styles.patientRow}>
              <div className={styles.picker} ref={patientBox}>
                {search.patientId && picked === undefined && prefill.isLoading ? (
                  <Skeleton aria-label="Loading the patient">
                    <SkeletonItem size={32} />
                  </Skeleton>
                ) : (
                  <PatientPicker
                    value={patient}
                    onChange={(p) => {
                      setPicked(p);
                      setProblem(null);
                      set({ patientId: p?.id });
                    }}
                    required
                    hint="Type at least 3 characters of the UHID, mobile number or name"
                  />
                )}
              </div>
              <Button
                appearance="secondary"
                icon={<PersonAddRegular />}
                onClick={() => void navigate({ to: '/patients/register' })}
              >
                Register new patient
              </Button>
            </div>
            {prefill.error && picked === undefined && (
              <MessageBar intent="warning" style={{ marginTop: 8 }}>
                <MessageBarBody>
                  {isApiError(prefill.error) ? prefill.error.userMessage : 'The patient could not be loaded.'} Search
                  for the patient instead.
                </MessageBarBody>
              </MessageBar>
            )}
            {patient && upcomingOpen.length > 0 && (
              <div className={styles.upcoming}>
                <Caption1 className={styles.muted}>Already booked:</Caption1>
                {upcomingOpen.slice(0, 3).map((a) => (
                  <Caption1 key={a.id}>
                    {formatDateTime(a.startsAt)} · {a.practitionerName} · {appointmentStatusText[a.status].label}
                  </Caption1>
                ))}
              </div>
            )}
          </Section>

          <Section title="2. Doctor" description={`Free slots shown for ${formatDayLong(date)}`}>
            <Field label="Department" style={{ maxWidth: 360 }}>
              <Select
                value={departmentId ?? ''}
                onChange={(_, d) => {
                  setSlot(null);
                  set({ departmentId: d.value || undefined, practitionerId: undefined, startsAt: undefined });
                }}
              >
                <option value="">Choose department</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            {departmentId ? (
              <DoctorCards
                doctors={doctors}
                loading={departmentDoctors.isLoading}
                error={departmentDoctors.error}
                selectedId={practitionerId}
                mode={mode}
                showVideo={videoOn}
                onSelect={(id) => {
                  setSlot(null);
                  set({ practitionerId: id, startsAt: undefined });
                }}
              />
            ) : (
              <Caption1 className={styles.muted}>Choose a department to see its doctors and their free slots.</Caption1>
            )}
          </Section>

          <Section
            title="3. Date and time"
            actions={
              <div className={styles.sectionHeaderExtra}>
                {offersVideo && (
                  <RadioGroup
                    layout="horizontal"
                    aria-label="Visit mode"
                    value={mode}
                    onChange={(_, d) => {
                      setSlot(null);
                      set({ video: d.value === 'Telehealth' ? true : undefined, startsAt: undefined });
                    }}
                  >
                    <Radio value="InPerson" label="In person" />
                    <Radio value="Telehealth" label="Video consultation" />
                  </RadioGroup>
                )}
                {practitionerId && (
                  <Button appearance="subtle" icon={<PersonClockRegular />} onClick={() => setWaitlistOpen(true)}>
                    Add to waitlist
                  </Button>
                )}
              </div>
            }
          >
            {practitionerId ? (
              <SlotPicker
                practitionerId={practitionerId}
                practitionerName={doctor?.practitionerName}
                mode={mode}
                date={date}
                onDateChange={(d) => set({ date: d === today ? undefined : d })}
                value={slot}
                onChange={(s) => {
                  setSlot(s);
                  setProblem(null);
                }}
                allowOverbook={canManage}
                preselectStartsAt={search.startsAt}
              />
            ) : (
              <EmptyState
                title="Choose a doctor"
                description="Their free slots for the next two weeks appear here."
                icon={<CalendarAddRegular />}
              />
            )}
          </Section>
        </div>

        <aside className={styles.summary} aria-label="Booking summary">
          <Section title="Summary">
            <form onSubmit={(event) => void submit(event)} noValidate className={styles.form}>
              <div className={styles.summaryList}>
                <Caption1 className={styles.muted}>Patient</Caption1>
                <Text weight={patient ? 'semibold' : 'regular'}>
                  {patient ? `${patient.fullName} (${patient.uhid})` : 'Not chosen'}
                </Text>
                <Caption1 className={styles.muted}>Doctor</Caption1>
                <Text>
                  {doctor
                    ? `${doctor.practitionerName}${doctor.departmentName ? ` · ${doctor.departmentName}` : ''}`
                    : 'Not chosen'}
                </Text>
                <Caption1 className={styles.muted}>When</Caption1>
                <Text weight={slot ? 'semibold' : 'regular'}>
                  {slot ? `${formatDayLong(slot.date)}, ${formatTime(slot.startsAt)}` : 'Choose a slot'}
                </Text>
                <Caption1 className={styles.muted}>Visit</Caption1>
                <Text>
                  {mode === 'Telehealth' ? (
                    <Badge appearance="tint" color="brand" icon={<VideoRegular />}>
                      Video consultation
                    </Badge>
                  ) : (
                    `In person${slot?.room ? ` · Room ${slot.room}` : ''}`
                  )}
                </Text>
              </div>

              {slot && slot.available <= 0 && (
                <MessageBar intent="warning">
                  <MessageBarBody>This slot is full. Booking it adds one extra patient (overbooking).</MessageBarBody>
                </MessageBar>
              )}

              <ChoiceField
                control={form.control}
                name="visitType"
                label="Visit type"
                options={[
                  { value: 'New', label: 'New visit' },
                  { value: 'FollowUp', label: 'Follow-up' },
                ]}
              />
              <SelectField
                control={form.control}
                name="channel"
                label="Booked through"
                options={STAFF_CHANNELS.map((c) => ({ value: c, label: channelLabel[c] }))}
              />
              <TextField
                control={form.control}
                name="reason"
                label="Reason for visit (optional)"
                maxLength={300}
                placeholder="e.g. fever for 3 days"
              />
              <TextAreaField
                control={form.control}
                name="notes"
                label="Note for the desk (optional)"
                rows={2}
                maxLength={1000}
              />
              <SwitchField control={form.control} name="notifyPatient" label="Send confirmation by SMS/WhatsApp" />

              {problem && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>{problem.title}</MessageBarTitle>
                    {problem.message}
                  </MessageBarBody>
                  {problem.appointmentId && (
                    <MessageBarActions>
                      <Button
                        size="small"
                        onClick={() => void navigate({ to: '/scheduling', search: { open: problem.appointmentId } })}
                      >
                        Open that appointment
                      </Button>
                    </MessageBarActions>
                  )}
                </MessageBar>
              )}

              <Button
                type="submit"
                appearance="primary"
                className={styles.bookButton}
                icon={book.isPending ? <Spinner size="tiny" /> : <CalendarAddRegular />}
                disabled={!ready || book.isPending}
                aria-keyshortcuts="Control+Enter"
              >
                Book appointment
              </Button>
            </form>
          </Section>
        </aside>
      </div>

      <BookingSuccessDialog
        appointment={booked}
        desk={desk}
        onClose={() => {
          const done = booked;
          reset(true);
          if (done) {
            const day = istDate(done.startsAt);
            void navigate({ to: '/scheduling', search: { date: day === today ? undefined : day, open: done.id } });
          }
        }}
        onNewBooking={() => reset(false)}
      />
      <AddToWaitlistDialog
        open={waitlistOpen}
        defaults={{
          patient,
          departmentId: departmentId ?? null,
          practitionerId: practitionerId ?? null,
          isTelehealth: mode === 'Telehealth',
          from: date,
        }}
        onClose={() => setWaitlistOpen(false)}
      />
      {desk.dialogs}
    </Page>
  );
}

function DoctorCards({
  doctors,
  loading,
  error,
  selectedId,
  mode,
  showVideo,
  onSelect,
}: {
  doctors: PractitionerAvailability[];
  loading: boolean;
  error: unknown;
  selectedId: string | undefined;
  mode: VisitMode;
  /** Video consultations are switched on: mark the doctors who offer them. */
  showVideo: boolean;
  onSelect: (id: string) => void;
}) {
  const styles = useStyles();
  const navigation = useArrowNavigationGroup({ axis: 'grid-linear' });

  if (error) {
    return (
      <MessageBar intent="error" style={{ marginTop: 12 }}>
        <MessageBarBody>{isApiError(error) ? error.userMessage : 'Could not load the doctors.'}</MessageBarBody>
      </MessageBar>
    );
  }
  if (loading) {
    return (
      <div className={styles.doctors}>
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} aria-label="Loading doctors">
            <SkeletonItem style={{ height: 56 }} />
          </Skeleton>
        ))}
      </div>
    );
  }
  if (doctors.length === 0) {
    return (
      <Caption1 className={styles.muted}>
        No doctor in this department has OPD sessions set up. Add sessions under Doctor schedules.
      </Caption1>
    );
  }

  return (
    <div className={styles.doctors} role="radiogroup" aria-label="Doctors" {...navigation}>
      {doctors.map((d) => {
        const selected = d.practitionerId === selectedId;
        const total = slotsForMode(d.slots, mode).length;
        const free = countFree(d.slots, mode);
        return (
          <button
            key={d.practitionerId}
            type="button"
            role="radio"
            aria-checked={selected}
            className={mergeClasses(styles.doctor, selected && styles.doctorSelected)}
            onClick={() => onSelect(d.practitionerId)}
          >
            <Avatar name={d.practitionerName} size={32} color="colorful" />
            <span className={styles.doctorText}>
              <Text weight="semibold" truncate wrap={false}>
                {d.practitionerName}
              </Text>
              <Caption1 className={free > 0 ? styles.free : styles.muted}>
                {total === 0
                  ? 'No OPD on this day'
                  : free === 0
                    ? 'Fully booked on this day'
                    : `${free} free on this day`}
              </Caption1>
            </span>
            {showVideo && d.offersTelehealth && <VideoRegular aria-label="Offers video consultations" />}
          </button>
        );
      })}
    </div>
  );
}
