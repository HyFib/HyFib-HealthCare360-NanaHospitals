import {
  Avatar,
  Button,
  CounterBadge,
  Dialog,
  DialogSurface,
  Field,
  makeStyles,
  mergeClasses,
  Spinner,
  Textarea,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  CallEndRegular,
  CameraSwitchRegular,
  ChatRegular,
  CheckmarkCircleRegular,
  HeadphonesRegular,
  HourglassHalfRegular,
  InfoRegular,
  MicOffRegular,
  MicRegular,
  SendRegular,
  VideoOffRegular,
  VideoRegular,
  Wifi1Regular,
  Wifi2Regular,
  Wifi3Regular,
  WifiWarningRegular,
} from '@fluentui/react-icons';
import { useEffect, useState, useSyncExternalStore, type ReactElement, type ReactNode } from 'react';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { Sheet } from '@/components/Sheet';
import { Notice, PageSpinner } from '@/components/States';
import { useT, type MessageKey } from '@/i18n';
import { formatCountdown } from '@/i18n/format';
import { useFormat } from '@/i18n/useFormat';
import { useNow } from '@/lib/hooks';
import { useCallActivity } from '@/lib/sessionEnd';
import type { IceServerDto, TelehealthMessageDto } from '@/lib/types';
import type { DeviceChoice } from './DeviceCheck';
import { classifyJoinError, detectPlatform, type JoinProblem } from './media';
import { PatientCall, type PatientCallOptions, type PatientCallState } from './PatientCall';
import { useWakeLock } from './useWakeLock';
import { VideoView } from './VideoView';

const JOIN_PROBLEM: Record<JoinProblem, MessageKey> = {
  tooEarly: 'video.join.tooEarly',
  closed: 'video.join.closed',
  cancelled: 'video.join.cancelled',
  missed: 'video.join.missed',
  linkInvalid: 'video.join.linkInvalid',
  linkExpired: 'video.join.linkExpired',
  notLinked: 'video.join.notLinked',
  signIn: 'video.join.signIn',
  tooMany: 'video.join.tooMany',
  other: 'video.join.network',
};

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
  waitingLayout: { display: 'grid', gap: '16px', '@media (min-width: 760px)': { gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', alignItems: 'center' } },
  waitingPreview: { width: '100%', aspectRatio: '4 / 3', objectFit: 'cover', borderRadius: tokens.borderRadiusXLarge, backgroundColor: '#1b1b1b' },
  waitingText: { display: 'flex', flexDirection: 'column', gap: '10px' },
  waitingTitle: { margin: 0, fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold, display: 'flex', alignItems: 'center', gap: '8px' },
  pulse: {
    width: '12px',
    height: '12px',
    borderRadius: '50%',
    backgroundColor: tokens.colorStatusWarningForeground3,
    animationName: { '0%': { opacity: 1 }, '50%': { opacity: 0.3 }, '100%': { opacity: 1 } },
    animationDuration: '1.6s',
    animationIterationCount: 'infinite',
  },
  muted: { margin: 0, color: tokens.colorNeutralForeground2 },
  row: { display: 'flex', flexWrap: 'wrap', gap: '8px' },
  surface: {
    padding: 0,
    width: '100vw',
    maxWidth: '100vw',
    height: '100dvh',
    maxHeight: '100dvh',
    borderRadius: 0,
    border: 'none',
    backgroundColor: '#0b0b0b',
    overflow: 'hidden',
  },
  stage: { position: 'relative', width: '100%', height: '100%', color: '#fff', display: 'flex', flexDirection: 'column' },
  remote: { position: 'absolute', inset: 0, width: '100%', height: '100%', objectFit: 'contain', backgroundColor: '#0b0b0b' },
  overlay: {
    position: 'absolute',
    inset: 0,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '16px',
    padding: '24px',
    textAlign: 'center',
    background: 'radial-gradient(circle at 50% 40%, #1d3b49 0%, #0b0b0b 70%)',
  },
  overlayText: { margin: 0, fontSize: tokens.fontSizeBase400, maxWidth: '420px', color: '#f0f0f0' },
  top: {
    position: 'relative',
    zIndex: 2,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
    padding: 'calc(12px + env(safe-area-inset-top)) 16px 12px',
    background: 'linear-gradient(180deg, rgba(0,0,0,0.65) 0%, rgba(0,0,0,0) 100%)',
  },
  doctor: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  quality: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 10px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: 'rgba(0,0,0,0.5)',
    fontSize: tokens.fontSizeBase200,
    flexShrink: 0,
  },
  poor: { color: '#ffb3b3' },
  fair: { color: '#ffe08a' },
  good: { color: '#9be29b' },
  self: {
    position: 'absolute',
    zIndex: 2,
    right: '12px',
    bottom: 'calc(104px + env(safe-area-inset-bottom))',
    width: '28vw',
    maxWidth: '180px',
    minWidth: '96px',
    aspectRatio: '3 / 4',
    borderRadius: tokens.borderRadiusLarge,
    overflow: 'hidden',
    border: '2px solid rgba(255,255,255,0.7)',
    backgroundColor: '#222',
    boxShadow: tokens.shadow16,
    '@media (min-width: 900px)': { aspectRatio: '4 / 3', maxWidth: '240px' },
  },
  selfVideo: { width: '100%', height: '100%', objectFit: 'cover' },
  selfOff: { width: '100%', height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#ddd', fontSize: '28px' },
  banner: {
    position: 'relative',
    zIndex: 2,
    margin: '0 12px',
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: 'rgba(32,32,32,0.9)',
    color: '#fff',
  },
  spacer: { flex: 1 },
  controls: {
    position: 'relative',
    zIndex: 2,
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '12px',
    padding: '16px 12px calc(16px + env(safe-area-inset-bottom))',
    background: 'linear-gradient(0deg, rgba(0,0,0,0.75) 0%, rgba(0,0,0,0) 100%)',
  },
  control: {
    width: '56px',
    height: '56px',
    minWidth: '56px',
    borderRadius: '50%',
    backgroundColor: 'rgba(255,255,255,0.18)',
    color: '#fff',
    border: 'none',
    fontSize: '24px',
    ':hover': { backgroundColor: 'rgba(255,255,255,0.28)', color: '#fff' },
    ':hover:active': { backgroundColor: 'rgba(255,255,255,0.36)', color: '#fff' },
    ':focus-visible': { outline: '3px solid #fff', outlineOffset: '2px' },
  },
  off: { backgroundColor: '#f3f3f3', color: '#111', ':hover': { backgroundColor: '#e0e0e0', color: '#111' } },
  end: { backgroundColor: '#c4314b', ':hover': { backgroundColor: '#a4262c', color: '#fff' }, ':hover:active': { backgroundColor: '#8e1e25', color: '#fff' } },
  chatButton: { position: 'relative' },
  badge: { position: 'absolute', top: '-2px', right: '-2px' },
  tapToHear: { position: 'absolute', zIndex: 3, left: '50%', top: '50%', transform: 'translate(-50%, -50%)' },
  chatList: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  bubble: { maxWidth: '85%', padding: '8px 12px', borderRadius: tokens.borderRadiusXLarge, backgroundColor: tokens.colorNeutralBackground3, alignSelf: 'flex-start' },
  mine: { alignSelf: 'flex-end', backgroundColor: tokens.colorBrandBackground2 },
  bubbleMeta: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  bubbleText: { margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
  srOnly: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    margin: '-1px',
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
    border: 0,
    padding: 0,
  },
  endedIcon: { fontSize: '48px', color: tokens.colorStatusSuccessForeground1 },
});

export interface CallScreenProps {
  sessionId: string;
  hubPath: string;
  identity: PatientCallOptions['identity'];
  iceServers?: IceServerDto[];
  choice: DeviceChoice;
  /** Back to the device check to join again. */
  onRejoin: () => void;
  /** Extra actions after the consultation (prescriptions, feedback) for signed-in patients. */
  endActions?: ReactNode;
}

/** The call from joining to the end. Mount it once per join (it owns the camera, microphone and connection). */
export function CallScreen({ sessionId, hubPath, identity, iceServers, choice, onRejoin, endActions }: CallScreenProps) {
  const [call] = useState(
    () =>
      new PatientCall({
        sessionId,
        hubPath,
        identity,
        iceServers,
        audioDeviceId: choice.audioDeviceId,
        videoDeviceId: choice.videoDeviceId,
        startAudioOnly: choice.audioOnly,
      }),
  );
  const state = useSyncExternalStore(call.subscribe, call.getSnapshot, call.getSnapshot);
  const setInCall = useCallActivity((s) => s.setInCall);
  const live = state.phase === 'waiting' || state.phase === 'in-call';

  useEffect(() => {
    call.start();
    return () => call.stop();
  }, [call]);

  useEffect(() => {
    setInCall(true);
    return () => setInCall(false);
  }, [setInCall]);

  useWakeLock(live);

  // Closing the tab mid-consultation asks first (the browser shows its own wording).
  useEffect(() => {
    if (!live) {
      return;
    }
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [live]);

  switch (state.phase) {
    case 'failed':
      return <JoinFailed state={state} onRetry={() => call.retry()} />;
    case 'waiting':
      return <WaitingRoom state={state} call={call} />;
    case 'in-call':
      return <InCall state={state} call={call} />;
    case 'ended':
      return <Ended state={state} onRejoin={onRejoin} actions={endActions} />;
    default:
      return <Connecting />;
  }
}

function Connecting() {
  const t = useT();
  return <PageSpinner label={t('video.connecting')} />;
}

function JoinFailed({ state, onRetry }: { state: PatientCallState; onRetry: () => void }) {
  const styles = useStyles();
  const t = useT();
  const problem = state.joinError ? classifyJoinError(state.joinError) : 'other';
  const retryable = problem === 'other' || problem === 'tooEarly' || problem === 'tooMany';
  return (
    <div className={styles.card}>
      <Notice intent={retryable ? 'warning' : 'error'} title={t('video.join.failed')}>
        {t(JOIN_PROBLEM[problem])}
      </Notice>
      {retryable && (
        <div className={styles.row}>
          <Button appearance="primary" size="large" icon={<ArrowClockwiseRegular />} onClick={onRetry}>
            {t('common.retry')}
          </Button>
        </div>
      )}
    </div>
  );
}

function WaitingRoom({ state, call }: { state: PatientCallState; call: PatientCall }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const now = useNow(1000);
  const [confirmLeave, setConfirmLeave] = useState(false);
  const doctor = state.doctorName ?? t('common.doctor');
  const waited = state.waitingSince ? now - state.waitingSince : 0;

  return (
    <div className={styles.card}>
      <div className={styles.waitingLayout}>
        {state.localStream?.getVideoTracks().length ? (
          <VideoView stream={state.localStream} muted mirrored className={styles.waitingPreview} label={t('video.check.preview')} />
        ) : (
          <Avatar name={doctor} size={96} color="colorful" aria-hidden style={{ justifySelf: 'center' }} />
        )}
        <div className={styles.waitingText}>
          <h2 className={styles.waitingTitle}>
            <span className={styles.pulse} aria-hidden />
            {t('video.waiting.title')}
          </h2>
          <p className={styles.muted} role="status">
            {t('video.waiting.body', { doctor })}
          </p>
          {state.scheduledStartAt && <p className={styles.muted}>{t('video.waiting.scheduled', { time: f.time(state.scheduledStartAt) })}</p>}
          {waited > 0 && (
            <p className={styles.muted}>
              <HourglassHalfRegular aria-hidden /> {t('video.waiting.since', { time: formatCountdown(waited) })}
            </p>
          )}
          {state.hub === 'reconnecting' && <Notice intent="warning">{t('video.call.signalLost')}</Notice>}
          {state.mediaProblem && state.localStream && <Notice intent="warning">{t('video.check.cameraProblem')}</Notice>}
          <p className={styles.muted}>
            <InfoRegular aria-hidden /> {t('video.chat.notAdmitted')}
          </p>
          <div className={styles.row}>
            <Button size="large" onClick={() => setConfirmLeave(true)}>
              {t('video.leave')}
            </Button>
          </div>
        </div>
      </div>
      <ConfirmDialog
        open={confirmLeave}
        title={t('video.leave.title')}
        confirmLabel={t('video.leave')}
        dismissLabel={t('video.leave.stay')}
        onConfirm={() => {
          setConfirmLeave(false);
          call.leave();
        }}
        onDismiss={() => setConfirmLeave(false)}
      >
        {t('video.leave.body')}
      </ConfirmDialog>
    </div>
  );
}

const QUALITY_ICON = { good: Wifi3Regular, fair: Wifi2Regular, poor: Wifi1Regular } as const;
const QUALITY_TEXT: Record<'good' | 'fair' | 'poor', MessageKey> = { good: 'video.quality.good', fair: 'video.quality.fair', poor: 'video.quality.poor' };

function InCall({ state, call }: { state: PatientCallState; call: PatientCall }) {
  const styles = useStyles();
  const t = useT();
  const [chatOpen, setChatOpen] = useState(false);
  const [seen, setSeen] = useState(0);
  const [confirmLeave, setConfirmLeave] = useState(false);
  const [needsTap, setNeedsTap] = useState(false);
  const [playToken, setPlayToken] = useState(0);
  const [mobile] = useState(() => typeof navigator !== 'undefined' && detectPlatform(navigator.userAgent, navigator.maxTouchPoints) !== 'desktop');
  const doctor = state.doctorName ?? t('common.doctor');
  const fromDoctor = state.messages.filter((message) => message.senderRole === 'Clinician');
  const unread = chatOpen ? 0 : Math.max(0, fromDoctor.length - seen);
  const latest = fromDoctor.at(-1);
  const hasLocalVideo = Boolean(state.localStream?.getVideoTracks().length);
  const remoteVideo = Boolean(state.remoteStream?.getVideoTracks().length) && !state.remoteAudioOnly;
  const quality = state.quality?.level;
  const QualityIcon = quality ? QUALITY_ICON[quality] : null;

  let overlay: string | null = null;
  if (!state.remoteStream || state.peer === 'connecting') {
    overlay = t('video.call.connectingDoctor', { doctor });
  }
  if (!state.doctorPresent && state.peer === 'idle') {
    overlay = t('video.call.doctorLeft');
  }
  if (state.peer === 'reconnecting') {
    overlay = t('video.call.reconnecting');
  }
  if (state.peer === 'failed') {
    overlay = t('video.call.failed');
  }
  if (!overlay && !remoteVideo) {
    overlay = t('video.call.doctorAudioOnly');
  }

  return (
    <Dialog open modalType="alert">
      <DialogSurface className={styles.surface} aria-label={t('video.call.label', { doctor })}>
        <div className={styles.stage}>
          <VideoView
            stream={state.remoteStream}
            className={styles.remote}
            label={t('video.call.label', { doctor })}
            onPlayBlocked={() => setNeedsTap(true)}
            playToken={playToken}
          />
          {overlay && (
            <div className={styles.overlay}>
              <Avatar name={doctor} size={96} color="colorful" aria-hidden />
              <p className={styles.overlayText} role="status">
                {overlay}
              </p>
              {(state.peer === 'connecting' || state.peer === 'reconnecting') && <Spinner appearance="inverted" />}
              {state.peer === 'failed' && (
                <Button appearance="primary" size="large" icon={<ArrowClockwiseRegular />} onClick={() => void call.reconnectVideo()}>
                  {t('video.call.reconnect')}
                </Button>
              )}
            </div>
          )}

          <div className={styles.top}>
            <span className={styles.doctor}>{doctor}</span>
            {state.hub === 'reconnecting' ? (
              <span className={mergeClasses(styles.quality, styles.poor)}>
                <WifiWarningRegular aria-hidden />
                {t('video.call.signalLost')}
              </span>
            ) : (
              QualityIcon &&
              quality && (
                <span className={mergeClasses(styles.quality, styles[quality])}>
                  <QualityIcon aria-hidden />
                  {t(QUALITY_TEXT[quality])}
                </span>
              )
            )}
          </div>

          {state.audioOnly ? (
            <div className={styles.banner} role="status">
              <span>{t('video.call.audioOnlyOn')}</span>
              {hasLocalVideo && (
                <Button size="small" onClick={() => call.setAudioOnly(false)}>
                  {t('video.call.backToVideo')}
                </Button>
              )}
            </div>
          ) : (
            state.poorStreak >= 3 &&
            hasLocalVideo && (
              <div className={styles.banner} role="status">
                <span>{t('video.quality.suggest')}</span>
                <Button size="small" onClick={() => call.setAudioOnly(true)}>
                  {t('video.quality.switch')}
                </Button>
              </div>
            )
          )}

          {needsTap && (
            <Button
              className={styles.tapToHear}
              appearance="primary"
              size="large"
              onClick={() => {
                setNeedsTap(false);
                setPlayToken((value) => value + 1);
              }}
            >
              {t('video.call.tapToHear')}
            </Button>
          )}

          <div className={styles.spacer} />

          <div className={styles.self} aria-hidden>
            {hasLocalVideo && state.cameraOn && !state.audioOnly ? (
              <VideoView stream={state.localStream} muted mirrored className={styles.selfVideo} />
            ) : (
              <span className={styles.selfOff}>
                <VideoOffRegular />
              </span>
            )}
          </div>

          <span className={styles.srOnly} aria-live="polite">
            {unread > 0 && latest ? t('video.chat.new', { name: latest.senderName }) : ''}
          </span>

          <div className={styles.controls} role="toolbar" aria-label={t('video.call.label', { doctor })}>
            <ControlButton
              label={state.micOn ? t('video.call.mute') : t('video.call.unmute')}
              pressed={!state.micOn}
              off={!state.micOn}
              icon={state.micOn ? <MicRegular /> : <MicOffRegular />}
              disabled={!state.localStream?.getAudioTracks().length}
              onClick={() => call.setMicrophone(!state.micOn)}
            />
            {hasLocalVideo && (
              <ControlButton
                label={state.cameraOn ? t('video.call.cameraOff') : t('video.call.cameraOn')}
                pressed={!state.cameraOn}
                off={!state.cameraOn}
                icon={state.cameraOn ? <VideoRegular /> : <VideoOffRegular />}
                onClick={() => call.setCamera(!state.cameraOn)}
              />
            )}
            {hasLocalVideo && (
              <ControlButton
                label={t('video.call.audioOnly')}
                pressed={state.audioOnly}
                off={state.audioOnly}
                icon={<HeadphonesRegular />}
                onClick={() => call.setAudioOnly(!state.audioOnly)}
              />
            )}
            {hasLocalVideo && mobile && !state.audioOnly && (
              <ControlButton label={t('video.call.switchCamera')} icon={<CameraSwitchRegular />} onClick={() => void call.switchCamera()} />
            )}
            <span className={styles.chatButton}>
              <ControlButton
                label={unread > 0 ? t('video.call.chatUnread', { count: unread }) : t('video.call.chat')}
                icon={<ChatRegular />}
                onClick={() => {
                  setChatOpen(true);
                  setSeen(fromDoctor.length);
                }}
              />
              {unread > 0 && <CounterBadge className={styles.badge} count={unread} color="danger" aria-hidden />}
            </span>
            <ControlButton label={t('video.call.end')} icon={<CallEndRegular />} danger onClick={() => setConfirmLeave(true)} />
          </div>
        </div>

        <ChatSheet
          open={chatOpen}
          onClose={() => {
            setChatOpen(false);
            setSeen(fromDoctor.length);
          }}
          messages={state.messages}
          onSend={(text) => call.sendChat(text)}
        />
        <ConfirmDialog
          open={confirmLeave}
          title={t('video.leave.title')}
          confirmLabel={t('video.call.end')}
          dismissLabel={t('video.leave.stay')}
          destructive
          onConfirm={() => {
            setConfirmLeave(false);
            call.leave();
          }}
          onDismiss={() => setConfirmLeave(false)}
        >
          {t('video.leave.body')}
        </ConfirmDialog>
      </DialogSurface>
    </Dialog>
  );
}

function ControlButton({
  label,
  icon,
  onClick,
  pressed,
  off,
  danger,
  disabled,
}: {
  label: string;
  icon: ReactElement;
  onClick: () => void;
  pressed?: boolean;
  off?: boolean;
  danger?: boolean;
  disabled?: boolean;
}) {
  const styles = useStyles();
  return (
    <Tooltip content={label} relationship="label">
      <Button
        className={mergeClasses(styles.control, off && styles.off, danger && styles.end)}
        icon={icon}
        aria-pressed={pressed}
        disabled={disabled}
        onClick={onClick}
      />
    </Tooltip>
  );
}

function ChatSheet({
  open,
  onClose,
  messages,
  onSend,
}: {
  open: boolean;
  onClose: () => void;
  messages: TelehealthMessageDto[];
  onSend: (text: string) => Promise<void>;
}) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const [text, setText] = useState('');
  const [sending, setSending] = useState(false);
  const [failed, setFailed] = useState(false);

  const send = async () => {
    const clean = text.trim();
    if (!clean || sending) {
      return;
    }
    setSending(true);
    setFailed(false);
    try {
      await onSend(clean);
      setText('');
    } catch {
      setFailed(true);
    } finally {
      setSending(false);
    }
  };

  return (
    <Sheet
      open={open}
      title={t('video.chat.title')}
      onClose={onClose}
      footer={
        <form
          style={{ display: 'flex', gap: 8, width: '100%', alignItems: 'flex-end' }}
          onSubmit={(event) => {
            event.preventDefault();
            void send();
          }}
        >
          <Field label={t('video.chat.label')} style={{ flex: 1 }} validationState={failed ? 'error' : 'none'} validationMessage={failed ? t('video.chat.failed') : undefined}>
            <Textarea
              value={text}
              onChange={(_, data) => setText(data.value)}
              placeholder={t('video.chat.placeholder')}
              rows={1}
              maxLength={2000}
              resize="vertical"
              onKeyDown={(event) => {
                if (event.key === 'Enter' && !event.shiftKey) {
                  event.preventDefault();
                  void send();
                }
              }}
            />
          </Field>
          <Button type="submit" appearance="primary" size="large" icon={sending ? <Spinner size="tiny" /> : <SendRegular />} disabled={sending || !text.trim()}>
            {t('video.chat.send')}
          </Button>
        </form>
      }
    >
      {messages.length === 0 ? (
        <p style={{ margin: 0 }}>{t('video.chat.empty')}</p>
      ) : (
        <ul className={styles.chatList} role="log" aria-live="polite">
          {messages.map((message) => {
            const mine = message.senderRole !== 'Clinician';
            return (
              <li key={message.id} className={mergeClasses(styles.bubble, mine && styles.mine)}>
                <span className={styles.bubbleMeta}>
                  {mine ? t('video.call.you') : message.senderName} · {f.time(message.sentAt)}
                </span>
                <p className={styles.bubbleText}>{message.text}</p>
              </li>
            );
          })}
        </ul>
      )}
    </Sheet>
  );
}

function Ended({ state, onRejoin, actions }: { state: PatientCallState; onRejoin: () => void; actions?: ReactNode }) {
  const styles = useStyles();
  const t = useT();
  const reason = state.endReason;
  const message: MessageKey =
    reason === 'left' ? 'video.ended.left' : reason === 'Cancelled' ? 'video.ended.cancelled' : reason === 'Missed' ? 'video.ended.missed' : 'video.ended.body';
  return (
    <div className={styles.card}>
      {reason === 'Completed' && <CheckmarkCircleRegular className={styles.endedIcon} aria-hidden />}
      <h2 className={styles.waitingTitle}>{reason === 'left' ? t('video.ended.left') : t('video.ended.title')}</h2>
      <p className={styles.muted} role="status">
        {t(message)}
      </p>
      <div className={styles.row}>
        {reason === 'left' && (
          <Button appearance="primary" size="large" icon={<VideoRegular />} onClick={onRejoin}>
            {t('video.ended.rejoin')}
          </Button>
        )}
        {actions}
      </div>
    </div>
  );
}
