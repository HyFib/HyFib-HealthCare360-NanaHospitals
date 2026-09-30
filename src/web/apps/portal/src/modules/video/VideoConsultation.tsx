import { useState, type ReactNode } from 'react';
import type { IceServerDto } from '@/lib/types';
import { CallScreen } from './CallScreen';
import { DeviceCheck, type DeviceChoice } from './DeviceCheck';
import type { PatientCallOptions } from './PatientCall';

/** The hub path used when the join information is not available (guests joining from the SMS link). */
export const TELEHEALTH_HUB_PATH = '/hubs/telehealth';

/** Device check first, then the call; "Join again" returns to the device check. */
export function VideoConsultation({
  sessionId,
  hubPath = TELEHEALTH_HUB_PATH,
  identity,
  iceServers,
  endActions,
}: {
  sessionId: string;
  hubPath?: string;
  identity: PatientCallOptions['identity'];
  iceServers?: IceServerDto[];
  endActions?: ReactNode;
}) {
  const [attempt, setAttempt] = useState<{ number: number; choice: DeviceChoice } | null>(null);

  if (!attempt) {
    return (
      <DeviceCheck onJoin={(choice) => setAttempt((previous) => ({ number: (previous?.number ?? 0) + 1, choice }))} />
    );
  }
  return (
    <CallScreen
      key={attempt.number}
      sessionId={sessionId}
      hubPath={hubPath}
      identity={identity}
      iceServers={iceServers}
      choice={attempt.choice}
      onRejoin={() => setAttempt(null)}
      endActions={endActions}
    />
  );
}
