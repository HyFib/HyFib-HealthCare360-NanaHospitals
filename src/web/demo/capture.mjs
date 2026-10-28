// Records what the real server answers the real staff app, screen by screen, for each demo role: it signs in as the
// role in Chromium, walks the role's part of the app (lib/walk.mjs) and keeps each API call's answer. The answers are
// the demo's data: build.mjs packs them.
//
//   HC360_DEMO_PASSWORD=… node demo/capture.mjs <out-dir> [role,role,…]
//
// CHROMIUM_PATH points at a Chromium other than the one this Playwright version downloads. DEMO_PARALLEL roles (3)
// record at a time, each for at most DEMO_MAX_MINUTES (40).

import { chromium } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { Api, BASE } from './lib/api.mjs';
import { walkRole } from './lib/walk.mjs';

const PASSWORD = process.env.HC360_DEMO_PASSWORD;
const OUT = process.argv[2] ?? 'recordings';
export const ROLES = [
  'frontoffice',
  'doctor',
  'nurse',
  'lab',
  'pathologist',
  'radiographer',
  'radiologist',
  'pharmacist',
  'billing',
  'hospitaladmin',
  'admin',
];
const roles = (process.argv[3] ?? ROLES.join(',')).split(',');
const PARALLEL = Number(process.env.DEMO_PARALLEL ?? 3);
const MAX_MINUTES = Number(process.env.DEMO_MAX_MINUTES ?? 40);

const isApi = (url) => {
  const { origin, pathname } = new URL(url);
  return origin === new URL(BASE).origin && (pathname.startsWith('/api/') || pathname === '/health/live');
};
const TEXT = /json|text|xml|html|javascript|csv|problem/;
const pathOf = (url) => {
  const { pathname, search } = new URL(url);
  return pathname + search;
};

async function serverNow() {
  const response = await fetch(`${BASE}/health/live`);
  return new Date(response.headers.get('date'));
}

/**
 * What each workstation keeps in local storage, set as the hospital's staff would on their first day: the queue desk
 * at General Medicine for Dr. Arjun Mehta's room (OPD-01), the nurses' station on the general ward (female), the first
 * OP billing counter. The demo build sets the same, so its screens open as these did.
 */
export async function deskSettings() {
  const admin = await Api.as('admin');
  const gm = (await admin.get('/api/v1/masters/lookups/departments')).find((d) => d.code === 'GM');
  const ward = (await admin.get('/api/v1/inpatient/wards')).find((w) => w.code === 'GWF');
  return {
    'hc360:queue-desk': JSON.stringify({ departmentId: gm.id, practitionerId: '', room: 'OPD-01' }),
    'hc360:nursing:ward': ward.id,
    'hc360:billing:counter': JSON.stringify({
      counter: 'OP counter 1',
      terminalId: '',
      receiptFormat: 'Receipt80',
      autoPrint: false,
    }),
  };
}

async function patientIds() {
  const admin = await Api.as('admin');
  return (await admin.get('/api/v1/patients?page=1&pageSize=50')).items.map((p) => p.id);
}

async function record(browser, role, desk, patients) {
  const startedAt = Date.now();
  const now = await serverNow();
  const context = await browser.newContext({
    ignoreHTTPSErrors: true,
    viewport: { width: 1440, height: 900 },
    locale: 'en-IN',
    timezoneId: 'Asia/Kolkata',
  });
  // The page's clock is the server's, held still (story.mjs stops the server's at the moment the demo shows): the
  // screens ask for "today" and count minutes from it. Timers keep running.
  await context.clock.setFixedTime(now);
  await context.addInitScript((settings) => {
    for (const [key, value] of Object.entries(settings)) {
      if (localStorage.getItem(key) === null) {
        localStorage.setItem(key, value);
      }
    }
  }, desk);
  const page = await context.newPage();
  // The browser's own questions: leaving a page is fine (nothing unsaved matters here), anything else is declined.
  page.on(
    'dialog',
    (dialog) => void (dialog.type() === 'beforeunload' ? dialog.accept() : dialog.dismiss()).catch(() => undefined),
  );
  context.on('page', (other) => void other.close().catch(() => undefined));

  const entries = new Map();
  const writes = [];
  const pending = new Set();
  let inflight = 0;
  let lastNetwork = Date.now();

  page.on('request', (request) => {
    if (isApi(request.url())) {
      inflight++;
      lastNetwork = Date.now();
    }
  });
  const settle = (request) => {
    if (isApi(request.url())) {
      inflight = Math.max(0, inflight - 1);
      lastNetwork = Date.now();
    }
  };
  page.on('requestfinished', settle);
  page.on('requestfailed', settle);
  page.on('response', (response) => {
    if (!isApi(response.url())) return;
    const request = response.request();
    const url = new URL(response.url());
    const key = `${request.method()} ${url.pathname}${url.search}`;
    const screen = pathOf(page.url());
    if (request.method() !== 'GET' && !url.pathname.startsWith('/api/v1/auth/')) {
      writes.push({ key, status: response.status(), screen });
    }
    if (entries.has(key)) return;
    const headers = response.headers();
    const entry = {
      status: response.status(),
      type: headers['content-type'] ?? null,
      disposition: headers['content-disposition'] ?? null,
      encoding: 'utf8',
      body: null,
      screen,
    };
    entries.set(key, entry);
    const read = response
      .body()
      .then((buffer) => {
        if (!entry.type || TEXT.test(entry.type) || buffer.length === 0) {
          entry.body = buffer.toString('utf8');
        } else if (buffer.length <= 3_000_000) {
          entry.encoding = 'base64';
          entry.body = buffer.toString('base64');
        }
      })
      .catch(() => undefined)
      .finally(() => pending.delete(read));
    pending.add(read);
  });

  async function quiet(max = 15_000) {
    const started = Date.now();
    await page.waitForTimeout(250);
    while (Date.now() - started < max) {
      if (inflight === 0 && Date.now() - lastNetwork > 700) return;
      await page.waitForTimeout(150);
    }
  }

  /** In-app navigation, as a link does (the router follows pushState): a reload would sign in again each time. */
  async function go(path) {
    await page.evaluate((target) => globalThis.history.pushState(null, '', target), path);
    await quiet();
    if (pathOf(page.url()) !== path) {
      await page.goto(`${BASE}${path}`);
      await quiet();
    }
  }

  // Sign in as the role, on the sign-in screen.
  await page.goto(`${BASE}/login`);
  let user;
  try {
    await page.locator('input[autocomplete="username"]').fill(role);
    await page.locator('input[autocomplete="current-password"]').fill(PASSWORD);
    const [answer] = await Promise.all([
      page.waitForResponse((r) => r.url().endsWith('/api/v1/auth/login')),
      page.getByRole('button', { name: 'Sign in' }).click(),
    ]);
    user = (await answer.json()).user;
    await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
  } catch (error) {
    await page.screenshot({ path: join(OUT, `${role}-sign-in.png`) });
    throw error;
  }

  const { menu, visited } = await walkRole(page, {
    here: () => pathOf(page.url()),
    go,
    quiet,
    user,
    patients,
    outOfTime: () => Date.now() - startedAt > MAX_MINUTES * 60_000,
  });

  await quiet();
  await Promise.all([...pending]);
  await context.close();
  const result = {
    role,
    recordedAt: now.toISOString(),
    minutes: Math.round((Date.now() - startedAt) / 6000) / 10,
    user,
    desk,
    patients,
    menu,
    visited,
    writes,
    entries: Object.fromEntries(entries),
  };
  writeFileSync(join(OUT, `${role}.json`), JSON.stringify(result));
  const failed = menu.filter((m) => m.error || m.skipped).length;
  console.log(
    `${role}: ${menu.length} menu items (${failed} not opened), ${visited.length} screens, ` +
      `${entries.size} answers, ${writes.length} writes, ${result.minutes} min`,
  );
  return result;
}

if (!PASSWORD) {
  throw new Error("Set HC360_DEMO_PASSWORD to the demo accounts' password.");
}
mkdirSync(OUT, { recursive: true });
const desk = await deskSettings();
const patients = await patientIds();
// CHROMIUM_PATH: a Chromium other than the one this Playwright version downloads (a machine with its own browser).
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined });
const work = [...roles];
await Promise.all(
  Array.from({ length: Math.min(PARALLEL, work.length) }, async () => {
    while (work.length) {
      const role = work.shift();
      try {
        await record(browser, role, desk, patients);
      } catch (error) {
        console.log(`${role}: failed — ${String(error.message ?? error).split('\n')[0]}`);
      }
    }
  }),
);
await browser.close();
