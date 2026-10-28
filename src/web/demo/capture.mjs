// Records what the real server answers the real staff app, screen by screen, for each demo role: it signs in as the
// role in Chromium, walks the role's part of the app (lib/walk.mjs) and keeps each API call's answer. The answers are
// the demo's data: build.mjs packs them.
//
//   HC360_DEMO_PASSWORD=… HC360_DEMO_DB=postgresql://… node demo/capture.mjs <out-dir> [role,role,…]
//
// CHROMIUM_PATH points at a Chromium other than the one this Playwright version downloads. DEMO_PARALLEL roles (3)
// record at a time, each for at most DEMO_MAX_MINUTES (40).
//
// The role "patient" is the patient personas.ts names, on the patient portal (HC360_PORTAL, the server's patients'
// port). Patients sign in with a code sent by SMS; the demo server has no SMS gateway, so the code is read from the
// messages it queued for one, in its database (HC360_DEMO_DB, a connection URI for psql).

import { chromium } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { PORTAL_PATIENT } from './app/personas.ts';
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
  'patient',
];
const roles = (process.argv[3] ?? ROLES.join(',')).split(',');
const PARALLEL = Number(process.env.DEMO_PARALLEL ?? 3);
const MAX_MINUTES = Number(process.env.DEMO_MAX_MINUTES ?? 40);
/** Where the server serves the patient portal: its own port, which a hospital opens to the internet. */
const PORTAL = process.env.HC360_PORTAL ?? 'https://localhost:7361';
const DB = process.env.HC360_DEMO_DB;

const ORIGINS = new Set([new URL(BASE).origin, new URL(PORTAL).origin]);
const isApi = (url) => {
  const { origin, pathname } = new URL(url);
  return ORIGINS.has(origin) && (pathname.startsWith('/api/') || pathname === '/health/live');
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

/** Asks the demo server's database (psql, HC360_DEMO_DB) one question; `values` are passed as psql variables. */
function ask(query, values) {
  if (!DB) {
    throw new Error("Set HC360_DEMO_DB to a connection URI for the demo server's database (the patient role).");
  }
  const variables = Object.entries(values).flatMap(([name, value]) => ['-v', `${name}=${value}`]);
  return execFileSync('psql', [DB, '-At', '-v', 'ON_ERROR_STOP=1', ...variables], { input: query, encoding: 'utf8' })
    .split('\n')
    .filter(Boolean)
    .map((row) => row.split('|'));
}

/** The portal sign-in codes the server has queued for a number, as [message id, code]. */
const sentCodes = (phone) =>
  ask(
    `select id, payload->'values'->>'code' from platform.outbox_messages
     where payload->>'templateKey' = 'auth.otp' and payload->>'to' = :'phone';`,
    { phone },
  );

/** Signs in to the patient portal as PORTAL_PATIENT, with the code the server sent to their phone. */
async function signInPatient(page) {
  const [[phone] = []] = ask(`select phone from patients.patients where full_name = :'name' limit 1;`, {
    name: PORTAL_PATIENT,
  });
  if (!phone) throw new Error(`No patient called ${PORTAL_PATIENT}: run story.mjs first.`);
  const earlier = new Set(sentCodes(phone).map(([id]) => id));
  await page.goto(`${PORTAL}/portal/sign-in`);
  const number = page.locator('input[autocomplete="tel-national"]');
  await number.fill(phone.replace(/^\+91/, ''));
  await Promise.all([
    page.waitForResponse((r) => r.url().endsWith('/api/v1/auth/otp/request') && r.ok(), { timeout: 60_000 }),
    number.press('Enter'),
  ]);
  let code;
  for (let i = 0; i < 40 && !code; i++) {
    code = sentCodes(phone).find(([id]) => !earlier.has(id))?.[1];
    if (!code) await page.waitForTimeout(250);
  }
  if (!code) throw new Error(`The server queued no sign-in code for ${PORTAL_PATIENT}.`);
  const verified = page.waitForResponse((r) => r.url().endsWith('/api/v1/auth/otp/verify'));
  const box = page.locator('input[autocomplete="one-time-code"]');
  await box.fill(code);
  // Six digits may send themselves; otherwise the form is sent as a patient would.
  const sent = await Promise.race([verified.then(() => true), page.waitForTimeout(1500).then(() => false)]);
  if (!sent) await box.press('Enter');
  const user = (await (await verified).json()).user;
  await page.waitForURL((url) => !url.pathname.endsWith('/sign-in'), { timeout: 30_000 });
  return { user, phone };
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

  const portal = role === 'patient';
  /** In-app navigation, as a link does (the router follows pushState): a reload would sign in again each time. */
  async function go(path) {
    await page.evaluate((target) => globalThis.history.pushState(null, '', target), path);
    await quiet();
    if (pathOf(page.url()) !== path) {
      await page.goto(`${portal ? PORTAL : BASE}${path}`);
      await quiet();
    }
  }

  // Sign in as the role, on the sign-in screen.
  let user;
  let phone;
  try {
    if (portal) {
      ({ user, phone } = await signInPatient(page));
    } else {
      await page.goto(`${BASE}/login`);
      await page.locator('input[autocomplete="username"]').fill(role);
      await page.locator('input[autocomplete="current-password"]').fill(PASSWORD);
      const [answer] = await Promise.all([
        page.waitForResponse((r) => r.url().endsWith('/api/v1/auth/login')),
        page.getByRole('button', { name: 'Sign in' }).click(),
      ]);
      user = (await answer.json()).user;
      await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
    }
  } catch (error) {
    await page.screenshot({ path: join(OUT, `${role}-sign-in.png`) });
    throw error;
  }

  const { menu, visited } = await walkRole(page, {
    here: () => pathOf(page.url()),
    go,
    quiet,
    user,
    patients: portal ? [] : patients,
    outOfTime: () => Date.now() - startedAt > MAX_MINUTES * 60_000,
    app: portal ? 'portal' : 'staff',
  });

  await quiet();
  await Promise.all([...pending]);
  await context.close();
  const result = {
    role,
    recordedAt: now.toISOString(),
    minutes: Math.round((Date.now() - startedAt) / 6000) / 10,
    user,
    ...(phone ? { phone } : {}),
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
