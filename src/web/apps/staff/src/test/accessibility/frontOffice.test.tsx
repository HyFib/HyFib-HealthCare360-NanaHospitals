import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '../appScreen';
import { FRONT_OFFICE_ANSWERS } from './fixtures/frontOffice';

// The front desk's screens, whole (shell included) with canned data, checked with axe-core for WCAG 2.2 A and AA
// problems (SRS UX-10). Colour contrast and target sizes are checked in a browser instead (FRONTEND-GUIDE.md).

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  server = fakeServer(FRONT_OFFICE_ANSWERS);
});

afterEach(() => {
  // A screen whose request went unanswered would show an error where its content should be, and hide that from axe.
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('front office screens', SCREEN_TESTS, () => {
  it('patient search', async () => {
    openScreen('/patients');
    expect(await screen.findByText('Ravi Shankar', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('patient registration, before and after a save with fields missing', async () => {
    openScreen('/patients/register');
    const save = await screen.findByRole('button', { name: 'Register and issue UHID' }, SLOW);
    await expectNoAxeViolations();

    await userEvent.click(save);
    expect((await screen.findAllByRole('alert', {}, SLOW)).length).toBeGreaterThan(0);
    await expectNoAxeViolations();
  });

  it('patient queue', async () => {
    openScreen('/front-office/queue');
    expect(await screen.findByText('GM-012', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('GM-013', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('appointments', async () => {
    openScreen('/scheduling');
    expect((await screen.findAllByText('Ravi Shankar', {}, SLOW)).length).toBeGreaterThan(0);
    await expectNoAxeViolations();
  });

  it('bills', async () => {
    openScreen('/billing/bills');
    expect(await screen.findByText('OP2600012', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
