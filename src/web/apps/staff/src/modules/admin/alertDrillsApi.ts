import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { NotificationStatus } from './notificationTemplatesApi';

/** A message a drill's warning went out in, as the message log has it now. */
export interface AlertDrillMessage {
  channel: 'Sms' | 'WhatsApp' | 'Email' | 'InApp';
  /** The number, masked. */
  recipient: string | null;
  status: NotificationStatus;
  queuedAt: string;
  sentAt: string | null;
  /** When the provider's delivery receipt said it reached the phone. */
  deliveredAt: string | null;
  error: string | null;
}

/** An alert drill (audit P2): a test critical warning, and when it got how far. */
export interface AlertDrill {
  id: string;
  /** What the warning, the SMS and the in-app notification quote. */
  code: string;
  startedAt: string;
  startedBy: string | null;
  state: 'open' | 'acknowledged' | 'expired';
  /** When the alerts told it: in the app, and to IT on-call. */
  toldAt: string | null;
  /** How many had it in the app. */
  toldTo: number;
  messages: AlertDrillMessage[];
  firstReadAt: string | null;
  acknowledgedAt: string | null;
  acknowledgedBy: string | null;
}

const DRILLS = '/api/v1/admin/system/alert-drills';

export const alertDrillQueries = {
  list: () =>
    queryOptions({
      queryKey: ['admin', 'alert-drills'],
      queryFn: ({ signal }) => api.get<AlertDrill[]>(DRILLS, { signal }),
    }),
};

export const startAlertDrill = () => api.post<AlertDrill>(DRILLS);

export const acknowledgeAlertDrill = (id: string) => api.post<AlertDrill>(`${DRILLS}/${id}/acknowledge`);

/** How long after the drill started something happened: "40 s", "4 min", "1 h 5 min". */
export function after(startedAt: string, at: string): string {
  const seconds = Math.max(0, Math.round((Date.parse(at) - Date.parse(startedAt)) / 1000));
  if (seconds < 60) {
    return `${seconds} s`;
  }
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const rest = minutes % 60;
  return rest === 0 ? `${Math.floor(minutes / 60)} h` : `${Math.floor(minutes / 60)} h ${rest} min`;
}

export interface DrillStep {
  label: string;
  /** How long after the start, or what stands instead: "not yet", "never", the provider's error. */
  when: string;
  tone: 'done' | 'waiting' | 'missed';
}

const CHANNEL: Record<AlertDrillMessage['channel'], string> = {
  Sms: 'SMS',
  WhatsApp: 'WhatsApp',
  Email: 'E-mail',
  InApp: 'In the app',
};

/**
 * The drill's steps in order, from the alerts telling it to someone acknowledging it: what a hospital looks at to see
 * that the chain works and how long it takes, or where it breaks.
 */
export function drillSteps(drill: AlertDrill): DrillStep[] {
  const open = drill.state === 'open';
  const step = (label: string, at: string | null, detail?: string): DrillStep =>
    at
      ? { label, when: `after ${after(drill.startedAt, at)}${detail ? ` (${detail})` : ''}`, tone: 'done' }
      : { label, when: open ? 'not yet' : 'never', tone: open ? 'waiting' : 'missed' };

  const steps = [step('Told in the app', drill.toldAt, `${drill.toldTo} ${drill.toldTo === 1 ? 'person' : 'people'}`)];
  if (drill.toldAt && drill.messages.length === 0) {
    steps.push({ label: 'Sent to IT on-call', when: 'no on-call number was set', tone: 'missed' });
  }
  for (const message of drill.messages) {
    const to = `${CHANNEL[message.channel]} to ${message.recipient ?? 'IT on-call'}`;
    if (message.status === 'Failed' || message.status === 'Skipped' || message.status === 'Suppressed') {
      steps.push({
        label: to,
        when: `${message.status.toLowerCase()}${message.error ? `: ${message.error}` : ''}`,
        tone: 'missed',
      });
      continue;
    }
    steps.push(step(`${to}, sent`, message.sentAt));
    steps.push(step(`${to}, delivered`, message.deliveredAt));
  }
  steps.push(step('Read in the app', drill.firstReadAt));
  steps.push(step('Acknowledged', drill.acknowledgedAt, drill.acknowledgedBy ?? undefined));
  return steps;
}
