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
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { CalendarAddRegular } from '@fluentui/react-icons';
import { formatInr, isApiError, todayIso } from '@hc360/core';
import { EmptyState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { addDaysIso } from '@/modules/emr/encounterForm';
import { checkupApi, checkupKeys, checkupQueries, type PackageDto } from '@/modules/health-checkups/api';
import { checkupProblem, type Wording } from '@/modules/health-checkups/itinerary';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

export const Route = createFileRoute('/_app/health-checkups/book')({
  component: BookCheckupPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(280px, 380px)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1000px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  packages: { display: 'flex', flexDirection: 'column', gap: '8px' },
  packageOption: {
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  items: { margin: '4px 0 0', paddingLeft: '18px' },
});

function audience(p: PackageDto): string {
  const who = p.gender === 'Male' ? 'Men' : p.gender === 'Female' ? 'Women' : 'Everyone';
  const age =
    p.minAgeYears !== null && p.maxAgeYears !== null
      ? `${p.minAgeYears}–${p.maxAgeYears} years`
      : p.minAgeYears !== null
        ? `${p.minAgeYears}+ years`
        : p.maxAgeYears !== null
          ? `up to ${p.maxAgeYears} years`
          : '';
  return [who, age].filter(Boolean).join(', ');
}

/** Books a master health check-up (MHC-02): patient, package (eligibility checked), date, and optional check-in now. */
function BookCheckupPage() {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const today = todayIso();
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [packageId, setPackageId] = useState('');
  const [date, setDate] = useState(today);
  const [notes, setNotes] = useState('');
  const [checkInNow, setCheckInNow] = useState(false);
  const [problem, setProblem] = useState<Wording | null>(null);
  const packages = useQuery(checkupQueries.packages());
  const eligibility = useQuery({
    ...checkupQueries.eligibility(packageId, patient?.id ?? ''),
    enabled: Boolean(packageId && patient),
  });
  const selected = packages.data?.find((p) => p.id === packageId);
  const latest = addDaysIso(today, 180);
  const dateValid = date >= today && date <= latest;

  const book = useMutation({
    mutationFn: async () => {
      const booking = await checkupApi.book({
        patientId: patient?.id ?? '',
        packageId,
        scheduledDate: date,
        visitId: null,
        notes: notes.trim() || null,
      });
      if (checkInNow && date === today) {
        try {
          return { booking: await checkupApi.checkIn(booking.id, null), checkInProblem: null };
        } catch (error) {
          return {
            booking,
            checkInProblem: isApiError(error)
              ? checkupProblem(error, 'check-in')
              : { title: 'Booked, but not checked in', message: 'Check the patient in from the day view.' },
          };
        }
      }
      return { booking, checkInProblem: null };
    },
    onSuccess: ({ booking, checkInProblem }) => {
      void queryClient.invalidateQueries({ queryKey: checkupKeys.all });
      if (checkInProblem) {
        notify.warning(
          `Booked ${booking.bookingNumber}, not checked in`,
          `${checkInProblem.title}. ${checkInProblem.message}`,
        );
      } else {
        notify.success(
          `Booked ${booking.bookingNumber}`,
          booking.status === 'CheckedIn'
            ? 'Checked in; the package fee is on the bill.'
            : `For ${date === today ? 'today' : date}.`,
        );
      }
      void navigate({ to: '/health-checkups/bookings/$bookingId', params: { bookingId: booking.id } });
    },
    onError: (error) => {
      if (isApiError(error)) {
        setProblem(checkupProblem(error, 'book'));
      } else {
        notify.error(error, 'Could not book the check-up');
      }
    },
  });

  const eligible = eligibility.data?.isEligible ?? true;
  const canBook = Boolean(patient && packageId && dateValid && eligible) && !book.isPending;

  return (
    <Page maxWidth={1200}>
      <PageHeader
        title="Book health check-up"
        breadcrumbs={[
          { label: 'Health check-ups', onClick: () => void navigate({ to: '/health-checkups' }) },
          { label: 'Book' },
        ]}
        subtitle="The day's stations and the tests are planned from the package; tests are covered by the package price."
      />
      <div className={styles.layout}>
        <div className={styles.stack}>
          <Section title="Patient">
            <PatientPicker
              value={patient}
              onChange={setPatient}
              required
              hint="Register new patients first under Patients › Register patient."
            />
          </Section>
          <Section title="Package">
            {packages.isLoading ? (
              <LoadingState />
            ) : (packages.data ?? []).length === 0 ? (
              <EmptyState
                title="No packages offered"
                description="Create packages under Health check-ups › Packages."
              />
            ) : (
              <RadioGroup
                value={packageId}
                onChange={(_, d) => setPackageId(d.value)}
                aria-label="Package"
                className={styles.packages}
              >
                {(packages.data ?? []).map((p) => (
                  <div key={p.id} className={styles.packageOption}>
                    <Radio
                      value={p.id}
                      label={
                        <span>
                          <Text weight="semibold">{p.name}</Text> · {formatInr(p.price, { whole: true })}
                          <Caption1 block className={styles.muted}>
                            {p.code} · {audience(p)} · {p.items.length} tests and consultations
                            {!p.serviceItemId ? ' · no billing item yet' : ''}
                          </Caption1>
                        </span>
                      }
                    />
                  </div>
                ))}
              </RadioGroup>
            )}
          </Section>
          <Section title="Date and notes">
            <div className={styles.stack}>
              <Field
                label="Check-up date"
                required
                validationState={dateValid ? 'none' : 'error'}
                validationMessage={dateValid ? undefined : 'Choose a date from today up to six months ahead.'}
              >
                <Input type="date" value={date} min={today} max={latest} onChange={(_, d) => setDate(d.value)} />
              </Field>
              {date === today && (
                <Switch
                  checked={checkInNow}
                  onChange={(_, d) => setCheckInNow(d.checked)}
                  label="The patient is here now: check in after booking (adds the package fee to the bill)"
                />
              )}
              <Field label="Notes">
                <Textarea
                  value={notes}
                  onChange={(_, d) => setNotes(d.value.slice(0, 1000))}
                  rows={2}
                  resize="vertical"
                  placeholder="e.g. fasting since 10 pm, wheelchair needed"
                />
              </Field>
            </div>
          </Section>
        </div>

        <Section title="Summary">
          <div className={styles.stack}>
            {!patient || !selected ? (
              <Text className={styles.muted}>Choose the patient and the package.</Text>
            ) : (
              <>
                <Text>
                  <Text weight="semibold">{patient.fullName}</Text> · {patient.uhid}
                </Text>
                <Text>
                  {selected.name} · <Text weight="semibold">{formatInr(selected.price)}</Text>
                </Text>
                {eligibility.isFetching && <Spinner size="tiny" label="Checking eligibility…" labelPosition="after" />}
                {eligibility.data && !eligibility.data.isEligible && (
                  <MessageBar intent="error" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>Not suitable for this patient</MessageBarTitle>
                      {eligibility.data.reason}
                    </MessageBarBody>
                  </MessageBar>
                )}
                {eligibility.data && eligibility.data.isEligible && eligibility.data.excludedItems.length > 0 && (
                  <MessageBar intent="info" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>Not included for this patient (age or sex)</MessageBarTitle>
                      <ul className={styles.items}>
                        {eligibility.data.excludedItems.map((item) => (
                          <li key={item}>{item}</li>
                        ))}
                      </ul>
                    </MessageBarBody>
                  </MessageBar>
                )}
                {!selected.serviceItemId && (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      This package has no billing item yet, so check-in will be refused until an administrator saves the
                      package.
                    </MessageBarBody>
                  </MessageBar>
                )}
              </>
            )}
            {problem && (
              <MessageBar intent="error" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>{problem.title}</MessageBarTitle>
                  {problem.message}
                </MessageBarBody>
              </MessageBar>
            )}
            <Button
              appearance="primary"
              size="large"
              icon={book.isPending ? <Spinner size="tiny" aria-hidden /> : <CalendarAddRegular />}
              disabled={!canBook}
              onClick={() => {
                setProblem(null);
                book.mutate();
              }}
            >
              {checkInNow && date === today ? 'Book and check in' : 'Book check-up'}
            </Button>
          </div>
        </Section>
      </div>
    </Page>
  );
}
