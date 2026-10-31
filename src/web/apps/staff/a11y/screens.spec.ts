import type { AuthUser } from '@hc360/core';
import {
  BROWSER_THEMES,
  expectAccessibleScreen,
  prepareTheme,
  serveFakeApi,
  type BrowserTheme,
  type ScreenCheckOptions,
} from '@hc360/ui/testing/browser';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { SYSTEM_HEALTH_ANSWERS } from '../src/test/accessibility/fixtures/admin';
import { ASHA, BILLING_ANSWERS, DRAFT_ID } from '../src/test/accessibility/fixtures/billing';
import { CLINICAL_ANSWERS, ENCOUNTER_ID } from '../src/test/accessibility/fixtures/clinical';
import {
  CALENDAR_ANSWERS,
  CALENDAR_MORNING,
  FRONT_DESK_HOME_ANSWERS,
  FRONT_DESK_USER,
  FRONT_OFFICE_ANSWERS,
  OPD,
} from '../src/test/accessibility/fixtures/frontOffice';
import { SHELL_SCREEN_ANSWERS } from '../src/test/accessibility/fixtures/shell';
import { createFakeFetch, paged, problem, STAFF_USER, type FakeAnswers } from '../src/test/fakeApi';

// The staff app's key screens, built and in Chromium, with canned data (SRS UX-10): axe-core with every WCAG 2.2 A/AA
// rule, colour contrast and 24 px targets included, and a walk with Tab that wants focus visible at every stop, in the
// light, dark and high-contrast themes. jsdom checks the same screens' structure (src/test/accessibility); this is what
// only painting shows. docs/architecture/FRONTEND-GUIDE.md, "Accessibility".

interface Screen {
  name: string;
  path: string;
  answers: FakeAnswers;
  /** Signed in as (STAFF_USER, a super administrator, by default), or null for signed out. */
  user?: AuthUser | null;
  /** What shows once the screen has its data. */
  ready: (page: Page) => Locator;
  check?: ScreenCheckOptions;
  /** The time the browser's clock shows, for a screen that draws what depends on the hour. */
  clock?: string;
}

const SCREENS: Screen[] = [
  {
    name: 'sign-in',
    path: '/login',
    answers: { 'GET /api/v1/system/info': SHELL_SCREEN_ANSWERS['GET /api/v1/system/info'] },
    user: null,
    ready: (page) => page.getByRole('heading', { name: 'Sign in' }),
    check: { focus: { from: 'top' } },
  },
  {
    name: "front desk's home page",
    path: '/',
    answers: FRONT_DESK_HOME_ANSWERS,
    user: FRONT_DESK_USER,
    ready: (page) => page.getByRole('heading', { name: /Kavita/ }),
  },
  {
    name: 'patient search',
    path: '/patients',
    answers: FRONT_OFFICE_ANSWERS,
    ready: (page) => page.getByText('Ravi Shankar'),
  },
  {
    name: 'patient registration',
    path: '/patients/register',
    answers: FRONT_OFFICE_ANSWERS,
    ready: (page) => page.getByRole('button', { name: 'Register and issue UHID' }),
  },
  {
    name: 'patient chart',
    path: `/patients/${ASHA.id}`,
    answers: {
      ...BILLING_ANSWERS,
      'GET /api/v1/masters/lookups/departments': [OPD],
      [`GET /api/v1/privacy/patients/${ASHA.id}/nominee`]: { nominee: null },
      'GET /api/v1/privacy/requests': paged([]),
      [`GET /api/v1/patients/${ASHA.id}/sensitive-categories`]: { categories: [], version: 1 },
      // A merge waiting for a second records officer would show here; none is waiting.
      'GET /api/v1/approvals': paged([]),
    },
    ready: (page) => page.getByText('Penicillin', { exact: false }).first(),
  },
  {
    name: 'patient queue',
    path: '/front-office/queue',
    answers: FRONT_OFFICE_ANSWERS,
    ready: (page) => page.getByText('GM-013'),
  },
  {
    name: 'appointments',
    path: '/scheduling',
    answers: FRONT_OFFICE_ANSWERS,
    ready: (page) => page.getByText('Ravi Shankar').first(),
  },
  {
    name: 'appointment calendar',
    path: `/scheduling/calendar?departmentId=${OPD.id}`,
    answers: CALENDAR_ANSWERS,
    ready: (page) => page.getByText('Ravi Shankar').first(),
    // Before the OPD opens: every slot is still free to book and the red "now" line, drawn over the slot of the
    // current minute, is not on the grid (axe cannot tell a slot's colours under it).
    clock: CALENDAR_MORNING,
  },
  {
    name: "doctor's worklist",
    path: '/emr',
    answers: CLINICAL_ANSWERS,
    ready: (page) => page.getByText('Lakshmi Narayanan'),
  },
  {
    name: 'consultation, with the patient banner',
    path: `/emr/encounters/${ENCOUNTER_ID}`,
    answers: CLINICAL_ANSWERS,
    ready: (page) => page.getByText('Dengue fever [classical dengue]'),
  },
  {
    name: 'OP billing, a draft bill',
    path: `/billing/op?patientId=${ASHA.id}&billId=${DRAFT_ID}`,
    answers: BILLING_ANSWERS,
    ready: (page) => page.getByRole('combobox', { name: 'Bill at a package' }),
  },
  {
    name: 'bills',
    path: '/billing/bills',
    answers: FRONT_OFFICE_ANSWERS,
    ready: (page) => page.getByText('OP2600012'),
  },
  {
    name: 'system health',
    path: '/admin/system',
    answers: SYSTEM_HEALTH_ANSWERS,
    ready: (page) => page.getByText('ABDM (ABHA)').first(),
  },
];

/** The patients screen, for checks of the frame around every screen: one patient, two notifications. */
const FRAME: Pick<Screen, 'path' | 'answers' | 'ready'> = {
  path: '/patients',
  answers: { ...FRONT_OFFICE_ANSWERS, ...SHELL_SCREEN_ANSWERS },
  ready: (page) => page.getByText('NH26000123'),
};

/** Opens the built app at `path` in `theme`, signed in as `user`, the API answered from `answers`. */
async function open(
  page: Page,
  theme: BrowserTheme,
  { path, answers, user = STAFF_USER, ready, clock }: Pick<Screen, 'path' | 'answers' | 'user' | 'ready' | 'clock'>,
) {
  const server = createFakeFetch({
    ...answers,
    // The session comes back from the refresh cookie on start (auth.restore), as after a reload.
    'POST /api/v1/auth/refresh': user
      ? { accessToken: 'a11y-token', expiresAt: new Date(Date.now() + 8 * 3600_000).toISOString(), user }
      : problem(401, 'Sign in again'),
    'GET /health/live': 'Healthy',
  });
  await prepareTheme(page, theme, 'stored');
  await serveFakeApi(page, server.fetch);
  if (clock) {
    await page.clock.setFixedTime(new Date(clock));
  }
  await page.goto(path);
  await expect(ready(page)).toBeVisible();
  await page.waitForLoadState('networkidle');
  // A request the fake server could not answer shows an error where the screen's content should be.
  expect(server.unanswered, 'requests without a canned answer').toEqual([]);
  return server;
}

for (const theme of BROWSER_THEMES) {
  test.describe(`${theme} theme`, () => {
    for (const screen of SCREENS) {
      test(screen.name, async ({ page }, testInfo) => {
        const server = await open(page, theme, screen);
        await expectAccessibleScreen(page, testInfo, screen.check ?? { focus: { from: 'skip-link' } });
        expect(server.unanswered, 'requests without a canned answer').toEqual([]);
      });
    }

    test('the frame: top bar and navigation', async ({ page }, testInfo) => {
      await open(page, theme, FRAME);
      await expectAccessibleScreen(page, testInfo, { focus: { from: 'top', maxStops: 60 } });
    });

    test('command palette with patients found', async ({ page }, testInfo) => {
      await open(page, theme, FRAME);
      await page.getByRole('button', { name: /Search patients or screens/ }).click();
      const palette = page.getByRole('dialog', { name: 'Search' });
      await palette.getByRole('combobox').fill('asha');
      await expect(palette.getByText('Asha Devi')).toBeVisible();
      await expectAccessibleScreen(page, testInfo, { focus: { from: 'here' } });
    });

    test('notifications', async ({ page }, testInfo) => {
      await open(page, theme, FRAME);
      await page.getByRole('button', { name: 'Notifications, 2 unread' }).click();
      await expect(page.getByText('Discount approved')).toBeVisible();
      await expectAccessibleScreen(page, testInfo, { focus: { from: 'here' } });
    });

    test('account menu', async ({ page }, testInfo) => {
      await open(page, theme, FRAME);
      await page.getByRole('button', { name: 'Account menu for Dr. Meera Iyer' }).click();
      await expect(page.getByRole('menuitemradio', { name: 'High contrast' })).toBeVisible();
      // The menu covers part of the screen, checked on its own above: axe cannot tell the colours under it.
      await expectAccessibleScreen(page, testInfo, { include: '[role="menu"]', focus: false });
    });
  });
}

test.describe('tablet, 768 px wide', () => {
  test.use({ viewport: { width: 768, height: 1024 } });

  for (const screen of SCREENS.filter((s) =>
    ['patient registration', 'patient queue', "doctor's worklist", 'OP billing, a draft bill'].includes(s.name),
  )) {
    test(screen.name, async ({ page }, testInfo) => {
      await open(page, 'light', screen);
      await expectAccessibleScreen(page, testInfo, { focus: { from: 'skip-link' } });
    });
  }

  test('navigation opened over the screen', async ({ page }, testInfo) => {
    await open(page, 'light', SCREENS[2]!);
    await page.getByRole('button', { name: 'Show navigation' }).click();
    await expect(page.getByRole('dialog', { name: 'Main navigation' })).toBeVisible();
    await expectAccessibleScreen(page, testInfo, { focus: { from: 'here' } });
  });
});
