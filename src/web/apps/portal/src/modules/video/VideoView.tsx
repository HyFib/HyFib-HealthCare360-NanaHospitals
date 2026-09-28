import { useEffect, useEffectEvent, useRef, type CSSProperties } from 'react';

/**
 * A <video> bound to a MediaStream. Phones may refuse to start sound without a fresh tap; `onPlayBlocked` then lets
 * the screen show a "tap to hear" button, and `playToken` retries playback when it changes.
 */
export function VideoView({
  stream,
  muted,
  mirrored,
  className,
  style,
  label,
  onPlayBlocked,
  playToken = 0,
}: {
  stream: MediaStream | null;
  muted?: boolean;
  mirrored?: boolean;
  className?: string;
  style?: CSSProperties;
  label?: string;
  onPlayBlocked?: () => void;
  playToken?: number;
}) {
  const ref = useRef<HTMLVideoElement>(null);
  const blocked = useEffectEvent(() => onPlayBlocked?.());

  useEffect(() => {
    const element = ref.current;
    if (!element) {
      return;
    }
    if (element.srcObject !== stream) {
      element.srcObject = stream;
    }
    if (stream) {
      element.play().catch(() => blocked());
    }
  }, [stream, playToken]);

  return (
    <video
      ref={ref}
      autoPlay
      playsInline
      muted={muted}
      className={className}
      style={{ ...style, transform: mirrored ? 'scaleX(-1)' : undefined }}
      aria-label={label}
    />
  );
}
