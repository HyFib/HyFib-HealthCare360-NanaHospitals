import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { after, type AlertDrillMessage } from './alertDrillsApi';

/** Who an alert has reached, in the order it is escalated while nobody acknowledges it. */
export type AlertLevel = 'OnCall' | 'SecondContact' | 'Support';

/** A message an alert went out in, as the message log has it now. */
export interface AlertMessage extends AlertDrillMessage {
  level: AlertLevel;
}

/** A critical System health warning while it lasted (audit P2): who it reached, who acknowledged it, and when. */
export interface AlertIncident {
  id: string;
  code: string;
  server: string | null;
  /** What the warning said when it was first told. */
  message: string;
  level: AlertLevel;
  raisedAt: string;
  toldAt: string | null;
  /** When the first message to IT on-call was delivered, by the provider's delivery report. */
  firstDeliveredAt: string | null;
  /** When the first of those who may see System health opened it in the app. */
  firstReadAt: string | null;
  secondContactAt: string | null;
  supportAt: string | null;
  acknowledgedAt: string | null;
  acknowledgedBy: string | null;
  clearedAt: string | null;
  /** From when it was told to when it was acknowledged. */
  minutesToAcknowledge: number | null;
  messages: AlertMessage[];
}

/**
 * The open alerts and those of the period, with how long they took to be acknowledged. The figures count every one;
 * `incidents` holds every open one and the latest 500 that cleared.
 */
export interface AlertReport {
  from: string;
  to: string;
  total: number;
  acknowledged: number;
  unacknowledged: number;
  reachedSecondContact: number;
  reachedSupport: number;
  medianMinutesToAcknowledge: number | null;
  ninetiethMinutesToAcknowledge: number | null;
  incidents: AlertIncident[];
}

const ALERTS = '/api/v1/admin/system/alerts';

/** How far back the report on System health goes. */
export const REPORT_DAYS = 30;

export const alertQueries = {
  report: (days = REPORT_DAYS) =>
    queryOptions({
      queryKey: ['admin', 'alerts', days],
      queryFn: ({ signal }) => api.get<AlertReport>(`${ALERTS}?days=${days}`, { signal }),
    }),
};

export const acknowledgeAlert = (id: string) =>
  api.post<AlertIncident>(`${ALERTS}/${encodeURIComponent(id)}/acknowledge`);

export const LEVEL: Record<AlertLevel, string> = {
  OnCall: 'IT on-call',
  SecondContact: 'the second contact',
  Support: 'HyFib support',
};

/** A number of minutes, as people say it: "under a minute", "12 min", "2 h 5 min". */
export function minutesText(minutes: number): string {
  if (minutes < 1) {
    return 'under a minute';
  }
  const rounded = Math.round(minutes);
  if (rounded < 60) {
    return `${rounded} min`;
  }
  const rest = rounded % 60;
  return rest === 0 ? `${Math.floor(rounded / 60)} h` : `${Math.floor(rounded / 60)} h ${rest} min`;
}

/** Where an alert stands, in a few words, and how much it needs someone. */
export function alertStatus(alert: AlertIncident): {
  text: string;
  tone: 'danger' | 'warning' | 'success' | 'informative';
} {
  if (alert.acknowledgedAt) {
    const by = alert.acknowledgedBy ? ` by ${alert.acknowledgedBy}` : '';
    const took = alert.toldAt ? ` ${after(alert.toldAt, alert.acknowledgedAt)} after it was told` : '';
    return alert.clearedAt
      ? { text: `Acknowledged${by}${took}; cleared`, tone: 'success' }
      : { text: `Acknowledged${by}${took}; still there`, tone: 'informative' };
  }
  if (alert.clearedAt) {
    return { text: `Cleared before anyone acknowledged it (it reached ${LEVEL[alert.level]})`, tone: 'warning' };
  }
  if (!alert.toldAt) {
    return { text: 'Not told yet: the alerts tell it within five minutes', tone: 'warning' };
  }
  return { text: `Not acknowledged: it has reached ${LEVEL[alert.level]}`, tone: 'danger' };
}

/** The period in figures: how many alerts, how fast they were acknowledged, how far they were escalated. */
export function reportSummary(report: AlertReport): string {
  if (report.total === 0) {
    return `No critical alert in the last ${REPORT_DAYS} days.`;
  }
  const parts = [`${report.total} critical ${report.total === 1 ? 'alert' : 'alerts'} in the last ${REPORT_DAYS} days`];
  if (report.medianMinutesToAcknowledge != null && report.ninetiethMinutesToAcknowledge != null) {
    parts.push(
      `acknowledged in ${minutesText(report.medianMinutesToAcknowledge)} at the median and ${minutesText(report.ninetiethMinutesToAcknowledge)} at the 90th percentile`,
    );
  }
  if (report.unacknowledged > 0) {
    parts.push(`${report.unacknowledged} not acknowledged`);
  }
  if (report.reachedSecondContact > 0) {
    parts.push(`${report.reachedSecondContact} reached the second contact`);
  }
  if (report.reachedSupport > 0) {
    parts.push(`${report.reachedSupport} reached HyFib support`);
  }
  return `${parts.join('; ')}.`;
}
