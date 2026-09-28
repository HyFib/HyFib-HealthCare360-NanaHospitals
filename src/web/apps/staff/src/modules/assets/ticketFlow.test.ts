import { describe, expect, it } from 'vitest';
import { dueLabel, formatDowntime, isRetired, isTicketOpen, primaryTicketAction, ticketActions } from './ticketFlow';

describe('breakdown ticket workflow', () => {
  it('offers the next step for each state like the server allows', () => {
    expect(ticketActions('Open')).toEqual(['assign', 'start', 'resolve']);
    expect(primaryTicketAction('Assigned')).toBe('start');
    expect(primaryTicketAction('InProgress')).toBe('resolve');
    expect(ticketActions('Resolved')).toEqual(['close', 'reopen']);
    expect(ticketActions('Closed')).toEqual([]);
    expect(primaryTicketAction('Closed')).toBeNull();
    expect(isTicketOpen('Assigned')).toBe(true);
    expect(isTicketOpen('Resolved')).toBe(false);
  });

  it('formats downtime compactly', () => {
    expect(formatDowntime(45)).toBe('45 min');
    expect(formatDowntime(200)).toBe('3 h 20 min');
    expect(formatDowntime(120)).toBe('2 h');
    expect(formatDowntime(3120)).toBe('2 d 4 h');
    expect(formatDowntime(null)).toBe('—');
  });

  it('describes due dates and retired assets', () => {
    expect(dueLabel(-3)).toBe('Overdue by 3 days');
    expect(dueLabel(-1)).toBe('Overdue by 1 day');
    expect(dueLabel(0)).toBe('Due today');
    expect(dueLabel(5)).toBe('Due in 5 days');
    expect(isRetired('Condemned')).toBe(true);
    expect(isRetired('Idle')).toBe(false);
  });
});
