import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { CameraRegular } from '@fluentui/react-icons';
import { useEffect, useRef, useState } from 'react';
import { cameraProblem } from './photo';

const useStyles = makeStyles({
  stage: {
    position: 'relative',
    width: '100%',
    aspectRatio: '4 / 3',
    backgroundColor: tokens.colorNeutralBackground6,
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
    display: 'grid',
    placeItems: 'center',
  },
  video: { position: 'absolute', inset: 0, width: '100%', height: '100%', objectFit: 'cover' },
});

type CameraState = { status: 'starting' } | { status: 'live' } | { status: 'failed'; message: string };

/**
 * Takes a photo with the workstation's camera. The camera is on only while this dialog is open. The captured frame is
 * handed over as an ImageBitmap (its new owner closes it); nothing is stored here.
 */
export function PhotoCaptureDialog({ open, onClose, onCaptured }: { open: boolean; onClose: () => void; onCaptured: (frame: ImageBitmap) => void }) {
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Take a photo</DialogTitle>
          {/* Mounted only while open, so the camera starts and stops with the dialog. */}
          {open && <CameraView onClose={onClose} onCaptured={onCaptured} />}
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** The camera preview and its buttons; separate from the dialog so it can be tested on its own. */
export function CameraView({ onClose, onCaptured }: { onClose: () => void; onCaptured: (frame: ImageBitmap) => void }) {
  const styles = useStyles();
  const videoRef = useRef<HTMLVideoElement>(null);
  const [state, setState] = useState<CameraState>(() =>
    // mediaDevices is missing on a page that is not served over HTTPS, although its type says otherwise.
    typeof navigator !== 'undefined' && typeof navigator.mediaDevices?.getUserMedia === 'function'
      ? { status: 'starting' }
      : { status: 'failed', message: 'This browser cannot use the camera. Upload a photo instead.' },
  );
  const [capturing, setCapturing] = useState(false);

  useEffect(() => {
    if (state.status === 'failed') {
      return;
    }
    let cancelled = false;
    let stream: MediaStream | null = null;
    navigator.mediaDevices
      .getUserMedia({ video: { facingMode: 'user', width: { ideal: 1280 }, height: { ideal: 960 } }, audio: false })
      .then((opened) => {
        if (cancelled) {
          opened.getTracks().forEach((track) => track.stop());
          return;
        }
        stream = opened;
        if (videoRef.current) {
          videoRef.current.srcObject = opened;
        }
        setState({ status: 'live' });
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setState({ status: 'failed', message: cameraProblem(error) });
        }
      });

    return () => {
      cancelled = true;
      stream?.getTracks().forEach((track) => track.stop());
    };
    // The camera is opened once, when the dialog opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const capture = async () => {
    const video = videoRef.current;
    // Zero until the camera has delivered its first picture.
    if (!video || !video.videoWidth) {
      return;
    }
    setCapturing(true);
    try {
      // The frame is taken before the dialog closes and the camera stops.
      onCaptured(await createImageBitmap(video));
      onClose();
    } catch {
      setState({ status: 'failed', message: 'The photo could not be taken. Try again, or upload a photo instead.' });
    } finally {
      setCapturing(false);
    }
  };

  return (
    <>
      <DialogContent>
        {state.status === 'failed' ? (
          <MessageBar intent="error" layout="multiline" politeness="assertive">
            <MessageBarBody>{state.message}</MessageBarBody>
          </MessageBar>
        ) : (
          <div className={styles.stage}>
            <video ref={videoRef} className={styles.video} autoPlay muted playsInline aria-label="Camera preview" />
            {state.status === 'starting' && <Spinner label="Starting the camera…" />}
          </div>
        )}
      </DialogContent>
      <DialogActions>
        <Button appearance="secondary" onClick={onClose}>
          Cancel
        </Button>
        <Button appearance="primary" icon={<CameraRegular />} disabled={state.status !== 'live' || capturing} onClick={() => void capture()}>
          Take photo
        </Button>
      </DialogActions>
    </>
  );
}
