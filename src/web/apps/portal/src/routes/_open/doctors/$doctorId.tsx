import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Avatar,
  Button,
  Field,
  makeStyles,
  Select,
  tokens,
} from '@fluentui/react-components';
import {
  BuildingRegular,
  CalendarClockRegular,
  InfoRegular,
  LocalLanguageRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import { isApiError, useSession } from '@hc360/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate, useRouter } from '@tanstack/react-router';
import { useState } from 'react';
import { ChoiceCards } from '@/components/ChoiceCards';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { PortalUnavailable } from '@/components/FeatureOff';
import { BackLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards, Notice, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { addDays, daysBetween, istDateOf } from '@/i18n/format';
import { useFormat } from '@/i18n/useFormat';
import { usePortalFeatures } from '@/lib/features';
import { useNow } from '@/lib/hooks';
import { guidParam, isGuid } from '@/lib/ids';
import { useToast } from '@/lib/toast';
import type { PublicDoctorProfile, PublicSession, Weekday } from '@/lib/types';
import { describeWhen } from '@/lib/when';
import { appointmentApi, appointmentQueries } from '@/modules/appointments/api';
import { doctorQueries } from '@/modules/doctors/api';
import { BookingSheet } from '@/modules/doctors/BookingSheet';
import { DateStrip } from '@/modules/doctors/DateStrip';
import { SlotPicker } from '@/modules/doctors/SlotPicker';
import { chooseDay, freeSeatsByDay, STRIP_DAYS, stripDays, stripStartFor } from '@/modules/doctors/slots';
import { WaitlistSheet } from '@/modules/doctors/WaitlistSheet';
import { safeRedirect } from '@/lib/redirect';
import { useOptionalProfile } from '@/shell/profileContext';

interface DoctorPageSearch {
  /** Video consultation instead of a visit at the hospital. */
  mode?: 'video';
  /** Branch (facility) id for visits at the hospital. */
  branch?: string;
  /** Moving an existing appointment to a new time (its id). */
  reschedule?: string;
  /** A time chosen before signing in (ISO instant), kept so the patient does not have to find it again. */
  slot?: string;
}

export const Route = createFileRoute('/_open/doctors/$doctorId')({
  validateSearch: (search: Record<string, unknown>): DoctorPageSearch => ({
    mode: search.mode === 'video' ? 'video' : undefined,
    branch: guidParam(search.branch),
    reschedule: guidParam(search.reschedule),
    slot: typeof search.slot === 'string' && !Number.isNaN(Date.parse(search.slot)) ? search.slot : undefined,
  }),
  component: DoctorPage,
});

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (min-width: 900px)': { padding: '24px' },
  },
  identity: { display: 'flex', gap: '16px', alignItems: 'flex-start' },
  identityText: { display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0 },
  line: { color: tokens.colorNeutralForeground2, overflowWrap: 'anywhere' },
  meta: { display: 'flex', alignItems: 'center', gap: '6px', color: tokens.colorNeutralForeground2 },
  fees: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: '8px' },
  fee: {
    display: 'flex',
    flexDirection: 'column',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  feeLabel: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 },
  feeValue: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  sectionTitle: { margin: 0, fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  rules: {
    display: 'flex',
    gap: '8px',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    alignItems: 'flex-start',
  },
  empty: { color: tokens.colorNeutralForeground2, margin: 0 },
  bar: {
    position: 'sticky',
    bottom: 'calc(76px + env(safe-area-inset-bottom))',
    zIndex: 5,
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    padding: '12px 16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow28,
    border: `1px solid ${tokens.colorBrandStroke1}`,
    '@media (min-width: 900px)': { bottom: '16px' },
  },
  barText: { fontWeight: tokens.fontWeightSemibold },
  timings: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  timing: { display: 'flex', flexWrap: 'wrap', gap: '4px 12px', justifyContent: 'space-between' },
  bio: { margin: 0, whiteSpace: 'pre-line', lineHeight: tokens.lineHeightBase400 },
  waitlist: { alignSelf: 'flex-start' },
});

/** A Monday used to name weekdays in the patient's language (1 Jan 2024 was a Monday). */
const WEEKDAY_DATES: Record<Weekday, string> = {
  Monday: '2024-01-01',
  Tuesday: '2024-01-02',
  Wednesday: '2024-01-03',
  Thursday: '2024-01-04',
  Friday: '2024-01-05',
  Saturday: '2024-01-06',
  Sunday: '2024-01-07',
};

function DoctorPage() {
  const { doctorId } = Route.useParams();
  const t = useT();
  // Booking online needs the portal.
  const { portal, settled } = usePortalFeatures();
  const doctor = useQuery({ ...doctorQueries.profile(doctorId), enabled: isGuid(doctorId) && portal && settled });

  if (!portal) {
    return (
      <PortalPage
        title={t('doctors.title')}
        back={<BackLink to="/doctors">{t('doctors.title')}</BackLink>}
        width="narrow"
      >
        <PortalUnavailable />
      </PortalPage>
    );
  }

  if (!isGuid(doctorId) || (doctor.isError && isApiError(doctor.error) && doctor.error.status === 404)) {
    return (
      <PortalPage
        title={t('doctors.title')}
        back={<BackLink to="/doctors">{t('doctors.title')}</BackLink>}
        width="narrow"
      >
        <Notice intent="warning">{t('doctors.profile.notFound')}</Notice>
      </PortalPage>
    );
  }
  if (doctor.isPending) {
    return <PageSpinner />;
  }
  if (doctor.isError) {
    return (
      <PortalPage title={t('doctors.title')} back={<BackLink to="/doctors">{t('doctors.title')}</BackLink>}>
        <ErrorBlock error={doctor.error} onRetry={() => void doctor.refetch()} />
      </PortalPage>
    );
  }
  return <DoctorBooking profile={doctor.data} />;
}

function DoctorBooking({ profile: doctorProfile }: { profile: PublicDoctorProfile }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const toast = useToast();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const router = useRouter();
  const active = useOptionalProfile();
  const patientId = active?.profile.id ?? null;
  const signedIn = useSession((s) => s.status === 'authenticated' && s.user?.type === 'patient');
  const now = useNow(60_000);
  const doctor = doctorProfile.doctor;
  const [noRecords, setNoRecords] = useState(false);

  const filters = useQuery(doctorQueries.filters());
  const reschedule = useQuery({
    ...appointmentQueries.detail(patientId ?? '-', search.reschedule ?? ''),
    enabled: Boolean(search.reschedule && patientId),
  });
  const moving = search.reschedule ? (reschedule.data ?? null) : null;

  // Without video consultations (turned off by the hospital) every booking is a visit at the hospital.
  const videoOn = usePortalFeatures().video;
  const offersVideo = videoOn && doctor.offersTelehealth;
  const video = moving ? moving.isTelehealth && videoOn : search.mode === 'video' && offersVideo;
  const branches = doctor.facilities;
  const branchId = moving
    ? moving.facilityId
    : video
      ? undefined
      : (branches.find((b) => b.id === search.branch)?.id ?? branches[0]?.id);
  const branchName = branches.find((b) => b.id === branchId)?.name ?? null;

  const today = istDateOf(now);
  const windowDays = filters.data?.windowDays ?? 30;
  const lastDay = addDays(today, windowDays);
  const [stripStart, setStripStart] = useState(() =>
    search.slot ? stripStartFor(istDateOf(search.slot), today) : today,
  );
  const start = daysBetween(today, stripStart) < 0 ? today : stripStart;
  const days = stripDays(start, lastDay);
  const from = days[0] ?? today;
  const to = days.at(-1) ?? today;

  const slots = useQuery({
    ...doctorQueries.slots(doctor.id, from, to, video, branchId),
    enabled: !search.reschedule || Boolean(moving),
  });
  const counts = freeSeatsByDay(slots.data);
  const [pickedDay, setPickedDay] = useState<string | null>(() => (search.slot ? istDateOf(search.slot) : null));
  const selectedDay = chooseDay(pickedDay, days, counts);
  const daySlots = slots.data?.find((day) => day.date === selectedDay)?.slots ?? [];
  const [pickedSlot, setPickedSlot] = useState<string | null>(() => search.slot ?? null);
  const selectedSlot = daySlots.find((slot) => slot.startsAt === pickedSlot && slot.seatsLeft > 0) ?? null;
  const weekEmpty = slots.isSuccess && days.every((day) => (counts.get(day) ?? 0) === 0);

  const [bookingOpen, setBookingOpen] = useState(false);
  const [waitlistOpen, setWaitlistOpen] = useState(false);
  const [confirmMove, setConfirmMove] = useState(false);
  const [moveBusy, setMoveBusy] = useState(false);

  const refreshSlots = () => {
    setPickedSlot(null);
    void queryClient.invalidateQueries({ queryKey: ['public', 'doctors', 'slots', doctor.id] });
  };

  const moveAppointment = async () => {
    if (!moving || !selectedSlot) {
      return;
    }
    setMoveBusy(true);
    try {
      const moved = await appointmentApi.reschedule(moving.id, selectedSlot.startsAt, null);
      if (patientId) {
        void queryClient.invalidateQueries({ queryKey: appointmentQueries.all(patientId) });
      }
      toast.success(t('booking.reschedule.done'));
      setConfirmMove(false);
      void navigate({ to: '/appointments/$appointmentId', params: { appointmentId: moved.id }, replace: true });
    } catch (error) {
      setConfirmMove(false);
      toast.failure(error, t);
      refreshSlots();
    } finally {
      setMoveBusy(false);
    }
  };

  /** Booking needs a signed-in patient: sign in first and come back here with the chosen time still selected. */
  const requireProfile = (open: () => void, slot?: string) => {
    if (active) {
      open();
    } else if (signedIn) {
      setNoRecords(true);
    } else {
      const back = router.buildLocation({
        to: '/doctors/$doctorId',
        params: { doctorId: doctor.id },
        search: { ...search, slot },
      }).href;
      void navigate({ to: '/sign-in', search: { redirect: safeRedirect(back) } });
    }
  };

  const specialty = [doctor.specialtyName, doctor.departmentName]
    .filter((part, index, all) => part && all.indexOf(part) === index)
    .join(' · ');
  const title = moving ? t('booking.reschedule.title') : doctor.name;

  return (
    <PortalPage
      title={title}
      documentTitle={title}
      subtitle={
        moving ? t('booking.reschedule.current', { when: f.dateTime(moving.startsAt) }) : specialty || undefined
      }
      back={
        moving ? (
          <BackLink to="/appointments/$appointmentId" params={{ appointmentId: moving.id }}>
            {t('nav.appointments')}
          </BackLink>
        ) : (
          <BackLink to="/doctors">{t('doctors.title')}</BackLink>
        )
      }
    >
      {search.reschedule && reschedule.isError && (
        <ErrorBlock error={reschedule.error} onRetry={() => void reschedule.refetch()} />
      )}
      {moving && !moving.canReschedule && <Notice intent="warning">{t('booking.reschedule.notAllowed')}</Notice>}

      <section className={styles.card} aria-label={doctor.name}>
        <div className={styles.identity}>
          <Avatar name={doctor.name} size={72} color="colorful" aria-hidden />
          <div className={styles.identityText}>
            {moving && <strong>{doctor.name}</strong>}
            {doctor.qualification && <span className={styles.line}>{doctor.qualification}</span>}
            {!moving && specialty && <span className={styles.line}>{specialty}</span>}
            {doctor.languages.length > 0 && (
              <span className={styles.meta}>
                <LocalLanguageRegular aria-hidden />
                {t('doctors.card.languages', { languages: doctor.languages.join(', ') })}
              </span>
            )}
            {branches.length > 0 && (
              <span className={styles.meta}>
                <BuildingRegular aria-hidden />
                {t('doctors.card.branches', { branches: branches.map((b) => b.name).join(', ') })}
              </span>
            )}
          </div>
        </div>
        {!moving && (
          <div className={styles.fees} aria-label={t('doctors.profile.fees')} role="group">
            <div className={styles.fee}>
              <span className={styles.feeLabel}>{t('doctors.profile.feeNew')}</span>
              <span className={styles.feeValue}>{f.inr(doctor.feeNew, { whole: true })}</span>
            </div>
            <div className={styles.fee}>
              <span className={styles.feeLabel}>
                {t('doctors.profile.feeFollowUp', { days: doctorProfile.followUpValidityDays })}
              </span>
              <span className={styles.feeValue}>{f.inr(doctor.feeFollowUp, { whole: true })}</span>
            </div>
          </div>
        )}
      </section>

      <section className={styles.card} aria-labelledby="choose-time">
        <h2 id="choose-time" className={styles.sectionTitle}>
          {t('doctors.profile.book')}
        </h2>
        {!moving && offersVideo && (
          <ChoiceCards
            label={t('booking.mode.label')}
            value={video ? 'video' : 'clinic'}
            twoUp
            onChange={(value) => {
              setPickedSlot(null);
              void navigate({
                search: (prev) => ({ ...prev, mode: value === 'video' ? 'video' : undefined }),
                replace: true,
              });
            }}
            options={[
              { value: 'clinic', title: t('booking.mode.inPerson'), icon: <BuildingRegular /> },
              { value: 'video', title: t('booking.mode.video'), icon: <VideoRegular /> },
            ]}
          />
        )}
        {!moving && !video && branches.length > 1 && (
          <Field label={t('booking.branch.label')}>
            <Select
              size="large"
              value={branchId ?? ''}
              onChange={(_, data) => {
                setPickedSlot(null);
                void navigate({ search: (prev) => ({ ...prev, branch: data.value || undefined }), replace: true });
              }}
            >
              {branches.map((branch) => (
                <option key={branch.id} value={branch.id}>
                  {branch.city ? `${branch.name}, ${branch.city}` : branch.name}
                </option>
              ))}
            </Select>
          </Field>
        )}

        <DateStrip
          days={days}
          counts={counts}
          selected={selectedDay}
          loading={slots.isPending}
          now={now}
          onSelect={(day) => {
            setPickedDay(day);
            setPickedSlot(null);
          }}
          onEarlier={
            daysBetween(today, start) > 0
              ? () => {
                  setStripStart(addDays(start, -STRIP_DAYS));
                  setPickedDay(null);
                  setPickedSlot(null);
                }
              : undefined
          }
          onLater={
            daysBetween(addDays(start, STRIP_DAYS), lastDay) >= 0
              ? () => {
                  setStripStart(addDays(start, STRIP_DAYS));
                  setPickedDay(null);
                  setPickedSlot(null);
                }
              : undefined
          }
        />

        {slots.isPending ? (
          <LoadingCards count={2} />
        ) : slots.isError ? (
          <ErrorBlock error={slots.error} onRetry={() => void slots.refetch()} />
        ) : weekEmpty ? (
          <p className={styles.empty} role="status">
            {t('booking.week.none')}
          </p>
        ) : daySlots.length === 0 ? (
          <p className={styles.empty}>{t('booking.time.none')}</p>
        ) : (
          <SlotPicker
            slots={daySlots}
            selected={selectedSlot?.startsAt ?? null}
            onSelect={(slot) => setPickedSlot(slot.startsAt)}
          />
        )}

        {filters.data && (
          <p className={styles.rules}>
            <InfoRegular aria-hidden />
            {t('booking.rules', { minutes: filters.data.cutoffMinutes, days: filters.data.windowDays })}
          </p>
        )}
        {!moving && (
          <Button
            className={styles.waitlist}
            appearance="secondary"
            icon={<CalendarClockRegular />}
            onClick={() => requireProfile(() => setWaitlistOpen(true))}
          >
            {t('waitlist.join')}
          </Button>
        )}
      </section>

      {(doctorProfile.bio || doctorProfile.weeklySessions.length > 0) && (
        <Accordion collapsible multiple>
          {doctorProfile.bio && (
            <AccordionItem value="about">
              <AccordionHeader size="large">{t('doctors.profile.about')}</AccordionHeader>
              <AccordionPanel>
                <p className={styles.bio}>{doctorProfile.bio}</p>
              </AccordionPanel>
            </AccordionItem>
          )}
          {doctorProfile.weeklySessions.length > 0 && (
            <AccordionItem value="timings">
              <AccordionHeader size="large">{t('doctors.profile.timings')}</AccordionHeader>
              <AccordionPanel>
                <Timings sessions={doctorProfile.weeklySessions} branches={branches} />
              </AccordionPanel>
            </AccordionItem>
          )}
        </Accordion>
      )}

      {selectedSlot && (
        <div
          className={styles.bar}
          role="region"
          aria-label={t('booking.selected', { when: describeWhen(selectedSlot.startsAt, f, t, now) })}
        >
          <span className={styles.barText}>
            {t('booking.selected', { when: describeWhen(selectedSlot.startsAt, f, t, now) })}
          </span>
          {moving ? (
            <Button
              appearance="primary"
              size="large"
              disabled={!moving.canReschedule}
              onClick={() => setConfirmMove(true)}
            >
              {t('booking.reschedule.confirm')}
            </Button>
          ) : (
            <Button
              appearance="primary"
              size="large"
              onClick={() => requireProfile(() => setBookingOpen(true), selectedSlot.startsAt)}
            >
              {active || signedIn ? t('common.continue') : t('booking.signInToBook')}
            </Button>
          )}
        </div>
      )}

      {noRecords && (
        <Notice intent="warning" title={t('shell.noProfiles.title')}>
          {t('shell.noProfiles.body')}
        </Notice>
      )}
      {!moving && active && (
        <BookingSheet
          open={bookingOpen}
          onClose={() => setBookingOpen(false)}
          doctor={doctor}
          slot={selectedSlot}
          video={video}
          branchName={branchName}
          onSlotsChanged={refreshSlots}
        />
      )}
      {!moving && active && (
        <WaitlistSheet open={waitlistOpen} onClose={() => setWaitlistOpen(false)} doctor={doctor} video={video} />
      )}
      {moving && selectedSlot && (
        <ConfirmDialog
          open={confirmMove}
          title={t('booking.reschedule.confirmTitle')}
          confirmLabel={t('booking.reschedule.confirm')}
          busy={moveBusy}
          onConfirm={() => void moveAppointment()}
          onDismiss={() => setConfirmMove(false)}
        >
          {t('booking.reschedule.confirmBody', {
            from: f.dateTime(moving.startsAt),
            to: f.dateTime(selectedSlot.startsAt),
          })}
        </ConfirmDialog>
      )}
    </PortalPage>
  );
}

/** Weekly OPD timings, one line per session, in the patient's language. */
function Timings({ sessions, branches }: { sessions: PublicSession[]; branches: { id: string; name: string }[] }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  return (
    <ul className={styles.timings}>
      {sessions.map((session, index) => (
        <li key={`${session.weekday}-${session.startTime}-${index}`} className={styles.timing}>
          <strong>{f.weekday(WEEKDAY_DATES[session.weekday], 'long')}</strong>
          <span>
            {t('doctors.profile.timing', { from: f.clock(session.startTime), to: f.clock(session.endTime) })}
            {session.mode !== 'InPerson' && ` · ${t('booking.mode.video')}`}
            {branches.length > 1 && ` · ${branches.find((b) => b.id === session.facilityId)?.name ?? ''}`}
          </span>
        </li>
      ))}
    </ul>
  );
}
