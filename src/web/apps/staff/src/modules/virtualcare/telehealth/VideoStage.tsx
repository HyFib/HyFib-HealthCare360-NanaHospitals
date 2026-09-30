import { Avatar, Button, makeStyles, mergeClasses, Spinner, Text, tokens } from '@fluentui/react-components';
import { ArrowClockwiseRegular, PersonAvailableRegular, Speaker2Regular, VideoOffRegular } from '@fluentui/react-icons';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import type { CallState } from './TelehealthCall';

const useStyles = makeStyles({
  stage: {
    position: 'relative',
    width: '100%',
    aspectRatio: '16 / 9',
    maxHeight: 'calc(100vh - 330px)',
    minHeight: '260px',
    backgroundColor: '#101418',
    borderRadius: tokens.borderRadiusXLarge,
    overflow: 'hidden',
  },
  remote: { width: '100%', height: '100%', objectFit: 'contain', display: 'block', backgroundColor: '#101418' },
  local: {
    position: 'absolute',
    right: '12px',
    bottom: '12px',
    width: '22%',
    minWidth: '140px',
    aspectRatio: '4 / 3',
    objectFit: 'cover',
    transform: 'scaleX(-1)',
    borderRadius: tokens.borderRadiusLarge,
    border: '2px solid rgba(255, 255, 255, 0.6)',
    backgroundColor: '#1d232a',
    boxShadow: tokens.shadow16,
  },
  localOff: { display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#e6e6e6', transform: 'none' },
  hidden: { visibility: 'hidden' },
  overlay: {
    position: 'absolute',
    inset: '0',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '12px',
    padding: '24px',
    textAlign: 'center',
    color: '#f5f5f5',
  },
  overlayText: { color: '#f5f5f5', maxWidth: '520px' },
  front: { zIndex: 3, backgroundColor: 'rgba(0, 0, 0, 0.45)' },
  banner: {
    position: 'absolute',
    top: '12px',
    left: '50%',
    transform: 'translateX(-50%)',
    padding: '6px 12px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: 'rgba(0, 0, 0, 0.7)',
    color: '#ffd666',
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'nowrap',
  },
  name: {
    position: 'absolute',
    left: '12px',
    bottom: '12px',
    padding: '4px 10px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: 'rgba(0, 0, 0, 0.55)',
    color: '#ffffff',
    fontSize: tokens.fontSizeBase200,
  },
});

/**
 * Plays a MediaStream in a <video>. When the browser blocks autoplay with sound, a button lets the doctor start it
 * (browsers allow sound once the user has interacted with the page).
 */
function StreamVideo({
  stream,
  className,
  label,
  muted = false,
  resumeLabel,
  overlayClassName,
}: {
  stream: MediaStream | null;
  className: string;
  label: string;
  muted?: boolean;
  resumeLabel?: string;
  overlayClassName?: string;
}) {
  const video = useRef<HTMLVideoElement>(null);
  const [blocked, setBlocked] = useState(false);

  useEffect(() => {
    const element = video.current;
    if (!element) {
      return;
    }
    if (element.srcObject !== stream) {
      element.srcObject = stream;
    }
    if (stream) {
      element.play().then(
        () => setBlocked(false),
        (error: unknown) => setBlocked((error as { name?: string } | null)?.name === 'NotAllowedError'),
      );
    }
  }, [stream]);

  const resume = () => {
    void video.current?.play().then(
      () => setBlocked(false),
      () => undefined,
    );
  };

  return (
    <>
      <video ref={video} className={className} autoPlay playsInline muted={muted} aria-label={label} />
      {blocked && resumeLabel && (
        <div className={overlayClassName}>
          <Button appearance="primary" size="large" icon={<Speaker2Regular />} onClick={resume}>
            {resumeLabel}
          </Button>
        </div>
      )}
    </>
  );
}

export interface VideoStageProps {
  state: CallState;
  patientName: string;
  earlyJoinMinutes: number;
  admitting: boolean;
  onAdmit: (participantId?: string) => void;
  onRetryJoin: () => void;
  onReconnectVideo: () => void;
}

/** The patient's video with the doctor's own camera picture-in-picture, and a plain-language status overlay. */
export function VideoStage({
  state,
  patientName,
  earlyJoinMinutes,
  admitting,
  onAdmit,
  onRetryJoin,
  onReconnectVideo,
}: VideoStageProps) {
  const styles = useStyles();
  const remoteName = state.remote?.name ?? patientName;
  const remoteHasVideo =
    Boolean(state.remoteStream?.getVideoTracks().some((track) => track.readyState === 'live')) &&
    !state.remoteAudioOnly;
  const firstWaiting = state.waiting[0];

  let overlay: ReactNode = null;
  if (state.phase === 'joining') {
    overlay = <Spinner appearance="inverted" label="Joining the consultation…" labelPosition="below" />;
  } else if (state.phase === 'failed') {
    overlay = (
      <>
        <Text className={styles.overlayText} weight="semibold">
          Could not join the consultation
        </Text>
        <Text className={styles.overlayText}>{state.joinError}</Text>
        <Button appearance="primary" icon={<ArrowClockwiseRegular />} onClick={onRetryJoin}>
          Try again
        </Button>
      </>
    );
  } else if (state.phase === 'ended') {
    overlay = <Text className={styles.overlayText}>The consultation has ended.</Text>;
  } else if (!state.remote && firstWaiting) {
    overlay = (
      <>
        <Avatar name={firstWaiting.displayName} size={72} color="colorful" />
        <Text className={styles.overlayText} size={500} weight="semibold">
          {firstWaiting.displayName} is in the waiting room
        </Text>
        <Button
          appearance="primary"
          size="large"
          icon={<PersonAvailableRegular />}
          disabled={admitting}
          onClick={() => onAdmit(firstWaiting.participantId)}
        >
          Admit and start
        </Button>
      </>
    );
  } else if (!state.remote) {
    overlay = (
      <>
        <Spinner appearance="inverted" size="small" />
        <Text className={styles.overlayText} weight="semibold">
          Waiting for {patientName} to join
        </Text>
        <Text className={styles.overlayText}>
          The patient joins from the link sent by SMS/WhatsApp or from the patient portal, from {earlyJoinMinutes}{' '}
          minutes before the time. You are told here when they arrive.
        </Text>
      </>
    );
  } else if (state.peer === 'connecting') {
    overlay = <Spinner appearance="inverted" label={`Connecting video with ${remoteName}…`} labelPosition="below" />;
  } else if (state.peer === 'reconnecting') {
    overlay = (
      <Spinner appearance="inverted" label="The connection was interrupted. Reconnecting…" labelPosition="below" />
    );
  } else if (state.peer === 'failed') {
    overlay = (
      <>
        <Text className={styles.overlayText} weight="semibold">
          The video could not connect
        </Text>
        <Text className={styles.overlayText}>
          The network may be blocking it. Try again, or continue by chat and phone.
        </Text>
        <Button appearance="primary" icon={<ArrowClockwiseRegular />} onClick={onReconnectVideo}>
          Try again
        </Button>
      </>
    );
  } else if (!remoteHasVideo) {
    overlay = (
      <>
        <Avatar name={remoteName} size={96} color="colorful" />
        <Text className={styles.overlayText} weight="semibold">
          <Speaker2Regular aria-hidden style={{ verticalAlign: 'middle', marginRight: 6 }} />
          {remoteName} · audio only
        </Text>
      </>
    );
  }

  return (
    <div className={styles.stage} role="region" aria-label="Video consultation">
      <StreamVideo
        stream={state.remoteStream}
        className={mergeClasses(styles.remote, !remoteHasVideo && styles.hidden)}
        label={`Video of ${remoteName}`}
        resumeLabel={`Click to hear ${remoteName}`}
        overlayClassName={mergeClasses(styles.overlay, styles.front)}
      />
      {overlay && <div className={styles.overlay}>{overlay}</div>}
      {state.remote && state.peer === 'connected' && <span className={styles.name}>{remoteName}</span>}
      {state.localStream && (
        <StreamVideo
          stream={state.localStream}
          className={mergeClasses(styles.local, !state.cameraOn && styles.hidden)}
          label="Your camera"
          muted
        />
      )}
      {state.phase === 'joined' && (!state.localStream || !state.cameraOn) && (
        <div className={mergeClasses(styles.local, styles.localOff)} role="img" aria-label="Your camera is off">
          <VideoOffRegular fontSize={28} />
        </div>
      )}
      {state.hub === 'reconnecting' && (
        <span className={styles.banner}>Connection to the server lost. Reconnecting… the call may continue.</span>
      )}
    </div>
  );
}
