import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CalendarAddRegular, DocumentArrowLeftRegular, SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateOnly, formatRelative, formatTime, todayIso, useHotkey } from '@hc360/core';
import { LEAVE_KEEPING_DRAFT, LoadingState, Page, PageHeader, Section, useLeaveGuard, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import { otApi, otKeys, type SurgeryCreated } from '@/modules/ot/api';
import { anaesthesiaShort, lateralityLabel, priorityLabel } from '@/modules/ot/labels';
import { slotConflicts } from '@/modules/ot/logic';
import { ConflictAlert, SideBadge } from '@/modules/ot/OtBadges';
import { fromIstInput, istDate, istTime } from '@/modules/ot/shared/datetime';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError, NoAccess } from '@/modules/ot/shared/feedback';
import { PlanFields, SlotFields } from '@/modules/ot/SurgeryFormFields';
import {
  emptySurgeryForm,
  slotOf,
  surgeryFormSchema,
  toCreateRequest,
  toRequestOnly,
  type SurgeryFormValues,
} from '@/modules/ot/surgeryForm';
import { patientQueries } from '@/modules/patients/api';
import { PatientPicker } from '@/modules/patients/PatientPicker';

interface BookSearch {
  theatreId?: string;
  /** IST wall-clock "yyyy-MM-ddTHH:mm" chosen on the calendar. */
  start?: string;
  patientId?: string;
}

export const Route = createFileRoute('/_app/ot/book')({
  validateSearch: (s: Record<string, unknown>): BookSearch => ({
    theatreId: typeof s.theatreId === 'string' && s.theatreId ? s.theatreId : undefined,
    start: typeof s.start === 'string' && fromIstInput(s.start) ? s.start : undefined,
    patientId: typeof s.patientId === 'string' && s.patientId ? s.patientId : undefined,
  }),
  component: BookSurgeryPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 320px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  aside: { position: 'sticky', top: '16px', display: 'flex', flexDirection: 'column', gap: '12px' },
  summary: { display: 'flex', flexDirection: 'column', gap: '8px' },
  label: { color: tokens.colorNeutralForeground3, display: 'block' },
  buttons: { display: 'flex', flexDirection: 'column', gap: '8px' },
});

function BookSurgeryPage() {
  const search = Route.useSearch();
  const canBook = usePermission('ot.bookings.manage');
  const presetPatient = useQuery({
    ...patientQueries.detail(search.patientId ?? ''),
    enabled: Boolean(search.patientId),
  });

  if (!canBook) {
    return (
      <Page>
        <NoAccess what="surgery booking" />
      </Page>
    );
  }
  if (search.patientId && presetPatient.isLoading) {
    return <LoadingState label="Loading the patient…" />;
  }

  const startIso = search.start ? fromIstInput(search.start) : null;
  const defaults: SurgeryFormValues = {
    ...emptySurgeryForm,
    patient: presetPatient.data
      ? { id: presetPatient.data.id, uhid: presetPatient.data.uhid, fullName: presetPatient.data.fullName }
      : null,
    theatreId: search.theatreId ?? null,
    date: startIso ? istDate(startIso) : todayIso(),
    time: startIso ? istTime(startIso) : null,
  };
  return <BookingForm defaults={defaults} />;
}

function BookingForm({ defaults }: { defaults: SurgeryFormValues }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<SurgeryFormValues>({
    resolver: zodResolver(surgeryFormSchema('create')),
    defaultValues: defaults,
    mode: 'onTouched',
  });
  const draft = useFormDraft(form, 'ot-book-surgery');
  const [scheduleNow, patient, procedureName, laterality, priority, anaesthesiaType, date, time, durationMinutes] =
    useWatch({
      control: form.control,
      name: [
        'scheduleNow',
        'patient',
        'procedureName',
        'laterality',
        'priority',
        'anaesthesiaType',
        'date',
        'time',
        'durationMinutes',
      ],
    });
  const [requestOnly, setRequestOnly] = useState(false);
  const slot = slotOf({ date, time, durationMinutes });

  const create = useMutation({
    mutationFn: ({ values, asRequest }: { values: SurgeryFormValues; asRequest: boolean }) =>
      otApi.createBooking(asRequest ? toRequestOnly(values) : toCreateRequest(values)),
    onSuccess: (created: SurgeryCreated) => {
      draft.clear();
      notify.success(
        created.status === 'Scheduled'
          ? `Surgery booked · ${created.bookingNumber}`
          : `Surgery requested · ${created.bookingNumber}`,
        created.status === 'Scheduled'
          ? 'The slot is protected against double booking.'
          : 'The OT coordinator will give it a theatre and time.',
      );
      void queryClient.invalidateQueries({ queryKey: otKeys.all });
      void navigate({ to: '/ot/bookings/$id', params: { id: created.id } });
    },
    onError: (error) => {
      setRequestOnly(false);
      applyFormErrors(error, form.setError);
    },
  });

  const submit = form.handleSubmit((values) => create.mutate({ values, asRequest: false }));
  const saveAsRequest = () => {
    setRequestOnly(true);
    void form.handleSubmit((values) => create.mutate({ values, asRequest: true }))();
  };
  useHotkey('mod+s', () => void submit(), { allowInInputs: true, enabled: !create.isPending });

  const conflicts = slotConflicts(create.error);

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: form.formState.isDirty, busy: create.isPending, leaveQuestion: LEAVE_KEEPING_DRAFT });

  return (
    <Page maxWidth={1400}>
      <PageHeader
        title="Book surgery"
        subtitle="Patient, procedure, team and theatre time. Double booking is checked before saving. Ctrl+S saves."
        icon={<CalendarAddRegular />}
        breadcrumbs={[{ label: 'OT calendar', onClick: () => void navigate({ to: '/ot' }) }, { label: 'Book surgery' }]}
      />

      {draft.saved && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>
            An unsaved booking from {formatRelative(new Date(draft.saved.savedAt).toISOString())} was found on this
            computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={draft.restore}>Restore it</Button>
            <Button appearance="subtle" onClick={draft.discard}>
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <form onSubmit={(event) => void submit(event)} noValidate>
        <div className={styles.layout}>
          <div className={styles.main}>
            <Section title="Patient">
              <Controller
                control={form.control}
                name="patient"
                render={({ field, fieldState }) => (
                  <PatientPicker
                    value={field.value}
                    onChange={field.onChange}
                    required
                    validationMessage={fieldState.error?.message}
                    hint="Search by UHID, mobile number or name."
                  />
                )}
              />
            </Section>

            <Section title="Procedure and team">
              <PlanFields form={form} showPreferredDate={!scheduleNow} />
            </Section>

            <Section
              title="Theatre and time"
              actions={
                <Controller
                  control={form.control}
                  name="scheduleNow"
                  render={({ field }) => (
                    <Switch
                      checked={field.value}
                      onChange={(_, d) => field.onChange(d.checked)}
                      label="Give a slot now"
                    />
                  )}
                />
              }
              description={
                scheduleNow
                  ? 'The slot is held for this patient, surgeon and anaesthetist.'
                  : 'Saved as a request. The OT coordinator gives the slot later.'
              }
            >
              {scheduleNow ? (
                <SlotFields form={form} />
              ) : (
                <Text>Choose a preferred date above if the surgeon has one.</Text>
              )}
            </Section>
          </div>

          <aside className={styles.aside}>
            <Section title="Summary">
              <div className={styles.summary}>
                <div>
                  <Caption1 className={styles.label}>Patient</Caption1>
                  <Text weight="semibold">{patient ? `${patient.fullName} (${patient.uhid})` : 'Not chosen'}</Text>
                </div>
                <div>
                  <Caption1 className={styles.label}>Procedure</Caption1>
                  <Text>{procedureName || '—'}</Text>
                </div>
                <div>
                  <Caption1 className={styles.label}>Side</Caption1>
                  {laterality && laterality !== 'NotApplicable' ? (
                    <SideBadge laterality={laterality} />
                  ) : (
                    <Text>{laterality ? lateralityLabel[laterality] : 'Not chosen'}</Text>
                  )}
                </div>
                <div>
                  <Caption1 className={styles.label}>Anaesthesia · priority</Caption1>
                  <Text>
                    {anaesthesiaShort[anaesthesiaType]} · {priorityLabel[priority]}
                  </Text>
                </div>
                <div>
                  <Caption1 className={styles.label}>When</Caption1>
                  <Text>
                    {scheduleNow
                      ? slot
                        ? `${formatDateOnly(date)}, ${formatTime(slot.start)}–${formatTime(slot.end)}`
                        : 'Choose the time'
                      : 'Request (no slot yet)'}
                  </Text>
                </div>
              </div>
            </Section>
            {conflicts ? <ConflictAlert conflicts={conflicts} /> : <ActionError error={create.error} />}
            <div className={styles.buttons}>
              <Button
                type="submit"
                appearance="primary"
                size="large"
                icon={create.isPending && !requestOnly ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
                disabled={create.isPending}
              >
                {scheduleNow ? 'Book surgery' : 'Save request'}
              </Button>
              {conflicts && (
                <Button
                  icon={create.isPending && requestOnly ? <Spinner size="tiny" aria-hidden /> : undefined}
                  disabled={create.isPending}
                  onClick={saveAsRequest}
                >
                  Save as a request without a slot
                </Button>
              )}
              <Button appearance="subtle" onClick={() => void navigate({ to: '/ot' })}>
                Cancel
              </Button>
            </div>
          </aside>
        </div>
      </form>
    </Page>
  );
}
