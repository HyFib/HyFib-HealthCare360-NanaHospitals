import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  mergeClasses,
  Spinner,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  BuildingGovernmentRegular,
  CalendarAddRegular,
  CheckmarkCircleRegular,
  DocumentCheckmarkRegular,
  EditRegular,
  LockClosedRegular,
  MegaphoneRegular,
  PrintRegular,
  ShieldErrorRegular,
  TimerRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { ConfirmDialog, EmptyState, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactElement, type ReactNode } from 'react';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { MainAside, Stack } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { AffectedPeople } from '@/modules/privacy/AffectedPeople';
import { privacyApi, privacyQueries, type BreachDetail, type NotifyAffectedResult } from '@/modules/privacy/api';
import { BreachStatusBadge } from '@/modules/privacy/badges';
import { BreachStepDialog, type BreachStepKind } from '@/modules/privacy/BreachDialogs';
import {
  breachDataCategoryLabel,
  breachNatureLabel,
  NOTICE_MAX_LENGTH,
  PRIVACY_PERMISSIONS as P,
} from '@/modules/privacy/labels';
import { printBreachReport } from '@/modules/privacy/print';
import {
  breachClock,
  breachSteps,
  listText,
  nextBreachStep,
  type BreachClock,
  type StepState,
} from '@/modules/privacy/rules';
import { useBreachCommand } from '@/modules/privacy/useBreachCommand';

export const Route = createFileRoute('/_app/privacy/breaches/$breachId/')({
  validateSearch: (s: Record<string, unknown>): { page?: number } => ({
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={P.breachesManage} what="the breach register">
      <BreachPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  banner: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '16px 18px',
    borderRadius: tokens.borderRadiusLarge,
    borderLeft: '6px solid',
    boxShadow: tokens.shadow4,
  },
  running: { backgroundColor: tokens.colorBrandBackground2, borderLeftColor: tokens.colorBrandStroke1 },
  urgent: {
    backgroundColor: tokens.colorPaletteYellowBackground1,
    borderLeftColor: tokens.colorPaletteMarigoldBorderActive,
  },
  overdue: { backgroundColor: tokens.colorPaletteRedBackground1, borderLeftColor: tokens.colorPaletteRedBorderActive },
  sent: { backgroundColor: tokens.colorPaletteGreenBackground1, borderLeftColor: tokens.colorPaletteGreenBorderActive },
  closed: { backgroundColor: tokens.colorNeutralBackground3, borderLeftColor: tokens.colorNeutralStroke1 },
  clockRow: { display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap' },
  clockIcon: { fontSize: '28px', display: 'flex' },
  clockText: {
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
  },
  overdueText: { color: tokens.colorPaletteRedForeground1 },
  steps: { display: 'flex', gap: '8px', flexWrap: 'wrap', marginTop: '4px' },
  text: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
  missing: { color: tokens.colorPaletteMarigoldForeground1, fontStyle: 'italic' },
  muted: { color: tokens.colorNeutralForeground3 },
  noticeBox: {
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
  },
  stack: { display: 'flex', flexDirection: 'column', gap: '10px' },
  subtitle: { display: 'inline-flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
});

function BreachPage() {
  const { breachId } = Route.useParams();
  const navigate = useNavigate();
  const breach = useQuery(privacyQueries.breach(breachId));
  const crumbs = [{ label: 'Data breaches', onClick: () => void navigate({ to: '/privacy/breaches' }) }];

  if (!breach.data) {
    return (
      <Page maxWidth={900}>
        <PageHeader title="Data breach" icon={<ShieldErrorRegular />} breadcrumbs={crumbs} />
        {!breach.error ? (
          <LoadingState />
        ) : isApiError(breach.error) && breach.error.status === 404 ? (
          <Section>
            <EmptyState
              title="This breach was not found"
              description="The link may be out of date."
              action={<Button onClick={() => void navigate({ to: '/privacy/breaches' })}>Back to the register</Button>}
            />
          </Section>
        ) : (
          <ErrorState error={breach.error} onRetry={() => void breach.refetch()} />
        )}
      </Page>
    );
  }
  return <BreachView breach={breach.data} fetchedAt={breach.dataUpdatedAt} crumbs={crumbs} />;
}

/** A free-text answer, or a clear "not filled in yet" (in words, not colour alone). */
function Answer({ value }: { value: string | null }) {
  const styles = useStyles();
  return value ? (
    <span className={styles.text}>{value}</span>
  ) : (
    <span className={styles.missing}>Not filled in yet</span>
  );
}

function clockIcon(clock: BreachClock): ReactNode {
  switch (clock.state) {
    case 'overdue':
      return <WarningRegular />;
    case 'sent':
      return <CheckmarkCircleRegular />;
    case 'closed':
      return <LockClosedRegular />;
    default:
      return <TimerRegular />;
  }
}

function BreachView({
  breach: b,
  fetchedAt,
  crumbs,
}: {
  breach: BreachDetail;
  /** When the details were loaded (every minute); the clock is worked out against it. */
  fetchedAt: number;
  crumbs: { label: string; onClick?: () => void }[];
}) {
  const styles = useStyles();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const { page = 1 } = Route.useSearch();
  const command = useBreachCommand();
  const [step, setStep] = useState<BreachStepKind | null>(null);
  const [closing, setClosing] = useState(false);
  const [confirmNotify, setConfirmNotify] = useState(false);
  const [notified, setNotified] = useState<NotifyAffectedResult | null>(null);
  const [printing, setPrinting] = useState(false);
  const steps = breachSteps(b);
  const next = nextBreachStep(b.status);
  const clock = breachClock(b, fetchedAt);

  const print = async () => {
    setPrinting(true);
    try {
      await printBreachReport(await privacyApi.breachReport(b.id));
    } catch (error) {
      notify.error(error, 'Could not print the report');
    } finally {
      setPrinting(false);
    }
  };

  const close = async (note: string) => {
    const result = await command.run(() => privacyApi.closeBreach(b.id, note || null), {
      success: 'Breach closed',
      failure: 'Could not close the breach',
    });
    if (result.ok) {
      setClosing(false);
    }
  };

  const sendNotices = async () => {
    const result = await command.run(() => privacyApi.notify(b.id), {
      success: 'Notices queued',
      failure: 'Could not send the notices',
    });
    setConfirmNotify(false);
    if (result.ok) {
      setNotified(result.value);
    }
  };

  const stepButton = (
    key: 'boardInformed' | 'extension' | 'reportSubmitted' | 'close',
    label: string,
    icon: ReactElement,
  ) => {
    const state: StepState = steps[key];
    if (!state.visible) {
      return null;
    }
    const button = (
      <Button
        key={key}
        appearance={next === key ? 'primary' : 'secondary'}
        icon={icon}
        disabledFocusable={Boolean(state.blocker)}
        disabled={command.busy}
        onClick={() => {
          if (state.blocker) {
            return;
          }
          if (key === 'close') {
            setClosing(true);
          } else {
            setStep(key);
          }
        }}
      >
        {label}
      </Button>
    );
    return state.blocker ? (
      <Tooltip key={key} content={state.blocker} relationship="description">
        {button}
      </Tooltip>
    ) : (
      button
    );
  };

  const notifyButton = (
    <Button
      icon={<MegaphoneRegular />}
      disabledFocusable={Boolean(steps.notify.blocker)}
      disabled={command.busy}
      onClick={() => !steps.notify.blocker && setConfirmNotify(true)}
    >
      Send notices
    </Button>
  );

  const occurred = b.occurredFrom
    ? b.occurredTo && b.occurredTo !== b.occurredFrom
      ? `${formatDateTime(b.occurredFrom)} to ${formatDateTime(b.occurredTo)}`
      : `From ${formatDateTime(b.occurredFrom)}`
    : null;

  return (
    <Page>
      <PageHeader
        title={b.title}
        subtitle={
          <span className={styles.subtitle}>
            <BreachStatusBadge status={b.status} reportOverdue={b.reportOverdue} />
            <span>
              {b.breachNumber} · the hospital became aware {formatDateTime(b.awareAt)}
            </span>
          </span>
        }
        icon={<ShieldErrorRegular />}
        breadcrumbs={[...crumbs, { label: b.breachNumber }]}
        actions={
          <>
            {steps.edit.visible && (
              <Button
                icon={<EditRegular />}
                onClick={() => void navigate({ to: '/privacy/breaches/$breachId/edit', params: { breachId: b.id } })}
              >
                Edit
              </Button>
            )}
            <Button
              icon={printing ? <Spinner size="tiny" /> : <PrintRegular />}
              disabled={printing}
              onClick={() => void print()}
            >
              Print report for the Board
            </Button>
          </>
        }
      />

      <section
        aria-label="The 72-hour clock for the Board's report"
        className={mergeClasses(styles.banner, styles[clock.state])}
      >
        <div className={styles.clockRow}>
          <span className={mergeClasses(styles.clockIcon, clock.state === 'overdue' && styles.overdueText)} aria-hidden>
            {clockIcon(clock)}
          </span>
          <span className={mergeClasses(styles.clockText, clock.state === 'overdue' && styles.overdueText)}>
            {clock.text}
          </span>
        </div>
        {!b.reportSubmittedAt && b.status !== 'Closed' && (
          <Text>
            {b.extendedUntil
              ? `The Board extended the deadline to ${formatDateTime(b.extendedUntil)}.`
              : `Due by ${formatDateTime(b.reportDueAt)}: 72 hours after the hospital became aware.`}
          </Text>
        )}
        <Text>
          {b.boardInformedAt
            ? `The Board was told on ${formatDateTime(b.boardInformedAt)}${b.boardReference ? ` (reference ${b.boardReference})` : ''}.`
            : b.status === 'Closed'
              ? 'The Board was not told.'
              : 'The Board has not been told yet. Tell it without delay, with what is known so far.'}
        </Text>
        {b.status !== 'Closed' && (
          <div className={styles.steps}>
            {stepButton('boardInformed', 'Board told', <BuildingGovernmentRegular />)}
            {stepButton('reportSubmitted', 'Report sent to the Board', <DocumentCheckmarkRegular />)}
            {stepButton('extension', 'Extension granted', <CalendarAddRegular />)}
            {stepButton('close', 'Close', <LockClosedRegular />)}
          </div>
        )}
        {steps.reportSubmitted.visible && b.missingForReport.length > 0 && (
          <Caption1>Still needed for the report: {listText(b.missingForReport)}.</Caption1>
        )}
      </section>

      <MainAside>
        <Stack>
          <Section title="What happened">
            <DetailGrid minColumnWidth={200}>
              <Detail label="Description" wide>
                <Answer value={b.description} />
              </Detail>
              <Detail label="The hospital became aware">{formatDateTime(b.awareAt)}</Detail>
              <Detail label="When it happened">{occurred ?? 'Not known yet'}</Detail>
              <Detail label="What happened to the data" wide>
                {b.nature.length > 0 ? b.nature.map((n) => breachNatureLabel[n]).join('; ') : 'Not recorded yet'}
              </Detail>
              <Detail label="Where" wide>
                {b.location}
              </Detail>
              <Detail label="Personal data affected" wide>
                {b.dataCategories.length > 0
                  ? b.dataCategories.map((c) => breachDataCategoryLabel[c]).join('; ')
                  : 'Not recorded yet'}
              </Detail>
              <Detail label="People affected, roughly">
                {b.estimatedAffected !== null ? b.estimatedAffected.toLocaleString('en-IN') : 'Not known yet'}
              </Detail>
            </DetailGrid>
          </Section>

          <Section title="Impact and response">
            <DetailGrid minColumnWidth={260}>
              <Detail label="Likely consequences" wide>
                <Answer value={b.consequences} />
              </Detail>
              <Detail label="What the hospital is doing to reduce the risk" wide>
                <Answer value={b.mitigation} />
              </Detail>
              <Detail label="What people can do to protect themselves" wide>
                <Answer value={b.principalSteps} />
              </Detail>
              <Detail label="What led to it" wide>
                <Answer value={b.cause} />
              </Detail>
              <Detail label="Findings about who caused it" wide>
                {b.findings ? (
                  <span className={styles.text}>{b.findings}</span>
                ) : (
                  <span className={styles.muted}>None recorded</span>
                )}
              </Detail>
              <Detail label="What stops it happening again" wide>
                <Answer value={b.remediation} />
              </Detail>
            </DetailGrid>
          </Section>

          <Section
            title="Notice to the people affected"
            description="By SMS, with a link to the full notice in the patient portal; by e-mail to those with an address."
            actions={
              !steps.notify.visible ? null : steps.notify.blocker ? (
                <Tooltip content={steps.notify.blocker} relationship="description">
                  {notifyButton}
                </Tooltip>
              ) : (
                notifyButton
              )
            }
          >
            <div className={styles.stack}>
              {b.principalNotice ? (
                <>
                  <div className={styles.noticeBox}>{b.principalNotice}</div>
                  <Caption1 className={styles.muted}>
                    {b.principalNotice.length} of {NOTICE_MAX_LENGTH} characters
                  </Caption1>
                </>
              ) : (
                <span className={steps.notify.visible ? styles.missing : styles.muted}>
                  {steps.notify.visible
                    ? 'The short notice for SMS is not written yet.'
                    : 'No short notice was written.'}
                </span>
              )}
              {steps.notify.visible && b.missingForNotice.length > 0 && (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Before notices can be sent</MessageBarTitle>
                    Fill in {listText(b.missingForNotice)}.
                  </MessageBarBody>
                </MessageBar>
              )}
              {notified && (
                <MessageBar intent={notified.withoutContact > 0 ? 'warning' : 'success'} layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>
                      Notices queued for {notified.queued.toLocaleString('en-IN')}{' '}
                      {notified.queued === 1 ? 'person' : 'people'}
                    </MessageBarTitle>
                    {notified.withoutContact > 0 &&
                      `${notified.withoutContact.toLocaleString('en-IN')} have no mobile number or e-mail address: reach them another way. They also see the notice when they sign in to the patient portal. `}
                    {notified.alreadyNotified > 0 &&
                      `${notified.alreadyNotified.toLocaleString('en-IN')} had already been sent it.`}
                  </MessageBarBody>
                </MessageBar>
              )}
              {b.notifiedAt && (
                <Caption1 className={styles.muted}>First notices sent {formatDateTime(b.notifiedAt)}.</Caption1>
              )}
            </div>
          </Section>

          <AffectedPeople
            breach={b}
            page={page}
            command={command}
            onPageChange={(next) =>
              void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }), replace: true })
            }
          />
        </Stack>

        <Stack>
          <Section title="The Board">
            <DetailGrid minColumnWidth={160}>
              <Detail label="Told">{b.boardInformedAt ? formatDateTime(b.boardInformedAt) : 'Not yet'}</Detail>
              <Detail label="Reference">{b.boardReference}</Detail>
              <Detail label="Report due">{formatDateTime(b.reportDueAt)}</Detail>
              <Detail label="Extension">{b.extendedUntil ? `Until ${formatDateTime(b.extendedUntil)}` : 'None'}</Detail>
              <Detail label="Report sent">
                {b.reportSubmittedAt ? formatDateTime(b.reportSubmittedAt) : 'Not yet'}
              </Detail>
              {b.closedAt && <Detail label="Closed">{formatDateTime(b.closedAt)}</Detail>}
              {b.closingNote && (
                <Detail label="Closing note" wide>
                  <span className={styles.text}>{b.closingNote}</span>
                </Detail>
              )}
            </DetailGrid>
          </Section>
          {(b.missingForReport.length > 0 || b.missingForNotice.length > 0) && b.status !== 'Closed' && (
            <Section title="Still to fill in">
              <div className={styles.stack}>
                {b.missingForReport.length > 0 && (
                  <Text>
                    <strong>For the Board’s report:</strong> {listText(b.missingForReport)}.
                  </Text>
                )}
                {b.missingForNotice.length > 0 && (
                  <Text>
                    <strong>For the notice to people:</strong> {listText(b.missingForNotice)}.
                  </Text>
                )}
                {steps.edit.visible && (
                  <div>
                    <Button
                      size="small"
                      icon={<EditRegular />}
                      onClick={() =>
                        void navigate({ to: '/privacy/breaches/$breachId/edit', params: { breachId: b.id } })
                      }
                    >
                      Fill them in
                    </Button>
                  </div>
                )}
              </div>
            </Section>
          )}
        </Stack>
      </MainAside>

      <BreachStepDialog kind={step} breach={b} command={command} onClose={() => setStep(null)} />
      <ReasonDialog
        open={closing}
        title="Close the breach record"
        description={
          steps.close.noteRequired
            ? 'The detailed report was not sent to the Board. Say why the breach can be closed, for example because no personal data was involved after all.'
            : 'The detailed report was sent to the Board. Add a closing note if it helps.'
        }
        label="Closing note"
        required={steps.close.noteRequired}
        maxLength={1000}
        confirmLabel="Close"
        busy={command.busy}
        onConfirm={(note) => void close(note)}
        onCancel={() => setClosing(false)}
      />
      <ConfirmDialog
        open={confirmNotify}
        title={`Send the notice to ${steps.notify.pending.toLocaleString('en-IN')} ${steps.notify.pending === 1 ? 'person' : 'people'}?`}
        confirmLabel="Send notices"
        busy={command.busy}
        onConfirm={() => void sendNotices()}
        onCancel={() => setConfirmNotify(false)}
      >
        <div className={styles.stack}>
          <Text>
            Everyone on the list who has not had it yet gets the notice: by SMS to a mobile number (the short notice and
            a link to the full notice), and by e-mail to an e-mail address.
          </Text>
          {b.principalNotice && <div className={styles.noticeBox}>{b.principalNotice}</div>}
          <Text>
            People with neither are counted, so you can reach them another way. Notices cannot be recalled once sent.
          </Text>
        </div>
      </ConfirmDialog>
    </Page>
  );
}
