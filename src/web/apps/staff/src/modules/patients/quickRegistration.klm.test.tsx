import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FRONT_OFFICE_ANSWERS } from '@/test/accessibility/fixtures/frontOffice';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import { keystrokeLevelModel, keystrokes } from '@/test/klm';

// SRS FO-01: quick registration within 60 seconds. The registration screen is driven for a typical walk-in patient
// (title, first and last name, sex, age, mobile number) and every action is counted as Keystroke-Level Model
// operators; the model's time for an average typist must be within 60 s. A model, not a stopwatch: the desk timing at
// acceptance stays the hospital's check (docs/testing/QUICK-REGISTRATION-KLM.md).

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

/** FO-01's limit for a quick registration, in seconds. */
const QUICK_REGISTRATION_SECONDS = 60;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('keystrokes are counted as typed', () => {
  it('counts characters, named keys and held keys once, releases not at all', () => {
    expect(keystrokes('Sunita')).toBe(6);
    expect(keystrokes('{Tab}{Tab}')).toBe(2);
    expect(keystrokes('{Control>}s{/Control}')).toBe(2);
  });
});

describe('quick registration (FO-01)', SCREEN_TESTS, () => {
  it('takes a typical walk-in patient to a UHID within 60 seconds by the Keystroke-Level Model', async () => {
    const server = fakeServer({
      ...FRONT_OFFICE_ANSWERS,
      'POST /api/v1/patients/duplicates': [],
      'POST /api/v1/patients': { id: '0192a000-0000-7000-8000-000000000009', uhid: 'NH26000301' },
    });
    const user = userEvent.setup();
    openScreen('/patients/register');
    const firstName = await screen.findByRole('textbox', { name: /^First name/ }, SLOW);
    // The screen opens with the cursor in the first name; the clerk's hands are on the keyboard.
    await waitFor(() => expect(firstName).toHaveFocus());
    const klm = keystrokeLevelModel(user, { handOn: 'keyboard' });

    await klm.step('Field: first name', () => klm.keyboard('Sunita'));
    await klm.step('Field: last name (past middle name)', () => klm.keyboard('{Tab}{Tab}Kulkarni'));
    expect(screen.getByRole('textbox', { name: /^Last name/ })).toHaveValue('Kulkarni');
    await klm.step('Decision: title', () =>
      klm.selectOption(screen.getByRole('combobox', { name: /^Title/ }) as HTMLSelectElement, 'Mrs'),
    );
    await klm.step('Decision: sex', () => klm.click(screen.getByRole('radio', { name: 'Female' })));
    await klm.step('Decision: age, not date of birth', () =>
      klm.click(screen.getByRole('radio', { name: 'Age only' })),
    );
    await klm.step('Field: age in years', async () => {
      await klm.click(await screen.findByLabelText(/^Years/));
      await klm.keyboard('46');
    });
    await klm.step('Field: mobile number', async () => {
      await klm.click(screen.getByRole('textbox', { name: /^Mobile number/ }));
      await klm.keyboard('9876543210');
    });
    await klm.step('Decision: check and save (Ctrl+S)', () => klm.keyboard('{Control>}s{/Control}'));

    // The UHID is issued: the task is done.
    const registered = await screen.findByRole('dialog', { name: 'Patient registered' }, SLOW);
    expect(within(registered).getByText('NH26000301')).toBeInTheDocument();
    expect(server.requests.find((r) => r.method === 'POST' && r.path === '/api/v1/patients')?.body).toMatchObject({
      title: 'Mrs',
      firstName: 'Sunita',
      lastName: 'Kulkarni',
      gender: 'Female',
      ageYears: 46,
    });

    const report = klm.report('FO-01 quick registration, Keystroke-Level Model');
    process.stdout.write(`${report}\n`);
    // Four fields and four decisions, each one mental preparation.
    expect(klm.counts().M).toBe(8);
    expect(klm.seconds(), report).toBeLessThanOrEqual(QUICK_REGISTRATION_SECONDS);
  });
});
