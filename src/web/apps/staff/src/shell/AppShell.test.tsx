import type * as Core from '@hc360/core';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import { SHELL_SCREEN_ANSWERS } from '@/test/accessibility/fixtures/shell';

// The navigation's categories: the one of the screen shown is open, and choosing another opens it in its place.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

beforeEach(() => {
  stubBrowserApis();
  fakeServer(SHELL_SCREEN_ANSWERS);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('navigation', SCREEN_TESTS, () => {
  it('opens the category chosen, and closes the one open before', async () => {
    openScreen('/patients');
    expect(await screen.findByText('NH26000123', {}, SLOW)).toBeInTheDocument();
    const navigation = screen.getByRole('navigation', { name: 'Main navigation' });
    const patients = within(navigation).getByRole('button', { name: 'Patients' });
    const billing = within(navigation).getByRole('button', { name: 'Billing' });
    expect(patients).toHaveAttribute('aria-expanded', 'true');
    expect(billing).toHaveAttribute('aria-expanded', 'false');

    await userEvent.click(billing);

    expect(billing).toHaveAttribute('aria-expanded', 'true');
    expect(await within(navigation).findByRole('button', { name: 'OP billing' })).toBeInTheDocument();
    expect(patients).toHaveAttribute('aria-expanded', 'false');

    await userEvent.click(billing);

    expect(billing).toHaveAttribute('aria-expanded', 'false');
  });
});
