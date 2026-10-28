// Walks the demo build as capture.mjs walked the real server: each role, the same walk (lib/walk.mjs), on the exact
// files that get published, served as one page the way the Artifact serves it. A screen passes when every call it
// made was answered from the recording and the browser reported no error; every guided place must pass, and each is
// photographed.
//
//   node demo/verify.mjs <dist-dir> <report-dir> [role,role,…]
//
// Writes <report-dir>/verify.json and verify.md, and exits 1 when a guided place failed.

import { chromium } from '@playwright/test';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve } from 'node:path';
import { PERSONAS } from './app/personas.ts';
import { walkRole } from './lib/walk.mjs';

const [DIST, REPORT] = process.argv.slice(2, 4).map((p) => resolve(p));
if (!DIST || !REPORT) {
  throw new Error('Usage: node demo/verify.mjs <dist-dir> <report-dir> [role,role,…]');
}
const MAX_MINUTES = Number(process.env.DEMO_MAX_MINUTES ?? 40);
const PARALLEL = Number(process.env.DEMO_PARALLEL ?? 3);
mkdirSync(REPORT, { recursive: true });

// ------------------------------------------------------------------------------------------------- the page, served

const TYPES = {
  '.js': 'text/javascript',
  '.css': 'text/css',
  '.json': 'application/json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
};
/** What the Artifact wraps the page in: a document with a charset, a viewport and a small reset. */
const skeleton = (page) => `<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<style>:root{color-scheme:light;padding:env(safe-area-inset-top,0) 0 env(safe-area-inset-bottom,0)}body{margin:0;font:14px system-ui}img{max-width:100%}[hidden]{display:none!important}</style>
</head><body>${page}</body></html>`;
/** A strict policy: the page's own files and inline code, nothing from elsewhere, no eval. */
const POLICY = [
  "default-src 'none'",
  "script-src 'self' 'unsafe-inline'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data: blob:",
  "font-src 'self' data:",
  "connect-src 'self'",
  "worker-src 'self' blob:",
].join('; ');

const server = createServer((request, response) => {
  const path = decodeURIComponent(new URL(request.url, 'http://demo').pathname);
  if (path === '/' || path === '/index.html') {
    response.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'content-security-policy': POLICY });
    response.end(skeleton(readFileSync(join(DIST, 'index.html'), 'utf8')));
    return;
  }
  const file = normalize(join(DIST, path));
  try {
    if (!file.startsWith(DIST)) throw new Error('outside');
    const body = readFileSync(file);
    response.writeHead(200, { 'content-type': TYPES[extname(file)] ?? 'application/octet-stream' });
    response.end(body);
  } catch {
    // Anything else, the API included, is not there: the replay must have answered it in the page.
    response.writeHead(404, { 'content-type': 'text/plain' });
    response.end('not here');
  }
});
await new Promise((ready) => server.listen(0, '127.0.0.1', ready));
const BASE = `http://127.0.0.1:${server.address().port}`;

// ------------------------------------------------------------------------------------------------- the walk

const meta = JSON.parse(
  readFileSync(join(DIST, 'data/meta.js'), 'utf8')
    .replace(/^self\.HC360_DEMO = /, '')
    .replace(/;\s*$/, ''),
);
const roles = (process.argv[4] ?? meta.roles.map((r) => r.role).join(',')).split(',');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined });
const results = {};
/** What the screen shows when the replay or the app could not give it its data. */
const BROKEN =
  /Not part of the demo recording|Something went wrong|The server had a problem|Cannot reach the HealthCare360 server/;

async function newPage(viewport = { width: 1440, height: 900 }, colorScheme = 'light') {
  const context = await browser.newContext({ viewport, colorScheme, locale: 'en-IN', timezoneId: 'Asia/Kolkata' });
  const page = await context.newPage();
  const errors = [];
  page.on('console', (message) => {
    if (message.type() === 'error') errors.push(message.text().slice(0, 300));
  });
  page.on('pageerror', (error) => errors.push(String(error.message ?? error).slice(0, 300)));
  page.on('dialog', (dialog) => void dialog.dismiss().catch(() => undefined));
  return { context, page, errors };
}

async function verifyRole(role) {
  const startedAt = Date.now();
  const person = meta.roles.find((r) => r.role === role);
  const { context, page, errors } = await newPage();
  let current = '/';
  const here = () => current;
  const track = async () => {
    current = await page.evaluate(() => globalThis.HC360_DEMO_HERE());
  };
  const misses = () => page.evaluate(() => globalThis.HC360_DEMO_REPLAY.misses.length);
  async function quiet(max = 10_000) {
    const started = Date.now();
    await page.waitForTimeout(150);
    while (Date.now() - started < max) {
      const settled = await page.evaluate(
        () =>
          globalThis.HC360_DEMO_REPLAY.inflight === 0 &&
          performance.now() - globalThis.HC360_DEMO_REPLAY.lastActivity > 500,
      );
      if (settled) break;
      await page.waitForTimeout(100);
    }
    await track();
  }
  async function go(path) {
    await page.evaluate((target) => globalThis.HC360_DEMO_NAVIGATE(target), path);
    await quiet();
  }

  await page.goto(BASE);
  await page.getByRole('button', { name: `Sign in as ${person.displayName}` }).click();
  await page.getByRole('navigation', { name: 'Main navigation' }).waitFor({ timeout: 60_000 });
  const user = await page.evaluate(async () => {
    const answer = await fetch('/api/v1/auth/me');
    return answer.json();
  });

  // The guided places first: they are what a visitor is sent to.
  const places = [];
  for (const [i, place] of (PERSONAS.find((p) => p.role === role)?.places ?? []).entries()) {
    const before = await misses();
    const errorsBefore = errors.length;
    await go(place.path);
    await page.waitForTimeout(400);
    const shot = `${role}-${i + 1}.png`;
    await page.screenshot({ path: join(REPORT, shot) });
    const broken = await page.getByText(BROKEN).count();
    const missed = (await misses()) - before;
    places.push({
      ...place,
      shot,
      missed,
      errors: errors.slice(errorsBefore),
      broken,
      ok: missed === 0 && broken === 0 && errors.length === errorsBefore,
    });
  }

  const screens = new Map();
  const { menu, visited } = await walkRole(page, {
    here,
    go,
    quiet,
    user,
    patients: meta.patients,
    outOfTime: () => Date.now() - startedAt > MAX_MINUTES * 60_000,
    onScreen: (path) => screens.set(path, true),
  });
  const allMisses = await page.evaluate(() => globalThis.HC360_DEMO_REPLAY.misses);
  await context.close();

  const byScreen = {};
  for (const miss of allMisses) (byScreen[miss.screen] ??= []).push(miss.request);
  const result = {
    role,
    minutes: Math.round((Date.now() - startedAt) / 6000) / 10,
    menu: menu.length,
    notOpened: menu.filter((m) => m.error || m.skipped).map((m) => `${m.category ?? ''} › ${m.label}`),
    screens: visited.length,
    screensMissing: Object.keys(byScreen).length,
    misses: byScreen,
    errors: [...new Set(errors)],
    places,
  };
  results[role] = result;
  console.log(
    `${role}: ${result.screens} screens, ${result.screensMissing} with unrecorded calls, ` +
      `${result.errors.length} browser errors, guided places ${places.filter((p) => p.ok).length}/${places.length}`,
  );
}

// The front page, as a visitor first sees it: wide, on a phone, and in a dark theme.
for (const [name, viewport, scheme] of [
  ['landing-wide.png', { width: 1440, height: 900 }, 'light'],
  ['landing-phone.png', { width: 390, height: 844 }, 'light'],
  ['landing-dark.png', { width: 1440, height: 900 }, 'dark'],
]) {
  const { context, page } = await newPage(viewport, scheme);
  await page.goto(BASE);
  await page
    .getByRole('button', { name: /^Sign in as/ })
    .first()
    .waitFor();
  await page.waitForFunction(() => !globalThis.document.querySelector('button[disabled]'));
  await page.screenshot({ path: join(REPORT, name), fullPage: true });
  await context.close();
}

const work = [...roles];
await Promise.all(
  Array.from({ length: Math.min(PARALLEL, work.length) }, async () => {
    while (work.length) {
      const role = work.shift();
      try {
        await verifyRole(role);
      } catch (error) {
        results[role] = { role, failed: String(error.message ?? error).split('\n')[0] };
        console.log(`${role}: failed — ${results[role].failed}`);
      }
    }
  }),
);
await browser.close();
server.close();

// ------------------------------------------------------------------------------------------------- the report

writeFileSync(join(REPORT, 'verify.json'), JSON.stringify(results, null, 2));
const lines = [
  '| Role | Screens walked | With unrecorded calls | Browser errors | Guided places |',
  '|---|---|---|---|---|',
];
for (const r of Object.values(results)) {
  lines.push(
    r.failed
      ? `| ${r.role} | failed: ${r.failed} | | | |`
      : `| ${r.role} | ${r.screens} | ${r.screensMissing} | ${r.errors.length} | ${r.places.filter((p) => p.ok).length}/${r.places.length} |`,
  );
}
writeFileSync(join(REPORT, 'verify.md'), `${lines.join('\n')}\n`);
console.log(`\n${lines.join('\n')}`);
const failedPlaces = Object.values(results).flatMap((r) =>
  r.failed ? [r.role] : r.places.filter((p) => !p.ok).map((p) => `${r.role} ${p.path}`),
);
if (failedPlaces.length) {
  console.log(`\nGuided places that failed: ${failedPlaces.join(', ')}`);
  process.exitCode = 1;
}
