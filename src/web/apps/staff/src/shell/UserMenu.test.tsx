import type * as Core from '@hc360/core';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import { SHELL_SCREEN_ANSWERS } from '@/test/accessibility/fixtures/shell';

// Signing out from the account menu: the sign-in page, once.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('signing out', SCREEN_TESTS, () => {
  it('goes to the sign-in page once, instead of sending the sign-in page to itself', async () => {
    const server = fakeServer({ ...SHELL_SCREEN_ANSWERS, 'POST /api/v1/auth/logout': null });
    const { router } = openScreen('/patients');
    expect(await screen.findByText('NH26000123', {}, SLOW)).toBeInTheDocument();
    const entries = router.history.length;

    await userEvent.click(screen.getByRole('button', { name: 'Account menu for Dr. Meera Iyer' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: /Sign out/ }));

    expect(await screen.findByLabelText(/User name/, {}, SLOW)).toBeInTheDocument();
    // Settled where it went: no move from the sign-in page to itself ("?redirect=/login?…").
    await new Promise((settle) => setTimeout(settle, 500));
    const { pathname, search } = router.state.location;
    expect(pathname).toBe('/login');
    expect(String((search as { redirect?: string }).redirect ?? '')).not.toContain('/login');
    expect(router.history.length).toBeLessThanOrEqual(entries + 1);
    expect(server.requests.filter((r) => r.path === '/api/v1/auth/logout')).toHaveLength(1);
  });
});
