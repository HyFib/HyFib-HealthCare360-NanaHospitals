import type * as Core from '@hc360/core';
import { useSession } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { act, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '../appScreen';
import { SHELL_SCREEN_ANSWERS } from './fixtures/shell';

// The staff app's frame on a real screen: navigation, top bar, command palette, notifications, account menu, the lock
// screen and the sign-in page, checked with axe-core for WCAG 2.2 A and AA problems (SRS UX-10).

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer(SHELL_SCREEN_ANSWERS);
});

afterEach(() => {
  // A screen whose request went unanswered would show an error where its content should be, and hide that from axe.
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** The patients screen in the frame, with its rows on screen. */
async function openPatients() {
  openScreen('/patients');
  expect(await screen.findByText('NH26000123', {}, SLOW)).toBeInTheDocument();
}

describe('the staff app frame', SCREEN_TESTS, () => {
  it('navigation and top bar', async () => {
    await openPatients();
    expect(screen.getByRole('navigation', { name: 'Main navigation' })).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('command palette with patients and screens found', async () => {
    await openPatients();
    await userEvent.click(screen.getByRole('button', { name: /Search patients or screens/ }));
    const palette = await screen.findByRole('dialog', { name: 'Search' }, SLOW);
    await userEvent.type(within(palette).getByRole('combobox'), 'asha');
    expect(await within(palette).findByText('Asha Devi', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('command palette with nothing found', async () => {
    await openPatients();
    await userEvent.click(screen.getByRole('button', { name: /Search patients or screens/ }));
    const palette = await screen.findByRole('dialog', { name: 'Search' }, SLOW);
    await userEvent.type(within(palette).getByRole('combobox'), 'zz');
    expect(await within(palette).findByText(/No matches/, {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('notifications', async () => {
    await openPatients();
    await userEvent.click(screen.getByRole('button', { name: 'Notifications, 2 unread' }));
    expect(await screen.findByText('Discount approved', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('account menu', async () => {
    await openPatients();
    await userEvent.click(screen.getByRole('button', { name: 'Account menu for Dr. Meera Iyer' }));
    expect(await screen.findByRole('menuitemradio', { name: 'High contrast' }, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('navigation on a tablet, opened over the screen', async () => {
    const wide = window.matchMedia;
    vi.stubGlobal('matchMedia', (query: string) => ({ ...wide(query), matches: query === '(max-width: 1023px)' }));
    await openPatients();
    await userEvent.click(screen.getByRole('button', { name: 'Show navigation' }));
    const navigation = await screen.findByRole('dialog', { name: 'Main navigation' }, SLOW);
    expect(within(navigation).getByRole('button', { name: /Patients/ })).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('lock screen', async () => {
    await openPatients();
    act(() => useSession.getState().setLocked(true));
    expect(await screen.findByRole('dialog', { name: 'Screen locked' }, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('sign-in page', async () => {
    openScreen('/login', null);
    expect(await screen.findByRole('heading', { name: 'Sign in' }, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
