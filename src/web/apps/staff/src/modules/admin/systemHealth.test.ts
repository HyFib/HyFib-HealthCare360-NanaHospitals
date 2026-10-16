import { describe, expect, it } from 'vitest';
import type { IntegrationHealthSummary } from './api';
import { expiryTone, integrationsSummary, integrationTone } from './systemHealth';

function row(name: string, health: string, enabled = true, configured = true): IntegrationHealthSummary {
  return {
    name,
    displayName: name.toUpperCase(),
    enabled,
    configured,
    health,
    lastSuccessAt: null,
    lastFailureAt: null,
    failuresLast24Hours: 0,
  };
}

describe('expiryTone', () => {
  const now = new Date('2026-10-16T09:00:00Z');

  it('is red once expired and amber within 30 days', () => {
    expect(expiryTone('2026-10-16T08:59:59Z', now)).toBe('bad');
    expect(expiryTone('2026-11-14T09:00:00Z', now)).toBe('warn');
  });

  it('says nothing further out, or without a date', () => {
    expect(expiryTone('2026-11-16T09:00:00Z', now)).toBeUndefined();
    expect(expiryTone(null, now)).toBeUndefined();
  });
});

describe('integrationsSummary', () => {
  it('counts failing, troubled, working and switched-off integrations, leaving out those not set up', () => {
    expect(
      integrationsSummary([
        row('sms', 'failing'),
        row('tally', 'degraded'),
        row('email', 'healthy'),
        row('icd', 'idle'),
        row('pacs', 'disabled', false),
        row('pos', 'not_configured', false, false),
      ]),
    ).toEqual({ text: '1 failing, 1 with failures in 24 h, 2 working, 1 turned off', tone: 'bad' });
  });

  it('is amber with failures today but nothing failing now, and green when all work', () => {
    expect(integrationsSummary([row('sms', 'degraded'), row('email', 'healthy')]).tone).toBe('warn');
    expect(integrationsSummary([row('email', 'healthy'), row('labels', 'idle')])).toEqual({
      text: '2 working',
      tone: 'ok',
    });
  });

  it('has no tone when nothing is on', () => {
    expect(integrationsSummary([row('pos', 'not_configured', false, false)])).toEqual({
      text: 'None set up',
      tone: undefined,
    });
    expect(integrationsSummary([row('pacs', 'disabled', false)])).toEqual({ text: '1 turned off', tone: undefined });
  });
});

describe('integrationTone', () => {
  it('colours only integrations that are switched on', () => {
    expect(integrationTone(row('sms', 'failing'))).toBe('bad');
    expect(integrationTone(row('sms', 'degraded'))).toBe('warn');
    expect(integrationTone(row('sms', 'healthy'))).toBe('ok');
    expect(integrationTone(row('sms', 'idle'))).toBeUndefined();
    expect(integrationTone(row('sms', 'failing', false))).toBeUndefined();
  });
});
