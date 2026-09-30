import { ApiError } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import { assistantErrorMessage } from './errors';

function problem(status: number, code?: string, errors?: Record<string, string[]>) {
  return new ApiError({ status, code, title: 'Raw server title', detail: 'Raw server detail with internals', errors });
}

describe('assistantErrorMessage', () => {
  it('turns the AI problem codes into plain language', () => {
    expect(assistantErrorMessage(problem(503, 'ai.disabled'))).toContain('AI help is turned off');
    expect(assistantErrorMessage(problem(503, 'ai.speech_disabled'))).toContain('Voice input is not set up');
    expect(assistantErrorMessage(problem(429, 'ai.quota'))).toContain('usage limit');
    expect(assistantErrorMessage(problem(502, 'ai.provider_failed'))).toContain('could not help this time');
  });

  it('explains the rate limit, permissions and the network', () => {
    expect(assistantErrorMessage(problem(429))).toContain('Wait a minute');
    expect(assistantErrorMessage(problem(403))).toContain('permission');
    expect(assistantErrorMessage(new ApiError({ status: 0, code: 'network', title: 'Cannot reach' }))).toContain(
      'WiFi',
    );
  });

  it('passes through staff-facing validation messages', () => {
    expect(assistantErrorMessage(problem(400, undefined, { text: ['Say or type a command.'] }))).toBe(
      'Say or type a command.',
    );
  });

  it('never shows raw details', () => {
    for (const error of [problem(500), problem(418), new Error('TypeError: x is undefined'), 'boom']) {
      const message = assistantErrorMessage(error);
      expect(message).not.toContain('Raw server');
      expect(message).not.toContain('TypeError');
    }
  });
});
