import { defineConfig, devices } from '@playwright/test';

// Accessibility checks of the built staff app and patient portal in Chromium (SRS UX-10): `pnpm test:a11y`. Each app is
// built into its own dist folder (never the server's wwwroot) and served by `vite preview`; the tests answer the API in
// the browser with canned data, so no HC360 Server is needed. docs/architecture/FRONTEND-GUIDE.md, "Accessibility".

const STAFF = 'http://127.0.0.1:5380';
const PORTAL = 'http://127.0.0.1:5381';

const serve = (app: string, port: number) =>
  `pnpm --filter @hc360/${app} exec vite build --outDir dist --emptyOutDir --logLevel warn && ` +
  `pnpm --filter @hc360/${app} exec vite preview --outDir dist --host 127.0.0.1 --port ${port} --strictPort`;

export default defineConfig({
  testMatch: '*.spec.ts',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  workers: process.env.CI ? 2 : 3,
  timeout: 120_000,
  expect: { timeout: 20_000 },
  reporter: process.env.CI
    ? [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]]
    : [['list'], ['html', { open: 'on-failure', outputFolder: 'playwright-report' }]],
  use: {
    ...devices['Desktop Chrome'],
    locale: 'en-IN',
    timezoneId: 'Asia/Kolkata',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    // The checks themselves, on small pages of their own (no app needed).
    { name: 'checks', testDir: 'packages/ui/a11y' },
    {
      name: 'staff',
      testDir: 'apps/staff/a11y',
      use: { baseURL: STAFF, viewport: { width: 1280, height: 720 } },
    },
    {
      name: 'portal',
      testDir: 'apps/portal/a11y',
      use: { baseURL: PORTAL },
    },
  ],
  webServer: [
    { command: serve('staff', 5380), url: `${STAFF}/`, reuseExistingServer: !process.env.CI, timeout: 300_000 },
    {
      command: serve('portal', 5381),
      url: `${PORTAL}/portal/`,
      reuseExistingServer: !process.env.CI,
      timeout: 300_000,
    },
  ],
});
