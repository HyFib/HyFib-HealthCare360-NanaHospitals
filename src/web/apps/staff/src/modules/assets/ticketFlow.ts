import type { AssetStatus, TicketStatus } from './labels';

/** What a biomedical engineer can do with a breakdown ticket in each state (MaintenanceTicket rules). */
export type TicketAction = 'assign' | 'start' | 'resolve' | 'close' | 'reopen';

export function isTicketOpen(status: TicketStatus): boolean {
  return status === 'Open' || status === 'Assigned' || status === 'InProgress';
}

/** Actions allowed on a ticket, in the order they are usually taken. */
export function ticketActions(status: TicketStatus): TicketAction[] {
  switch (status) {
    case 'Open':
      return ['assign', 'start', 'resolve'];
    case 'Assigned':
      return ['start', 'assign', 'resolve'];
    case 'InProgress':
      return ['resolve', 'assign'];
    case 'Resolved':
      return ['close', 'reopen'];
    case 'Closed':
      return [];
  }
}

/** The main next step shown as the primary button. */
export function primaryTicketAction(status: TicketStatus): TicketAction | null {
  return ticketActions(status)[0] ?? null;
}

export const ticketActionLabel: Record<TicketAction, string> = {
  assign: 'Assign engineer',
  start: 'Start work',
  resolve: 'Mark resolved',
  close: 'Close ticket',
  reopen: 'Reopen',
};

/** Downtime as "3 h 20 min" / "2 d 4 h" for tickets (minutes from the server). */
export function formatDowntime(minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined) {
    return '—';
  }
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    const rest = minutes % 60;
    return rest ? `${hours} h ${rest} min` : `${hours} h`;
  }
  const days = Math.floor(hours / 24);
  const restHours = hours % 24;
  return restHours ? `${days} d ${restHours} h` : `${days} d`;
}

/** Asset life cycle: condemned or disposed assets can no longer be edited, repaired or put back in use. */
export function isRetired(status: AssetStatus): boolean {
  return status === 'Condemned' || status === 'Disposed';
}

/** Due date wording for PM, calibration, warranty and contracts: "Overdue by 3 days" / "Due today" / "Due in 5 days". */
export function dueLabel(daysToDue: number): string {
  if (daysToDue < 0) {
    return `Overdue by ${-daysToDue} ${daysToDue === -1 ? 'day' : 'days'}`;
  }
  if (daysToDue === 0) {
    return 'Due today';
  }
  return `Due in ${daysToDue} ${daysToDue === 1 ? 'day' : 'days'}`;
}
