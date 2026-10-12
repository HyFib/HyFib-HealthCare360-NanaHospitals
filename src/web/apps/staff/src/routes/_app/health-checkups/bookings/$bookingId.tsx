import {
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
  ProgressBar,
  Spinner,
  Text,
  Textarea,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  CheckmarkCircleRegular,
  CheckmarkRegular,
  DismissRegular,
  DocumentArrowLeftRegular,
  OpenRegular,
  PersonAvailableRegular,
  SaveRegular,
  SkipForwardTabRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateOnly, formatDateTime, formatRelative, isApiError, todayIso } from '@hc360/core';
import { ConfirmDialog, ErrorState, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { DocumentButtons } from '@/lib/DocumentButtons';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import { PatientBannerWithFlags } from '@/modules/emr/PatientBannerWithFlags';
import { ReasonDialog } from '@/modules/emr/ui';
import {
  checkupApi,
  checkupKeys,
  checkupQueries,
  checkupReportDocument,
  type BookingDto,
  type ItineraryItemDto,
} from '@/modules/health-checkups/api';
import { CheckInCheckupDialog } from '@/modules/health-checkups/CheckInCheckupDialog';
import {
  bookingStatusLabel,
  canCancel,
  canCheckIn,
  canMarkNoShow,
  checkupProblem,
  completionBlockers,
  isStarted,
  nextStop,
  progress,
  type Wording,
} from '@/modules/health-checkups/itinerary';
import { patientQueries } from '@/modules/patients/api';

export const Route = createFileRoute('/_app/health-checkups/bookings/$bookingId')({
  component: BookingPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(320px, 440px)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  stops: { display: 'flex', flexDirection: 'column', gap: '8px', margin: 0, padding: 0, listStyle: 'none' },
  stop: {
    display: 'grid',
    gridTemplateColumns: '32px minmax(0, 1fr) auto',
    gap: '8px',
    alignItems: 'center',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  stopNext: { border: `2px solid ${tokens.colorBrandStroke1}` },
  stopClosed: { backgroundColor: tokens.colorNeutralBackground2 },
  seq: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground3, textAlign: 'center' },
  done: { color: tokens.colorPaletteGreenForeground1, display: 'flex', fontSize: '20px', justifyContent: 'center' },
  stopText: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  actions: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

const summarySchema = z.object({
  summary: z.string().trim().min(1, 'Write the consolidated summary.').max(8000),
  recommendations: z.string().max(4000),
});

type SummaryValues = z.infer<typeof summarySchema>;

function BookingPage() {
  const { bookingId } = Route.useParams();
  const booking = useQuery(checkupQueries.booking(bookingId));
  if (booking.isLoading) {
    return <LoadingState />;
  }
  if (booking.error || !booking.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={booking.error} onRetry={() => void booking.refetch()} />
      </Page>
    );
  }
  return <BookingView booking={booking.data} />;
}

/** The patient banner: allergies and alerts for clinical staff, identity only for the front desk. */
function BookingBanner({ patientId }: { patientId: string }) {
  const patient = useQuery(patientQueries.detail(patientId));
  if (patient.data) {
    const p = patient.data;
    return (
      <PatientBannerWithFlags
        patientId={patientId}
        photoFileId={p.photoFileId}
        patient={{
          id: p.id,
          uhid: p.uhid,
          fullName: p.fullName,
          gender: p.gender,
          age: p.age,
          bloodGroup: p.bloodGroup,
          isVip: p.isVip,
          isDeceased: p.isDeceased,
        }}
      />
    );
  }
  return null;
}

function SummaryEditor({ booking, onSaved }: { booking: BookingDto; onSaved: (b: BookingDto) => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const [defaults] = useState<SummaryValues>(() => ({
    summary: booking.summary ?? '',
    recommendations: booking.recommendations ?? '',
  }));
  const form = useForm<SummaryValues>({ resolver: zodResolver(summarySchema), defaultValues: defaults });
  const draft = useFormDraft(form, `hc-summary:${booking.id}`);
  const closed = !['Booked', 'CheckedIn', 'InProgress'].includes(booking.status);
  const save = useMutation({
    mutationFn: (values: SummaryValues) =>
      checkupApi.saveSummary(booking.id, values.summary.trim(), values.recommendations.trim() || null),
    onSuccess: (updated, values) => {
      form.reset(values);
      draft.discard();
      notify.success('Summary saved');
      onSaved(updated);
    },
    onError: (error) => notify.error(error, 'Could not save the summary'),
  });
  const restorable = draft.saved && JSON.stringify(draft.saved.value) !== JSON.stringify(defaults);
  return (
    <form className={styles.stack} onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate>
      {restorable && draft.saved && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>
            Unsaved text from {formatRelative(new Date(draft.saved.savedAt).toISOString())} was found on this computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={draft.restore}>Restore it</Button>
            <Button appearance="subtle" onClick={draft.discard}>
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}
      <Controller
        control={form.control}
        name="summary"
        render={({ field, fieldState }) => (
          <Field
            label="Consolidated summary"
            required
            validationMessage={fieldState.error?.message}
            validationState={fieldState.error ? 'error' : 'none'}
          >
            <Textarea
              {...field}
              rows={6}
              resize="vertical"
              maxLength={8000}
              disabled={closed}
              placeholder="Key findings across the tests and consultations"
            />
          </Field>
        )}
      />
      <Controller
        control={form.control}
        name="recommendations"
        render={({ field }) => (
          <Field label="Recommendations">
            <Textarea
              {...field}
              rows={4}
              resize="vertical"
              maxLength={4000}
              disabled={closed}
              placeholder="Lifestyle, follow-up tests, specialist referrals"
            />
          </Field>
        )}
      />
      {booking.reviewedAt && (
        <Caption1 className={styles.muted}>Last saved {formatDateTime(booking.reviewedAt)}</Caption1>
      )}
      {!closed && (
        <div>
          <Button
            type="submit"
            appearance="primary"
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
            disabled={save.isPending}
          >
            Save summary
          </Button>
        </div>
      )}
    </form>
  );
}

type Pending = { kind: 'skip'; stop: ItineraryItemDto } | { kind: 'note'; stop: ItineraryItemDto } | null;

/**
 * One booking: check-in, the station checklist, the doctor's summary, completion and the full report (summary with all
 * released results) to print or download as a PDF.
 */
function BookingView({ booking: b }: { booking: BookingDto }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canManage = usePermission('healthcheck.manage');
  const canStations = usePermission(['healthcheck.manage', 'clinical.emr.write', 'clinical.vitals.record']);
  const canSummarise = usePermission('clinical.emr.write');
  const canReadEmr = usePermission('clinical.emr.read');
  const [checkingIn, setCheckingIn] = useState(false);
  const [pending, setPending] = useState<Pending>(null);
  const [cancelling, setCancelling] = useState(false);
  const [confirmNoShow, setConfirmNoShow] = useState(false);
  const [confirmComplete, setConfirmComplete] = useState(false);
  const [problem, setProblem] = useState<Wording | null>(null);

  const publish = (updated: BookingDto) => {
    queryClient.setQueryData(checkupKeys.booking(updated.id), updated);
    void queryClient.invalidateQueries({ queryKey: ['health-checkups', 'bookings'] });
  };

  const explain = (error: unknown, action: 'cancel' | 'no-show' | 'complete') => {
    if (isApiError(error)) {
      setProblem(checkupProblem(error, action));
    } else {
      notify.error(error, 'The check-up could not be updated');
    }
  };

  const stopAction = useMutation({
    mutationFn: ({ stop, kind, text }: { stop: ItineraryItemDto; kind: 'done' | 'skip'; text: string | null }) =>
      kind === 'done' ? checkupApi.completeStop(b.id, stop.id, text) : checkupApi.skipStop(b.id, stop.id, text ?? ''),
    onSuccess: (updated, { stop, kind }) => {
      setPending(null);
      publish(updated);
      const next = nextStop(updated.itinerary);
      notify.success(
        `${stop.description}: ${kind === 'done' ? 'done' : 'skipped'}`,
        next ? `Next: ${next.departmentName}` : 'All stations are closed. The doctor can now review.',
      );
    },
    onError: (error) => {
      notify.error(error, 'Could not update the station');
      void queryClient.invalidateQueries({ queryKey: checkupKeys.booking(b.id) });
    },
  });
  const cancel = useMutation({
    mutationFn: (reason: string) => checkupApi.cancel(b.id, reason),
    onSuccess: (updated) => {
      setCancelling(false);
      setProblem(null);
      publish(updated);
      notify.success('Check-up cancelled', 'The remaining tests and the package charge were cancelled.');
    },
    onError: (error) => {
      setCancelling(false);
      explain(error, 'cancel');
    },
  });
  const noShow = useMutation({
    mutationFn: () => checkupApi.noShow(b.id),
    onSuccess: (updated) => {
      setConfirmNoShow(false);
      setProblem(null);
      publish(updated);
      notify.success('Marked as did not come');
    },
    onError: (error) => {
      setConfirmNoShow(false);
      explain(error, 'no-show');
    },
  });
  const complete = useMutation({
    mutationFn: () => checkupApi.complete(b.id),
    onSuccess: (updated) => {
      setConfirmComplete(false);
      setProblem(null);
      publish(updated);
      notify.success('Check-up completed', 'Print the full report or download its PDF for the patient.');
    },
    onError: (error) => {
      setConfirmComplete(false);
      explain(error, 'complete');
    },
  });
  const p = progress(b.itinerary);
  const next = nextStop(b.itinerary);
  const blockers = completionBlockers(b);
  const started = isStarted(b.status);
  const isToday = b.scheduledDate === todayIso();

  return (
    <Page>
      <BookingBanner patientId={b.patientId} />
      <PageHeader
        title={b.packageName}
        subtitle={`${b.bookingNumber} · ${formatDateOnly(b.scheduledDate)}${b.corporateName ? ` · ${b.corporateName}` : ''}${b.checkedInAt ? ` · checked in ${formatDateTime(b.checkedInAt)}` : ''}`}
        breadcrumbs={[
          {
            label: 'Health check-ups',
            onClick: () => void navigate({ to: '/health-checkups', search: isToday ? {} : { date: b.scheduledDate } }),
          },
          { label: b.bookingNumber },
        ]}
        actions={
          <div className={styles.actions}>
            <StatusBadge status={b.status} label={bookingStatusLabel[b.status]} />
            {canManage && canCheckIn(b.status) && (
              <Button appearance="primary" icon={<PersonAvailableRegular />} onClick={() => setCheckingIn(true)}>
                Check in
              </Button>
            )}
            {(canManage || canSummarise) && started && (
              <Tooltip
                content={blockers.length > 0 ? blockers.join(' ') : 'All stations closed and the summary written'}
                relationship="description"
              >
                <Button
                  appearance="primary"
                  icon={<CheckmarkCircleRegular />}
                  disabled={blockers.length > 0}
                  onClick={() => setConfirmComplete(true)}
                >
                  Complete check-up
                </Button>
              </Tooltip>
            )}
            <DocumentButtons
              path={checkupReportDocument(b.id)}
              printLabel={b.status === 'Completed' ? 'Print full report' : 'Print draft report'}
              printHint="The doctor's summary and recommendations with all released results"
            />
            {canReadEmr && (
              <Button
                icon={<OpenRegular />}
                onClick={() =>
                  void navigate({
                    to: '/emr/patients/$patientId',
                    params: { patientId: b.patientId },
                    search: { tab: 'orders' },
                  })
                }
              >
                Results and orders
              </Button>
            )}
            {canManage && canMarkNoShow(b.status) && (
              <Button onClick={() => setConfirmNoShow(true)}>Did not come</Button>
            )}
            {canManage && canCancel(b.status) && (
              <Button icon={<DismissRegular />} onClick={() => setCancelling(true)}>
                Cancel
              </Button>
            )}
          </div>
        }
      />

      {problem && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{problem.title}</MessageBarTitle>
            {problem.message}
          </MessageBarBody>
          <MessageBarActions
            containerAction={
              <Button
                appearance="transparent"
                icon={<DismissRegular />}
                aria-label="Dismiss"
                onClick={() => setProblem(null)}
              />
            }
          />
        </MessageBar>
      )}
      {b.status === 'Cancelled' && b.cancelReason && (
        <MessageBar intent="warning">
          <MessageBarBody>Cancelled: {b.cancelReason}</MessageBarBody>
        </MessageBar>
      )}
      {b.notes && (
        <MessageBar intent="info">
          <MessageBarBody>{b.notes}</MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.layout}>
        <Section
          title="Stations"
          description={
            started
              ? next
                ? `Next: ${next.departmentName} — ${next.description}`
                : 'All stations closed'
              : b.status === 'Booked'
                ? 'Check the patient in to start'
                : undefined
          }
        >
          <div className={styles.stack}>
            <div>
              <Caption1>
                {p.done} done · {p.skipped} not done · {p.pending} pending
              </Caption1>
              <ProgressBar
                value={p.ratio}
                thickness="large"
                aria-label={`${p.done + p.skipped} of ${p.total} stations closed`}
              />
            </div>
            <ol className={styles.stops} aria-label="Check-up stations">
              {b.itinerary
                .toSorted((x, y) => x.sequence - y.sequence)
                .map((stop) => {
                  const closed = stop.status !== 'Pending';
                  return (
                    <li
                      key={stop.id}
                      className={mergeClasses(
                        styles.stop,
                        closed ? styles.stopClosed : undefined,
                        started && next?.id === stop.id ? styles.stopNext : undefined,
                      )}
                    >
                      {stop.status === 'Done' ? (
                        <span className={styles.done} aria-label="Done">
                          <CheckmarkCircleRegular />
                        </span>
                      ) : (
                        <span className={styles.seq}>{stop.sequence}</span>
                      )}
                      <span className={styles.stopText}>
                        <Text weight="semibold">{stop.description}</Text>
                        <Caption1 className={styles.muted}>
                          {stop.departmentName}
                          {stop.completedAt ? ` · ${formatDateTime(stop.completedAt)}` : ''}
                        </Caption1>
                        {stop.notes && (
                          <Caption1>{stop.status === 'Skipped' ? `Not done: ${stop.notes}` : stop.notes}</Caption1>
                        )}
                      </span>
                      <span className={styles.actions}>
                        {stop.status === 'Skipped' && (
                          <Badge appearance="tint" color="warning">
                            Not done
                          </Badge>
                        )}
                        {!closed && started && canStations && (
                          <>
                            <Button
                              size="small"
                              appearance="primary"
                              icon={<CheckmarkRegular />}
                              disabled={stopAction.isPending}
                              onClick={() => stopAction.mutate({ stop, kind: 'done', text: null })}
                            >
                              Done
                            </Button>
                            <Button
                              size="small"
                              disabled={stopAction.isPending}
                              onClick={() => setPending({ kind: 'note', stop })}
                            >
                              Done with note
                            </Button>
                            <Tooltip content="Not done (patient refused, not applicable…)" relationship="label">
                              <Button
                                size="small"
                                appearance="subtle"
                                icon={<SkipForwardTabRegular />}
                                disabled={stopAction.isPending}
                                onClick={() => setPending({ kind: 'skip', stop })}
                              />
                            </Tooltip>
                          </>
                        )}
                      </span>
                    </li>
                  );
                })}
            </ol>
          </div>
        </Section>

        <Section
          title="Doctor's review"
          description={
            canSummarise
              ? 'Write the consolidated summary once the results are in.'
              : 'Written by the reviewing doctor.'
          }
        >
          {canSummarise ? (
            <SummaryEditor key={b.id} booking={b} onSaved={publish} />
          ) : b.summary ? (
            <div className={styles.stack}>
              <Text style={{ whiteSpace: 'pre-wrap' }}>{b.summary}</Text>
              {b.recommendations && (
                <>
                  <Caption1 className={styles.muted}>Recommendations</Caption1>
                  <Text style={{ whiteSpace: 'pre-wrap' }}>{b.recommendations}</Text>
                </>
              )}
            </div>
          ) : (
            <Text className={styles.muted}>Not written yet.</Text>
          )}
        </Section>
      </div>

      <CheckInCheckupDialog
        booking={checkingIn ? b : null}
        onClose={() => setCheckingIn(false)}
        onCheckedIn={publish}
      />
      <ReasonDialog
        open={pending !== null}
        title={
          pending?.kind === 'skip'
            ? `Mark "${pending.stop.description}" as not done?`
            : `Complete "${pending?.stop.description ?? ''}"`
        }
        description={
          pending?.kind === 'skip'
            ? 'The station is closed without being done. The reason appears on the report.'
            : 'Add a note for the doctor (for example a reading or an observation).'
        }
        label={pending?.kind === 'skip' ? 'Reason' : 'Note'}
        optional={pending?.kind === 'note'}
        maxLength={pending?.kind === 'skip' ? 300 : 500}
        confirmLabel={pending?.kind === 'skip' ? 'Not done' : 'Done'}
        busy={stopAction.isPending}
        onConfirm={(text) =>
          pending &&
          stopAction.mutate({ stop: pending.stop, kind: pending.kind === 'skip' ? 'skip' : 'done', text: text || null })
        }
        onCancel={() => setPending(null)}
      />
      <ReasonDialog
        open={cancelling}
        title="Cancel this check-up?"
        description="The remaining tests are cancelled in the departments and the package charge is removed from the bill. If the bill is already finalised, billing must cancel it or issue a credit note first."
        confirmLabel="Cancel check-up"
        destructive
        busy={cancel.isPending}
        onConfirm={(reason) => cancel.mutate(reason)}
        onCancel={() => setCancelling(false)}
      />
      <ConfirmDialog
        open={confirmNoShow}
        title="Mark as did not come?"
        confirmLabel="Did not come"
        destructive
        busy={noShow.isPending}
        onConfirm={() => noShow.mutate()}
        onCancel={() => setConfirmNoShow(false)}
      >
        The booked tests are cancelled. Book again if the patient comes on another day.
      </ConfirmDialog>
      <ConfirmDialog
        open={confirmComplete}
        title="Complete the check-up?"
        confirmLabel="Complete"
        busy={complete.isPending}
        onConfirm={() => complete.mutate()}
        onCancel={() => setConfirmComplete(false)}
      >
        The report becomes final and the patient can be handed the consolidated report.
      </ConfirmDialog>
    </Page>
  );
}
