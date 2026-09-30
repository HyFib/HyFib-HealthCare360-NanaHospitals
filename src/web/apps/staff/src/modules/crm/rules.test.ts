import { describe, expect, it } from 'vitest';
import {
  campaignActions,
  inSendingWindow,
  isLeadOpen,
  leadMoves,
  outcomeClosesTask,
  outcomeNeedsNextDate,
  percent,
  smsLength,
} from './rules';

describe('lead workflow', () => {
  it('offers only the moves the server allows', () => {
    expect(leadMoves('New')).toEqual(['Contacted', 'Qualified', 'Lost']);
    expect(leadMoves('Qualified')).toEqual(['New', 'Contacted', 'Lost']);
    expect(leadMoves('Lost')).toEqual(['New', 'Contacted']);
    expect(leadMoves('Converted')).toEqual([]);
    expect(leadMoves('New')).not.toContain('Converted');
  });

  it('knows open leads', () => {
    expect(isLeadOpen('Contacted')).toBe(true);
    expect(isLeadOpen('Lost')).toBe(false);
    expect(isLeadOpen('Converted')).toBe(false);
  });
});

describe('follow-up calls', () => {
  it('closes the task for final outcomes', () => {
    expect(outcomeClosesTask('Reached', 0)).toBe(true);
    expect(outcomeClosesTask('AppointmentBooked', 1)).toBe(true);
    expect(outcomeClosesTask('WrongNumber', 0)).toBe(true);
  });

  it('keeps retrying until the third unanswered call', () => {
    expect(outcomeClosesTask('NotReachable', 0)).toBe(false);
    expect(outcomeClosesTask('NotReachable', 1)).toBe(false);
    expect(outcomeClosesTask('NotReachable', 2)).toBe(true);
    expect(outcomeNeedsNextDate('NotReachable', 0)).toBe(true);
  });

  it('never closes on a call-back request', () => {
    expect(outcomeClosesTask('CallBackLater', 5)).toBe(false);
    expect(outcomeNeedsNextDate('CallBackLater', 5)).toBe(true);
  });
});

describe('campaigns', () => {
  it('separates drafting from sending', () => {
    expect(campaignActions('Draft', { manage: true, send: false })).toEqual(['edit']);
    expect(campaignActions('Draft', { manage: false, send: true })).toEqual(['schedule', 'cancel']);
    expect(campaignActions('Scheduled', { manage: true, send: true })).toEqual(['unschedule', 'cancel']);
    expect(campaignActions('Sending', { manage: true, send: true })).toEqual(['cancel']);
    expect(campaignActions('Completed', { manage: true, send: true })).toEqual([]);
  });

  it('knows the TRAI promotional window', () => {
    expect(inSendingWindow('2026-10-01T09:59')).toBe(false);
    expect(inSendingWindow('2026-10-01T10:00')).toBe(true);
    expect(inSendingWindow('2026-10-01T20:59')).toBe(true);
    expect(inSendingWindow('2026-10-01T21:00')).toBe(false);
    expect(inSendingWindow('')).toBe(false);
  });
});

describe('SMS length', () => {
  it('counts plain English in 160-character parts', () => {
    expect(smsLength('')).toEqual({ characters: 0, parts: 0, unicode: false, perPart: 160 });
    expect(smsLength('a'.repeat(160)).parts).toBe(1);
    expect(smsLength('a'.repeat(161))).toEqual({ characters: 161, parts: 2, unicode: false, perPart: 153 });
  });

  it('counts extension characters twice', () => {
    expect(smsLength('Fee: €100 [OPD]').characters).toBe(18);
  });

  it('switches to Unicode for Indian scripts', () => {
    const tamil = smsLength('வணக்கம்');
    expect(tamil.unicode).toBe(true);
    expect(tamil.parts).toBe(1);
    expect(smsLength('न'.repeat(71))).toEqual({ characters: 71, parts: 2, unicode: true, perPart: 67 });
  });

  it('formats percentages', () => {
    expect(percent(1, 8)).toBe('12.5%');
    expect(percent(1, 0)).toBe('—');
  });
});
