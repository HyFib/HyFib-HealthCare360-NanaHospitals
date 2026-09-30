import { Button, Field, makeStyles, ProgressBar, Select, Spinner, tokens } from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  CameraRegular,
  CheckmarkCircleRegular,
  MicRegular,
  Speaker2Regular,
  VideoOffRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import { useEffect, useState, useSyncExternalStore } from 'react';
import { Notice } from '@/components/States';
import { useT, type MessageKey } from '@/i18n';
import { detectPlatform, isInAppBrowser, type MediaProblem } from './media';
import { MediaPreview } from './MediaPreview';
import { VideoView } from './VideoView';

export interface DeviceChoice {
  audioDeviceId: string | null;
  videoDeviceId: string | null;
  audioOnly: boolean;
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (min-width: 900px)': { padding: '24px' },
  },
  heading: { margin: 0, fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  lead: { margin: 0, color: tokens.colorNeutralForeground2 },
  layout: {
    display: 'grid',
    gap: '16px',
    '@media (min-width: 760px)': { gridTemplateColumns: 'minmax(0, 1.2fr) minmax(0, 1fr)', alignItems: 'start' },
  },
  previewBox: {
    position: 'relative',
    width: '100%',
    aspectRatio: '4 / 3',
    borderRadius: tokens.borderRadiusXLarge,
    overflow: 'hidden',
    backgroundColor: '#1b1b1b',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  preview: { width: '100%', height: '100%', objectFit: 'cover' },
  noCamera: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    color: '#e8e8e8',
    fontSize: tokens.fontSizeBase400,
  },
  noCameraIcon: { fontSize: '40px' },
  panel: { display: 'flex', flexDirection: 'column', gap: '14px' },
  meter: { display: 'flex', flexDirection: 'column', gap: '6px' },
  meterLabel: { display: 'flex', alignItems: 'center', gap: '8px', fontWeight: tokens.fontWeightSemibold },
  heard: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    color: tokens.colorStatusSuccessForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  hint: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  tips: {
    margin: 0,
    paddingLeft: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    color: tokens.colorNeutralForeground2,
  },
  tipsTitle: { margin: 0, fontWeight: tokens.fontWeightSemibold },
  actions: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    '@media (min-width: 520px)': { flexDirection: 'row', flexWrap: 'wrap' },
  },
  starting: { display: 'flex', justifyContent: 'center', padding: '32px 0' },
});

const PROBLEM_TEXT: Record<Exclude<MediaProblem, 'denied'>, { title: MessageKey; body: MessageKey }> = {
  notFound: { title: 'video.error.notFound.title', body: 'video.error.notFound.body' },
  inUse: { title: 'video.error.inUse.title', body: 'video.error.inUse.body' },
  insecure: { title: 'video.error.insecure.title', body: 'video.error.insecure.body' },
  unsupported: { title: 'video.error.unsupported.title', body: 'video.error.unsupported.body' },
  unknown: { title: 'video.error.unknown.title', body: 'video.error.unknown.body' },
};

function deniedHelp(): MessageKey {
  const platform =
    typeof navigator === 'undefined' ? 'desktop' : detectPlatform(navigator.userAgent, navigator.maxTouchPoints);
  return platform === 'android'
    ? 'video.error.denied.android'
    : platform === 'ios'
      ? 'video.error.denied.ios'
      : 'video.error.denied.desktop';
}

/**
 * Before joining: allow the camera and microphone, see yourself, check you can be heard and that the speaker works.
 * Every failure explains exactly how to fix it on this kind of device.
 */
export function DeviceCheck({ onJoin, joinLabel }: { onJoin: (choice: DeviceChoice) => void; joinLabel?: string }) {
  const styles = useStyles();
  const t = useT();
  const [preview] = useState(() => new MediaPreview());
  const state = useSyncExternalStore(preview.subscribe, preview.getSnapshot, preview.getSnapshot);
  const [inApp] = useState(() => typeof navigator !== 'undefined' && isInAppBrowser(navigator.userAgent));

  useEffect(() => () => preview.stop(), [preview]);

  const join = (audioOnly: boolean) => {
    const choice: DeviceChoice = { audioDeviceId: state.audioDeviceId, videoDeviceId: state.videoDeviceId, audioOnly };
    preview.stop();
    onJoin(choice);
  };

  return (
    <section className={styles.root} aria-labelledby="device-check-title">
      <h2 id="device-check-title" className={styles.heading}>
        {t('video.check.title')}
      </h2>
      <p className={styles.lead}>{t('video.check.body')}</p>
      {inApp && <Notice intent="warning">{t('video.error.inApp')}</Notice>}

      {state.status === 'idle' && (
        <div className={styles.actions}>
          <Button appearance="primary" size="large" icon={<VideoRegular />} onClick={() => void preview.start()}>
            {t('video.check.start')}
          </Button>
        </div>
      )}

      {state.status === 'requesting' && (
        <div className={styles.starting}>
          <Spinner size="large" label={t('video.check.starting')} labelPosition="below" />
        </div>
      )}

      {state.status === 'error' && state.problem && (
        <>
          <Notice
            intent="error"
            title={t(state.problem === 'denied' ? 'video.error.denied.title' : PROBLEM_TEXT[state.problem].title)}
          >
            {t(state.problem === 'denied' ? deniedHelp() : PROBLEM_TEXT[state.problem].body)}
          </Notice>
          <div className={styles.actions}>
            <Button
              appearance="primary"
              size="large"
              icon={<ArrowClockwiseRegular />}
              onClick={() => void preview.start()}
            >
              {t('common.retry')}
            </Button>
            {state.problem !== 'insecure' && state.problem !== 'unsupported' && (
              <Button size="large" onClick={() => join(true)}>
                {joinLabel ?? t('video.check.join')}
              </Button>
            )}
          </div>
        </>
      )}

      {state.status === 'ready' && (
        <div className={styles.layout}>
          <div className={styles.previewBox}>
            {state.hasVideo ? (
              <VideoView
                stream={state.stream}
                muted
                mirrored
                className={styles.preview}
                label={t('video.check.preview')}
              />
            ) : (
              <span className={styles.noCamera}>
                <VideoOffRegular className={styles.noCameraIcon} aria-hidden />
                {t('video.check.noCamera')}
              </span>
            )}
          </div>
          <div className={styles.panel}>
            {!state.hasVideo && <Notice intent="warning">{t('video.check.cameraProblem')}</Notice>}
            {state.hasAudio && (
              <div className={styles.meter}>
                <span className={styles.meterLabel}>
                  <MicRegular aria-hidden />
                  {t('video.check.micLevel')}
                </span>
                <ProgressBar
                  value={state.level}
                  max={1}
                  thickness="large"
                  shape="rounded"
                  aria-label={t('video.check.micLevel')}
                />
                {state.heard ? (
                  <span className={styles.heard} role="status">
                    <CheckmarkCircleRegular aria-hidden />
                    {t('video.check.heard')}
                  </span>
                ) : (
                  <p className={styles.hint}>{t('video.check.speak')}</p>
                )}
              </div>
            )}
            {state.cameras.length > 1 && (
              <Field label={t('video.check.camera')}>
                <Select
                  value={state.videoDeviceId ?? ''}
                  onChange={(_, data) => void preview.changeDevice('video', data.value)}
                >
                  {state.cameras.map((camera, index) => (
                    <option key={camera.deviceId} value={camera.deviceId}>
                      {camera.label || `${t('video.check.camera')} ${index + 1}`}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            {state.microphones.length > 1 && (
              <Field label={t('video.check.microphone')}>
                <Select
                  value={state.audioDeviceId ?? ''}
                  onChange={(_, data) => void preview.changeDevice('audio', data.value)}
                >
                  {state.microphones.map((microphone, index) => (
                    <option key={microphone.deviceId} value={microphone.deviceId}>
                      {microphone.label || `${t('video.check.microphone')} ${index + 1}`}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            <div>
              <Button icon={<Speaker2Regular />} onClick={() => preview.playTestSound()}>
                {t('video.check.testSound')}
              </Button>
              <p className={styles.hint}>{t('video.check.testSoundHint')}</p>
            </div>
            <div>
              <p className={styles.tipsTitle}>{t('video.check.tipsTitle')}</p>
              <ul className={styles.tips}>
                <li>{t('video.check.tip1')}</li>
                <li>{t('video.check.tip2')}</li>
                <li>{t('video.check.tip3')}</li>
              </ul>
            </div>
            <div className={styles.actions}>
              <Button
                appearance="primary"
                size="large"
                icon={state.hasVideo ? <CameraRegular /> : <MicRegular />}
                onClick={() => join(!state.hasVideo)}
              >
                {joinLabel ?? (state.hasVideo ? t('video.check.join') : t('video.check.joinAudio'))}
              </Button>
            </div>
          </div>
        </div>
      )}
    </section>
  );
}
