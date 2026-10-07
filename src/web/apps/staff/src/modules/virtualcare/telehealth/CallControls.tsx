import { Button, Caption1, makeStyles, mergeClasses, Tooltip, tokens } from '@fluentui/react-components';
import {
  CallEndRegular,
  CallRegular,
  DoorArrowLeftRegular,
  MicOffRegular,
  MicRegular,
  VideoOffRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import { useHotkey } from '@hc360/core';
import type { CallState, TelehealthCall } from './TelehealthCall';
import { describeQuality, type QualityReading } from './webrtc';

const useStyles = makeStyles({
  bar: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap', padding: '10px 0' },
  spacer: { flex: 1 },
  end: {
    backgroundColor: tokens.colorPaletteRedBackground3,
    color: tokens.colorNeutralForegroundOnBrand,
    ':hover': { backgroundColor: tokens.colorPaletteRedForeground1, color: tokens.colorNeutralForegroundOnBrand },
  },
  off: { color: tokens.colorPaletteRedForeground1 },
  quality: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 8px',
    borderRadius: tokens.borderRadiusMedium,
  },
  bars: { display: 'inline-flex', alignItems: 'flex-end', gap: '2px', height: '16px' },
  bar1: { width: '4px', height: '6px', borderRadius: '1px', backgroundColor: tokens.colorNeutralStroke1 },
  bar2: { width: '4px', height: '11px', borderRadius: '1px', backgroundColor: tokens.colorNeutralStroke1 },
  bar3: { width: '4px', height: '16px', borderRadius: '1px', backgroundColor: tokens.colorNeutralStroke1 },
  good: { backgroundColor: tokens.colorPaletteGreenBackground3 },
  fair: { backgroundColor: tokens.colorPaletteMarigoldBackground3 },
  poor: { backgroundColor: tokens.colorPaletteRedBackground3 },
});

/** Three-bar connection indicator with a word, so colour is never the only signal. */
export function QualityIndicator({ quality, connected }: { quality: QualityReading | null; connected: boolean }) {
  const styles = useStyles();
  const level = connected ? quality?.level : undefined;
  const filled = level === 'good' ? 3 : level === 'fair' ? 2 : level === 'poor' ? 1 : 0;
  const tone = level ? styles[level] : undefined;
  const label = !connected
    ? 'Not connected'
    : !quality
      ? 'Measuring…'
      : level === 'good'
        ? 'Good'
        : level === 'fair'
          ? 'Fair'
          : 'Poor';
  return (
    <Tooltip
      content={connected ? describeQuality(quality) : 'The video is not connected yet'}
      relationship="description"
    >
      <span className={styles.quality} role="img" aria-label={`Connection quality: ${label}`} tabIndex={0}>
        <span className={styles.bars} aria-hidden>
          <span className={mergeClasses(styles.bar1, filled >= 1 && tone)} />
          <span className={mergeClasses(styles.bar2, filled >= 2 && tone)} />
          <span className={mergeClasses(styles.bar3, filled >= 3 && tone)} />
        </span>
        <Caption1>{label}</Caption1>
      </span>
    </Tooltip>
  );
}

export interface CallControlsProps {
  state: CallState;
  call: TelehealthCall;
  canEnd: boolean;
  onEnd: () => void;
  onLeave: () => void;
}

/** Mute, camera, audio-only fallback, quality and hang-up. Shortcuts follow Microsoft Teams (Ctrl+Shift+M / O). */
export function CallControls({ state, call, canEnd, onEnd, onLeave }: CallControlsProps) {
  const styles = useStyles();
  const active = state.phase === 'joined';
  const canUseMic = active && state.media.hasAudio;
  const canUseCamera = active && state.media.hasVideo;

  // Muting must work even while a dialog is open over the call.
  useHotkey('mod+shift+m', () => canUseMic && call.setMicrophone(!state.micOn), {
    allowInInputs: true,
    overDialogs: true,
  });
  useHotkey('mod+shift+o', () => canUseCamera && call.setCamera(!state.cameraOn), {
    allowInInputs: true,
    overDialogs: true,
  });

  return (
    <div className={styles.bar} role="toolbar" aria-label="Call controls">
      <Tooltip content={`${state.micOn ? 'Mute' : 'Unmute'} (Ctrl+Shift+M)`} relationship="description">
        <Button
          icon={state.micOn ? <MicRegular /> : <MicOffRegular className={styles.off} />}
          disabled={!canUseMic}
          aria-pressed={!state.micOn}
          aria-keyshortcuts="Control+Shift+M"
          onClick={() => call.setMicrophone(!state.micOn)}
        >
          {state.micOn ? 'Mute' : 'Unmute'}
        </Button>
      </Tooltip>
      <Tooltip
        content={`${state.cameraOn ? 'Turn camera off' : 'Turn camera on'} (Ctrl+Shift+O)`}
        relationship="description"
      >
        <Button
          icon={state.cameraOn ? <VideoRegular /> : <VideoOffRegular className={styles.off} />}
          disabled={!canUseCamera}
          aria-pressed={!state.cameraOn}
          aria-keyshortcuts="Control+Shift+O"
          onClick={() => call.setCamera(!state.cameraOn)}
        >
          {state.cameraOn ? 'Camera off' : 'Camera on'}
        </Button>
      </Tooltip>
      <Tooltip content="Stop video to keep the call going on a weak network" relationship="description">
        <Button
          icon={<CallRegular />}
          disabled={!canUseCamera}
          aria-pressed={state.audioOnly}
          appearance={state.audioOnly ? 'primary' : 'secondary'}
          onClick={() => call.setAudioOnly(!state.audioOnly)}
        >
          Audio only
        </Button>
      </Tooltip>
      <QualityIndicator quality={state.quality} connected={state.peer === 'connected'} />
      <span className={styles.spacer} />
      {canEnd ? (
        <Button appearance="primary" className={styles.end} icon={<CallEndRegular />} onClick={onEnd}>
          End consultation
        </Button>
      ) : (
        <Button icon={<DoorArrowLeftRegular />} onClick={onLeave}>
          Leave
        </Button>
      )}
    </div>
  );
}
