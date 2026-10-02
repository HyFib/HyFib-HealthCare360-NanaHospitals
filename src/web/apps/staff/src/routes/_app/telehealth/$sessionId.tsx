import {
  Button,
  CounterBadge,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Tab,
  TabList,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular, DocumentTextRegular, PersonRegular, PillRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError, isFeatureDisabled, useRealtimeEvent, useSession } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useBlocker, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { PatientBannerWithFlags } from '@/modules/emr/PatientBannerWithFlags';
import { hubErrorMessage } from '@hc360/core';
import {
  telehealthApi,
  telehealthKeys,
  telehealthQueries,
  type TelehealthConfig,
  type TelehealthSessionDetail,
} from '@/modules/virtualcare/telehealth/api';
import { CallControls } from '@/modules/virtualcare/telehealth/CallControls';
import { ChatPanel } from '@/modules/virtualcare/telehealth/ChatPanel';
import { isSessionOpen } from '@/modules/virtualcare/telehealth/labels';
import { emrLinks } from '@/modules/virtualcare/telehealth/links';
import { EndSessionDialog, RecordingConsentDialog } from '@/modules/virtualcare/telehealth/SessionDialogs';
import { SessionDetails } from '@/modules/virtualcare/telehealth/SessionDetails';
import { useSessionActions } from '@/modules/virtualcare/telehealth/useSessionActions';
import { useTelehealthCall } from '@/modules/virtualcare/telehealth/useTelehealthCall';
import { VideoOffPage } from '@/modules/virtualcare/telehealth/VideoOffPage';
import { VideoStage } from '@/modules/virtualcare/telehealth/VideoStage';
import { WaitingRoomPanel } from '@/modules/virtualcare/telehealth/WaitingRoomPanel';

export const Route = createFileRoute('/_app/telehealth/$sessionId')({
  component: SessionPage,
});

const useStyles = makeStyles({
  console: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(300px, 380px)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '8px', minWidth: 0 },
  notices: { display: 'flex', flexDirection: 'column', gap: '8px' },
  panel: { paddingTop: '12px' },
  summary: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(300px, 420px)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  bannerActions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

function SessionPage() {
  const { sessionId } = Route.useParams();
  // A different session is a different call: remount so the old call is left cleanly.
  return <SessionScreen key={sessionId} sessionId={sessionId} />;
}

/** One video consultation: the doctor's console while it is open, otherwise its record (TEL-02..05). */
function SessionScreen({ sessionId }: { sessionId: string }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const canConsult = usePermission('telehealth.consult');
  const session = useQuery(telehealthQueries.session(sessionId));
  const config = useQuery({ ...telehealthQueries.config(), enabled: canConsult });

  useRealtimeEvent<{ id: string }>(facilityId ? `facility:${facilityId}` : null, 'telehealth.changed', (payload) => {
    if (payload.id === sessionId) {
      void queryClient.invalidateQueries({ queryKey: telehealthKeys.session(sessionId) });
    }
  });

  // Video consultations switched off: explained, never shown as a failure. A call already on screen stays, so the
  // doctor can finish it.
  const videoOff = isFeatureDisabled(session.error) || isFeatureDisabled(config.error);
  if (session.isLoading) {
    return <LoadingState label="Opening the consultation…" />;
  }
  if (!session.data && videoOff) {
    return <VideoOffPage />;
  }
  if ((session.error && !videoOff) || !session.data) {
    const restricted = isApiError(session.error) && session.error.code === 'patient.restricted';
    return (
      <Page maxWidth={900}>
        <PageHeader
          title="Video consultation"
          breadcrumbs={[
            { label: 'Video consultations', onClick: () => void navigate({ to: '/telehealth' }) },
            { label: 'Consultation' },
          ]}
        />
        <ErrorState
          error={session.error}
          title={restricted ? 'This patient record is restricted' : undefined}
          onRetry={restricted ? undefined : () => void session.refetch()}
        />
      </Page>
    );
  }

  const s = session.data;
  const live = canConsult && isSessionOpen(s.status);

  return (
    <Page>
      <PageHeader
        title="Video consultation"
        subtitle={`${formatDateTime(s.scheduledStartAt)} · ${s.practitionerName}`}
        breadcrumbs={[
          { label: 'Video consultations', onClick: () => void navigate({ to: '/telehealth' }) },
          { label: 'Consultation' },
        ]}
      />
      <PatientBannerWithFlags
        patientId={s.patientId}
        patient={{
          id: s.patientId,
          uhid: s.uhid,
          fullName: s.patientName,
          gender: s.gender,
          age: s.age,
          phone: s.phoneMasked,
        }}
        actions={
          <div className={styles.bannerActions}>
            <Button
              icon={<PersonRegular />}
              onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: s.patientId } })}
            >
              Patient profile
            </Button>
            <Button
              as="a"
              href={emrLinks.chart(s.patientId)}
              target="_blank"
              rel="noopener noreferrer"
              icon={<DocumentTextRegular />}
            >
              Open chart
            </Button>
            {s.visitId && (
              <Button
                as="a"
                href={emrLinks.consultation(s.patientId)}
                target="_blank"
                rel="noopener noreferrer"
                icon={<PillRegular />}
              >
                Notes and prescription
              </Button>
            )}
          </div>
        }
      />
      {videoOff && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Video consultations have been turned off</MessageBarTitle>A call already running can be
            finished, but nobody else can join.
          </MessageBarBody>
        </MessageBar>
      )}
      {!live ? (
        <SessionRecord session={s} canConsult={canConsult} />
      ) : config.data ? (
        <Console session={s} config={config.data} />
      ) : config.isLoading ? (
        <LoadingState label="Preparing the call…" />
      ) : (
        <ErrorState error={config.error} onRetry={() => void config.refetch()} />
      )}
    </Page>
  );
}

function Console({ session, config }: { session: TelehealthSessionDetail; config: TelehealthConfig }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const actions = useSessionActions();
  const [state, call] = useTelehealthCall({
    sessionId: session.id,
    hubPath: config.hubPath || '/hubs/telehealth',
    iceServers: config.iceServers,
    loadSession: () => queryClient.fetchQuery({ ...telehealthQueries.session(session.id), staleTime: 0 }),
    onSessionChanged: () => void queryClient.invalidateQueries({ queryKey: telehealthKeys.session(session.id) }),
  });
  const [tab, setTab] = useState<'waiting' | 'chat' | 'details'>('waiting');
  const [seen, setSeen] = useState(0);
  const [ending, setEnding] = useState(false);
  const [consentOpen, setConsentOpen] = useState(false);
  const [consentDismissed, setConsentDismissed] = useState(false);
  const [startProblem, setStartProblem] = useState<string | null>(null);

  // Messages count as read while the chat tab is open.
  if (tab === 'chat' && seen !== state.messages.length) {
    setSeen(state.messages.length);
  }
  const unread = tab === 'chat' ? 0 : Math.max(0, state.messages.length - seen);
  const inCall = state.phase === 'joined' && state.remote !== null;
  const status = state.sessionStatus ?? session.status;

  const blocker = useBlocker({ shouldBlockFn: () => inCall, enableBeforeUnload: () => inCall, withResolver: true });

  const refreshSession = () => void queryClient.invalidateQueries({ queryKey: telehealthKeys.session(session.id) });

  const start = useMutation({
    mutationFn: () => telehealthApi.start(session.id),
    onSuccess: (detail) => {
      setStartProblem(null);
      queryClient.setQueryData(telehealthKeys.session(session.id), detail);
    },
    onError: (error) =>
      setStartProblem(isApiError(error) ? error.userMessage : 'The visit for this consultation could not be created.'),
  });

  const admit = useMutation({
    mutationFn: (participantId?: string) => call.admit(participantId),
    onSuccess: () => {
      // Starting creates the telehealth visit for notes and e-prescriptions (safe to repeat).
      if (!session.visitId) {
        start.mutate();
      }
      refreshSession();
    },
    onError: (error) => notify.warning('Could not admit the patient', hubErrorMessage(error)),
  });

  const showConsentPrompt = state.peer === 'connected' && !session.recordingConsentAt && !consentDismissed;
  const suggestAudioOnly = state.poorStreak >= 2 && !state.audioOnly && state.cameraOn;
  const mediaProblem = state.media.message;

  return (
    <div className={styles.console}>
      <div className={styles.main}>
        <VideoStage
          state={state}
          patientName={session.patientName}
          earlyJoinMinutes={config.earlyJoinMinutes}
          admitting={admit.isPending}
          onAdmit={(participantId) => admit.mutate(participantId)}
          onRetryJoin={() => call.retry()}
          onReconnectVideo={() => call.reconnectVideo()}
        />
        <CallControls
          state={state}
          call={call}
          canEnd={status === 'InProgress'}
          onEnd={() => setEnding(true)}
          onLeave={() => void navigate({ to: '/telehealth' })}
        />

        <div className={styles.notices} aria-live="polite">
          {mediaProblem && (
            <MessageBar intent={state.media.status === 'ready' ? 'warning' : 'error'} layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>
                  {state.media.status === 'ready' ? 'Audio only' : 'Camera and microphone'}
                </MessageBarTitle>
                {mediaProblem}
              </MessageBarBody>
              {state.media.status !== 'requesting' && (
                <MessageBarActions>
                  <Button size="small" icon={<ArrowClockwiseRegular />} onClick={() => call.retryMedia()}>
                    Try again
                  </Button>
                </MessageBarActions>
              )}
            </MessageBar>
          )}
          {suggestAudioOnly && (
            <MessageBar intent="warning">
              <MessageBarBody>
                The connection is weak. Switching to audio only usually keeps the call going.
              </MessageBarBody>
              <MessageBarActions>
                <Button size="small" onClick={() => call.setAudioOnly(true)}>
                  Switch to audio only
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}
          {startProblem && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>The visit was not created</MessageBarTitle>
                {startProblem} Notes and prescriptions need the visit.
              </MessageBarBody>
              <MessageBarActions>
                <Button size="small" disabled={start.isPending} onClick={() => start.mutate()}>
                  Try again
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}
          {showConsentPrompt && (
            <MessageBar intent="info" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Recording</MessageBarTitle>
                If this consultation will be recorded, ask for the patient's consent first and note their answer.
              </MessageBarBody>
              <MessageBarActions>
                <Button size="small" onClick={() => setConsentOpen(true)}>
                  Ask for consent
                </Button>
                <Button size="small" appearance="transparent" onClick={() => setConsentDismissed(true)}>
                  Not recording
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}
        </div>
      </div>

      <Section>
        <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as typeof tab)} size="small">
          <Tab value="waiting">
            Waiting room{' '}
            {state.waiting.length > 0 && <CounterBadge count={state.waiting.length} size="small" color="danger" />}
          </Tab>
          <Tab value="chat">Chat {unread > 0 && <CounterBadge count={unread} size="small" />}</Tab>
          <Tab value="details">Details</Tab>
        </TabList>
        <div className={styles.panel}>
          {tab === 'waiting' && (
            <WaitingRoomPanel
              waiting={state.waiting}
              session={session}
              canAdmit={state.phase === 'joined'}
              admitting={admit.isPending}
              onAdmit={(participantId) => admit.mutate(participantId)}
              canResend={actions.canManage}
              resending={actions.resending}
              onResend={() => actions.resendLink(session.id)}
            />
          )}
          {tab === 'chat' && (
            <ChatPanel
              messages={state.messages}
              disabled={state.phase !== 'joined'}
              onSend={(text) => call.sendChat(text)}
            />
          )}
          {tab === 'details' && (
            <SessionDetails
              session={session}
              canConsult
              starting={start.isPending}
              onStart={() => start.mutate()}
              onAskConsent={() => setConsentOpen(true)}
              canResend={actions.canManage}
              resending={actions.resending}
              onResend={() => actions.resendLink(session.id)}
            />
          )}
        </div>
      </Section>

      <EndSessionDialog
        open={ending}
        sessionId={session.id}
        onClose={() => setEnding(false)}
        onEnded={(detail) => {
          setEnding(false);
          call.finish(detail.status);
          queryClient.setQueryData(telehealthKeys.session(session.id), detail);
          notify.success('Consultation ended', 'Write the notes and the prescription against the visit.');
        }}
      />
      <RecordingConsentDialog open={consentOpen} sessionId={session.id} onClose={() => setConsentOpen(false)} />
      {actions.dialogs}

      <Dialog
        open={blocker.status === 'blocked'}
        onOpenChange={(_, d) => !d.open && blocker.reset?.()}
        modalType="alert"
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Leave the video call?</DialogTitle>
            <DialogContent>
              You are in a call with {state.remote?.name ?? session.patientName}. Leaving disconnects you, and the
              patient waits for you to come back. To finish, choose End consultation instead.
            </DialogContent>
            <DialogActions>
              <Button onClick={() => blocker.reset?.()}>Stay in the call</Button>
              <Button appearance="primary" onClick={() => blocker.proceed?.()}>
                Leave call
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}

/** A closed consultation, or any consultation for staff who do not run calls: details and the chat transcript. */
function SessionRecord({ session, canConsult }: { session: TelehealthSessionDetail; canConsult: boolean }) {
  const styles = useStyles();
  const actions = useSessionActions();
  const messages = useQuery(telehealthQueries.messages(session.id));
  const open = isSessionOpen(session.status);

  return (
    <>
      {open && !canConsult && (
        <MessageBar intent="info">
          <MessageBarBody>
            Only doctors run video consultations. You can send the patient a new link or cancel the consultation here.
          </MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.summary}>
        <Section title="Consultation">
          <SessionDetails
            session={session}
            canConsult={false}
            canResend={actions.canManage && open}
            resending={actions.resending}
            onResend={() => actions.resendLink(session.id)}
          />
          {actions.canManage && open && session.status !== 'InProgress' && (
            <div style={{ marginTop: 12 }}>
              <Button
                onClick={() =>
                  actions.cancel({
                    id: session.id,
                    patientName: session.patientName,
                    scheduledStartAt: session.scheduledStartAt,
                  })
                }
              >
                Cancel consultation
              </Button>
            </div>
          )}
        </Section>
        <Section title="Chat transcript" description="Kept with the consultation record">
          {messages.error ? (
            <ErrorState error={messages.error} onRetry={() => void messages.refetch()} />
          ) : messages.isLoading ? (
            <LoadingState />
          ) : (
            <ChatPanel messages={messages.data ?? []} disabled readOnly />
          )}
        </Section>
      </div>
      {actions.dialogs}
    </>
  );
}
