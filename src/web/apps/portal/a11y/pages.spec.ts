import {
  BROWSER_THEMES,
  expectAccessibleScreen,
  prepareTheme,
  serveFakeApi,
  type BrowserTheme,
  type ScreenCheckOptions,
} from '@hc360/ui/testing/browser';
import { createFakeFetch, problem } from '@hc360/ui/testing/fake-api';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { DOCTOR_ID, PORTAL_ANSWERS, SLOT_DAYS, USER } from '../src/test/fixtures';

// The patient portal's main pages, built and in Chromium, with canned data (SRS UX-10): axe-core with every WCAG 2.2
// A/AA rule, colour contrast and 24 px targets included, and a walk with Tab that wants focus visible at every stop. On
// a phone (the portal is mobile-first) in the light and dark themes and with the device's high contrast (forced
// colours), and on a computer. jsdom checks the same pages' structure (src/accessibility.test.tsx); this is what only
// painting shows. docs/architecture/FRONTEND-GUIDE.md, "Accessibility".

const PHONE = { width: 390, height: 844 };
const COMPUTER = { width: 1280, height: 800 };

interface PortalPage {
  name: string;
  path: string;
  signedIn?: boolean;
  /** What shows once the page has its data. */
  ready: (page: Page) => Locator;
  /** What a patient does on the page before it is checked, such as opening a sheet. */
  act?: (page: Page) => Promise<void>;
  check?: ScreenCheckOptions;
}

const FIRST_SLOT = SLOT_DAYS[0]!.slots[0]!.startsAt;

const PAGES: PortalPage[] = [
  {
    name: 'sign-in',
    path: '/portal/sign-in',
    signedIn: false,
    ready: (page) => page.getByRole('textbox', { name: /Mobile number/ }),
    check: { focus: { from: 'top' } },
  },
  {
    name: 'sign-in, the code sent to the phone',
    path: '/portal/sign-in',
    signedIn: false,
    ready: (page) => page.getByRole('textbox', { name: /Mobile number/ }),
    act: async (page) => {
      await page.getByRole('textbox', { name: /Mobile number/ }).fill('9876543210');
      await page.getByRole('button', { name: 'Send code' }).click();
      await expect(page.getByText(/\+91\*{6}3210/)).toBeVisible();
    },
    check: { focus: { from: 'top' } },
  },
  { name: 'home', path: '/portal/home', ready: (page) => page.getByRole('heading', { level: 1, name: /Asha/ }) },
  {
    name: 'appointments',
    path: '/portal/appointments',
    ready: (page) => page.getByText(/Dr\. Anil Rao/).first(),
  },
  { name: 'find a doctor', path: '/portal/doctors', ready: (page) => page.getByText('Dr. Anil Rao').first() },
  {
    name: "a doctor's page, a time chosen",
    path: `/portal/doctors/${DOCTOR_ID}?slot=${encodeURIComponent(FIRST_SLOT)}`,
    ready: (page) => page.getByRole('button', { name: 'Continue' }),
  },
  {
    name: 'booking an appointment',
    path: `/portal/doctors/${DOCTOR_ID}?slot=${encodeURIComponent(FIRST_SLOT)}`,
    ready: (page) => page.getByRole('button', { name: 'Continue' }),
    act: async (page) => {
      await page.getByRole('button', { name: 'Continue' }).click();
      await expect(page.getByRole('button', { name: 'Book appointment' })).toBeVisible();
    },
    check: { focus: { from: 'here' } },
  },
  {
    name: 'the rate list, not signed in',
    path: '/portal/rates',
    signedIn: false,
    ready: (page) => page.getByText('Complete blood count'),
  },
  { name: 'bills', path: '/portal/bills', ready: (page) => page.getByText('OP2600001') },
  { name: 'reports', path: '/portal/reports', ready: (page) => page.getByText('Complete blood count') },
  {
    name: 'profile',
    path: '/portal/profile',
    ready: (page) => page.getByRole('heading', { level: 1, name: 'Profile' }),
  },
  {
    name: 'messages from the hospital (preferences)',
    path: '/portal/profile/preferences',
    ready: (page) => page.getByRole('heading', { level: 1, name: 'Messages from the hospital' }),
  },
];

/** Opens the built portal at `path` in `theme`, signed in as USER or not, the API answered from the fixtures. */
async function open(page: Page, theme: BrowserTheme, { path, signedIn = true, ready, act }: PortalPage) {
  const server = createFakeFetch({
    ...PORTAL_ANSWERS,
    // The session comes back from the refresh cookie on start, as after a reload.
    'POST /api/v1/auth/refresh': signedIn
      ? { accessToken: 'a11y-token', expiresAt: new Date(Date.now() + 3600_000).toISOString(), user: USER }
      : problem(401, 'Sign in again'),
    'GET /health/live': 'Healthy',
  });
  await prepareTheme(page, theme, 'device');
  await serveFakeApi(page, server.fetch);
  await page.goto(path);
  await expect(ready(page)).toBeVisible();
  await page.waitForLoadState('networkidle');
  await act?.(page);
  // A request the fake server could not answer shows an error where the page's content should be.
  expect(server.unanswered, 'requests without a canned answer').toEqual([]);
  return server;
}

/**
 * With the device's high contrast on, the browser paints the patient's own system colours over the portal's, and axe
 * reads the portal's colours rather than those painted, so its contrast figures mean nothing there. Target sizes and
 * focus are checked as in the other themes, and the portal's own high-contrast colours in the staff app's checks.
 */
const forcedColours = (theme: BrowserTheme, check: ScreenCheckOptions = {}): ScreenCheckOptions =>
  theme === 'high-contrast'
    ? {
        ...check,
        skip: {
          ...check.skip,
          'color-contrast':
            "forced colours: the browser paints the device's system colours, which axe does not read (see pages.spec.ts)",
        },
      }
    : check;

for (const theme of BROWSER_THEMES) {
  test.describe(`phone, ${theme === 'high-contrast' ? 'high contrast (forced colours)' : `${theme} theme`}`, () => {
    test.use({ viewport: PHONE, isMobile: true, hasTouch: true });

    for (const portalPage of PAGES) {
      test(portalPage.name, async ({ page }, testInfo) => {
        const server = await open(page, theme, portalPage);
        await expectAccessibleScreen(
          page,
          testInfo,
          forcedColours(theme, portalPage.check ?? { focus: { from: 'skip-link' } }),
        );
        expect(server.unanswered, 'requests without a canned answer').toEqual([]);
      });
    }

    test('the More menu', async ({ page }, testInfo) => {
      await open(page, theme, PAGES[2]!);
      await page.getByRole('button', { name: 'More' }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await expectAccessibleScreen(page, testInfo, forcedColours(theme, { focus: { from: 'here' } }));
    });
  });
}

test.describe('computer, light theme', () => {
  test.use({ viewport: COMPUTER });

  for (const portalPage of PAGES.filter((p) =>
    [
      'sign-in',
      'home',
      'appointments',
      "a doctor's page, a time chosen",
      'the rate list, not signed in',
      'bills',
      'reports',
    ].includes(p.name),
  )) {
    test(portalPage.name, async ({ page }, testInfo) => {
      await open(page, 'light', portalPage);
      await expectAccessibleScreen(page, testInfo, portalPage.check ?? { focus: { from: 'skip-link' } });
    });
  }
});
