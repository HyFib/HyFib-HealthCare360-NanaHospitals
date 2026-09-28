import { useEffect, useState, useSyncExternalStore } from 'react';
import { TelehealthCall, type CallState, type TelehealthCallOptions } from './TelehealthCall';

/**
 * Joins the consultation while the component is mounted and leaves when it unmounts. The options are read once;
 * remount the component (for example with `key={sessionId}`) to join another session.
 */
export function useTelehealthCall(options: TelehealthCallOptions): [CallState, TelehealthCall] {
  const [call] = useState(() => new TelehealthCall(options));
  const state = useSyncExternalStore(call.subscribe, call.getSnapshot, call.getSnapshot);

  useEffect(() => {
    call.start();
    return () => call.stop();
  }, [call]);

  return [state, call];
}
