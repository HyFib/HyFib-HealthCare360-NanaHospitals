// Runs one storyboard against a real HC360 server, signed in as the storyboard's user, and keeps what the video needs:
// a screenshot before and after each step, the position of what the step points at, and the text of the narration.
// The run is also the video's test: every step must find its target and every expected result must appear, or the
// capture stops with the step, its narration and a picture of the screen, and no video is made from it.
//
//   node training/pipeline/capture.mjs <storyboard.mjs> <out-dir>
//
// HC360_SERVER (https://localhost:7360), HC360_PORTAL (https://localhost:7361), HC360_DEMO_PASSWORD (the training
// accounts' password), HC360_DEMO_DB (for the patient's sign-in code: the training server has no SMS gateway).

import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { ending, narration, stepId } from './narration.mjs';

// Playwright as installed on the machine that records (NODE_PATH), with the Chromium of its own version.
const { chromium } = createRequire(import.meta.url)('playwright');
const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const BASE = process.env.HC360_SERVER ?? 'https://localhost:7360';
const PORTAL = process.env.HC360_PORTAL ?? 'https://localhost:7361';
const PASSWORD = process.env.HC360_DEMO_PASSWORD;
export const VIEWPORT = { width: 1440, height: 810, dpr: 2 };

const [storyPath, outDir] = process.argv.slice(2);
if (!storyPath || !outDir) throw new Error('usage: capture.mjs <storyboard.mjs> <out-dir>');
const story = (await import(pathToFileURL(resolve(storyPath)).href)).default;
const OUT = resolve(outDir);
mkdirSync(join(OUT, 'shots'), { recursive: true });

// The training server's database, for what a real patient would read on their phone: the portal's sign-in code.
const { ask, sentCodes } = await import(pathToFileURL(join(ROOT, 'src/web/demo/lib/db.mjs')).href);
const { Api } = await import(pathToFileURL(join(ROOT, 'src/web/demo/lib/api.mjs')).href);
/** The API as one of the training accounts, for a storyboard's setup (what other roles did before the video starts). */
const api = (userName) => Api.as(userName);
/** What steps share: a phone number found in one step and typed in the next. */
const state = {};

function commit() {
  try {
    return execFileSync('git', ['-C', ROOT, 'rev-parse', '--short=8', 'HEAD'], { encoding: 'utf8' }).trim();
  } catch {
    return 'unknown';
  }
}

// ----------------------------------------------------------------------------------------------------- the desk

/**
 * What a workstation keeps in local storage, set as the hospital's staff would on their first day (the same as the
 * demo's, src/web/demo/capture.mjs): the queue desk at General Medicine for room OPD-01, the nurses' station on the
 * female general ward, the first OP billing counter.
 */
async function deskSettings() {
  const admin = await Api.as('admin');
  const gm = (await admin.get('/api/v1/masters/lookups/departments')).find((d) => d.code === 'GM');
  const ward = (await admin.get('/api/v1/inpatient/wards')).find((w) => w.code === 'GWF');
  return {
    'hc360:queue-desk': JSON.stringify({ departmentId: gm.id, practitionerId: '', room: 'OPD-01' }),
    'hc360:nursing:ward': ward.id,
    'hc360:billing:counter': JSON.stringify({ counter: 'OP counter 1', terminalId: '', receiptFormat: 'Receipt80', autoPrint: false }),
  };
}
const desk = story.desk ? await deskSettings() : {};

// ----------------------------------------------------------------------------------------------------- the browser

// Dates as an Indian workstation shows them (14/06/1988): Chromium formats date fields in its own language.
const browser = await chromium.launch({ args: ['--lang=en-IN'], env: { ...process.env, LANG: 'en_IN.UTF-8', LANGUAGE: 'en_IN' } });
const context = await browser.newContext({
  ignoreHTTPSErrors: true,
  viewport: { width: VIEWPORT.width, height: VIEWPORT.height },
  deviceScaleFactor: VIEWPORT.dpr,
  locale: 'en-IN',
  timezoneId: 'Asia/Kolkata',
  reducedMotion: 'reduce',
  colorScheme: 'light',
});
const serverDate = new Date((await fetch(`${BASE}/health/live`)).headers.get('date'));
await context.clock.install({ time: serverDate });
await context.addInitScript((settings) => {
  for (const [key, value] of Object.entries(settings)) {
    if (localStorage.getItem(key) === null) localStorage.setItem(key, value);
  }
}, { ...desk, ...(story.localStorage ?? {}) });
// A screenshot shows no blinking caret or half-drawn scrollbar.
await context.addInitScript(() => {
  const style = () => {
    const s = document.createElement('style');
    s.textContent = '*{caret-color:transparent!important} ::-webkit-scrollbar{width:8px;height:8px} ::-webkit-scrollbar-thumb{background:#c8c8c8;border-radius:4px}';
    document.head.appendChild(s);
  };
  if (document.head) style();
  else document.addEventListener('DOMContentLoaded', style);
});
const page = await context.newPage();
page.on('dialog', (d) => void d.accept().catch(() => undefined));

let inflight = 0;
let lastNetwork = Date.now();
const isApi = (url) => url.includes('/api/');
page.on('request', (r) => {
  if (isApi(r.url())) {
    inflight++;
    lastNetwork = Date.now();
  }
});
const done = (r) => {
  if (isApi(r.url())) {
    inflight = Math.max(0, inflight - 1);
    lastNetwork = Date.now();
  }
};
page.on('requestfinished', done);
page.on('requestfailed', done);

const failures = [];
page.on('response', (r) => {
  if (isApi(r.url()) && r.status() >= 500) failures.push(`${r.request().method()} ${new URL(r.url()).pathname} ${r.status()}`);
});

async function quiet(max = 15000) {
  const start = Date.now();
  await page.waitForTimeout(200);
  while (Date.now() - start < max) {
    // Loading, not progress: dashboards draw progress bars that never go away.
    const busy = await page.locator('[aria-busy="true"]:visible, .fui-Spinner:visible, .fui-Skeleton:visible').count().catch(() => 0);
    if (inflight === 0 && Date.now() - lastNetwork > 600 && busy === 0) break;
    await page.waitForTimeout(120);
  }
  await page.evaluate(() => document.fonts.ready).catch(() => undefined);
  await page.waitForTimeout(250);
}

// ----------------------------------------------------------------------------------------------------- targets

/** A storyboard names what it points at the way a person reads the screen: a button's name, a field's label. */
function find(spec, scope = page) {
  if (typeof spec === 'string') return scope.getByText(spec, { exact: true }).first();
  let l;
  if (spec.within) scope = find(spec.within, scope);
  // The menu's areas and screens are buttons in the "Main navigation" landmark.
  if (spec.nav) l = page.getByRole('navigation', { name: 'Main navigation' }).getByRole('button', { name: spec.nav, exact: true });
  else if (spec.role)
    l = scope.getByRole(spec.role, {
      ...(spec.name !== undefined ? { name: spec.name, exact: spec.exact ?? typeof spec.name === 'string' } : {}),
      ...(spec.level ? { level: spec.level } : {}),
      ...(spec.selected !== undefined ? { selected: spec.selected } : {}),
    });
  else if (spec.label) {
    // A field by its accessible name, as a screen reader announces it: a required field's label also shows an
    // asterisk, which a match on the label's text would have to include.
    const named = (role) => scope.getByRole(role, { name: spec.label, exact: spec.exact ?? typeof spec.label === 'string' });
    l = named('textbox').or(named('combobox')).or(named('spinbutton')).or(named('searchbox'));
  }
  else if (spec.placeholder) l = scope.getByPlaceholder(spec.placeholder, { exact: spec.exact ?? false });
  else if (spec.row) l = scope.getByRole('row').filter({ hasText: spec.row });
  else if (spec.text) l = scope.getByText(spec.text, { exact: spec.exact ?? true });
  else if (spec.css) l = scope.locator(spec.css);
  else if (spec.testId) l = scope.getByTestId(spec.testId);
  else throw new Error(`Cannot find ${JSON.stringify(spec)}`);
  if (spec.has) l = l.filter({ hasText: spec.has });
  if (spec.nth !== undefined) l = l.nth(spec.nth);
  else if (spec.last) l = l.last();
  else l = l.first();
  return l;
}

async function boxOf(locator) {
  const b = await locator.boundingBox();
  if (!b) return null;
  const x = Math.max(0, b.x);
  const y = Math.max(0, b.y);
  const box = [x, y, Math.min(VIEWPORT.width, b.x + b.width) - x, Math.min(VIEWPORT.height, b.y + b.height) - y];
  // Outside the screen: nothing to point at.
  if (box[2] <= 0 || box[3] <= 0) return null;
  return box.map((v) => Math.round(v * 10) / 10);
}

let shotNo = 0;
let lastShot = null;
async function shot(label) {
  const buffer = await page.screenshot({ animations: 'disabled', caret: 'hide' });
  const hash = createHash('sha1').update(buffer).digest('hex').slice(0, 12);
  if (lastShot && lastShot.hash === hash) return lastShot.name;
  const name = `shots/${String(++shotNo).padStart(3, '0')}-${label}.png`;
  writeFileSync(join(OUT, name), buffer);
  lastShot = { hash, name };
  return name;
}

// ----------------------------------------------------------------------------------------------------- sign in

async function signInStaff(user) {
  await page.goto(`${BASE}/login`);
  await page.locator('input[autocomplete="username"]').fill(user);
  await page.locator('input[autocomplete="current-password"]').fill(PASSWORD);
  await Promise.all([
    page.waitForResponse((r) => r.url().endsWith('/api/v1/auth/login')),
    page.getByRole('button', { name: 'Sign in' }).click(),
  ]);
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30000 });
  await quiet();
}

// ----------------------------------------------------------------------------------------------------- run

const beats = [];

async function go(path) {
  const base = story.portal ? PORTAL : BASE;
  await page.evaluate((target) => globalThis.history.pushState(null, '', target), path);
  await page.evaluate(() => globalThis.dispatchEvent(new PopStateEvent('popstate')));
  await quiet();
  const here = new URL(page.url());
  if (here.pathname + here.search !== path) {
    await page.goto(`${base}${path}`);
    await quiet();
  }
}

async function runStep(step, id) {
  const beat = { type: 'step', id, action: 'none' };
  if (step.go) {
    await go(step.go);
    beat.action = 'go';
  }
  const tools = { page, find, go, quiet, ask, sentCodes, state, api };
  if (step.before) await step.before(tools);
  let target = null;
  for (const kind of ['click', 'nav', 'fill', 'select', 'check', 'show']) {
    if (step[kind] === undefined) continue;
    const spec = kind === 'fill' || kind === 'select' ? step[kind][0] : kind === 'nav' ? { nav: step.nav } : step[kind];
    target = find(spec);
    await target.waitFor({ state: 'visible', timeout: 15000 });
    await target.scrollIntoViewIfNeeded();
    await quiet(4000);
    beat.action = kind;
    break;
  }
  if (step.press && !target) beat.action = 'press';
  beat.shotBefore = await shot(`${id}-before`);
  if (target) {
    const box = await boxOf(target);
    if (beat.action === 'show') beat.focus = box;
    else beat.target = box;
  }
  if (step.focus) beat.focus = await boxOf(find(step.focus));
  switch (beat.action) {
    case 'click':
    case 'nav':
    case 'check':
      await target.click();
      break;
    case 'fill': {
      const value = step.fill[1];
      const text = String(typeof value === 'function' ? await value(tools) : value);
      // A password is typed on screen (the field shows dots) but never written to the capture.
      beat.text = step.secret ? '•'.repeat(text.length) : text;
      await target.click();
      // A date or time field takes its value whole (2026-11-20), in any display format; it is not typed key by key.
      if (['date', 'time', 'datetime-local', 'month'].includes(await target.evaluate((el) => el.type ?? ''))) {
        await target.fill(text);
        beat.typing = [await shot(`${id}-type`)];
        break;
      }
      await target.fill('');
      const parts = Math.min(6, Math.max(2, Math.ceil(text.length / 4)));
      const size = Math.ceil(text.length / parts);
      beat.typing = [];
      for (let i = 0; i < text.length; i += size) {
        await target.pressSequentially(text.slice(i, i + size), { delay: 10 });
        beat.typing.push(await shot(`${id}-type`));
      }
      break;
    }
    case 'select': {
      if (step.native) {
        // A native <select>: its list is drawn by the system, so the option is chosen by its label (the whole label,
        // or failing that one that contains it), once the options have loaded.
        const wanted = step.select[1];
        let value;
        for (let i = 0; i < 40 && value === undefined; i++) {
          value = await target.evaluate(
            (el, w) =>
              ([...el.options].find((o) => o.label.trim() === w) ?? [...el.options].find((o) => o.label.includes(w)))?.value,
            wanted,
          );
          if (value === undefined) await page.waitForTimeout(250);
        }
        if (value === undefined) throw new Error(`No option "${wanted}" in ${JSON.stringify(step.select[0])}`);
        await target.selectOption(value);
        break;
      }
      await target.click();
      await quiet(3000);
      const option = page.getByRole('option', { name: step.select[1], exact: step.exact ?? true }).first();
      await option.waitFor({ state: 'visible', timeout: 10000 });
      await option.click();
      break;
    }
    default:
      break;
  }
  if (step.press) await page.keyboard.press(step.press);
  if (step.after_do) await step.after_do(tools);
  await quiet();
  if (step.wait) {
    const w = find(step.wait);
    await w.waitFor({ state: 'visible', timeout: 20000 });
    await quiet(3000);
  }
  if (step.expect) {
    for (const e of [].concat(step.expect)) {
      await find(e).waitFor({ state: 'visible', timeout: 20000 });
    }
  }
  // What the narration says after the step is shown on the screen: a result below the fold is scrolled to first.
  const shown = step.result ?? step.wait;
  if (shown && step.after) {
    await find(shown)
      .scrollIntoViewIfNeeded({ timeout: 5000 })
      .then(() => quiet(), () => undefined);
  }
  beat.shotAfter = await shot(`${id}-after`);
  if (shown) {
    beat.result = await boxOf(find(shown));
    if (!beat.result && step.after) beat.offScreen = true;
  }
  for (const key of ['zoom', 'say', 'after']) if (step[key] !== undefined) beat[key] = step[key];
  return beat;
}

const started = Date.now();
let current = null;
try {
  if (story.portal) {
    // The patient's own sign-in is part of what the portal video teaches: its steps do it on screen.
    await page.goto(`${PORTAL}/portal/sign-in`);
    await quiet();
  } else if (story.signIn === 'on-screen') {
    // The video teaches signing in: its first steps type the user name and password.
    await page.goto(`${BASE}/login`);
    await quiet();
  } else {
    await signInStaff(story.user);
  }
  // Before the filmed steps: data other roles would have made earlier in the day, through the API as those roles.
  if (story.setup) await story.setup({ page, find, go, quiet, ask, sentCodes, state, api });
  let n = 0;
  for (const [ci, scene] of story.scenes.entries()) {
    const cid = `c${ci + 1}`;
    beats.push({ type: 'chapter', id: cid, title: scene.title });
    for (const step of scene.steps) {
      current = { scene: scene.title, step };
      const id = stepId(++n);
      beats.push(await runStep(step, id));
    }
  }
} catch (error) {
  await page.screenshot({ path: join(OUT, 'failure.png') }).catch(() => undefined);
  writeFileSync(
    join(OUT, 'failure.json'),
    JSON.stringify({ error: String(error.message ?? error).split('\n').slice(0, 6), at: current, url: page.url(), failures }, null, 1),
  );
  console.error(`✗ ${story.id}: ${String(error.message ?? error).split('\n')[0]}`);
  console.error(`  at "${current?.scene}" › ${JSON.stringify(current?.step?.say ?? '').slice(0, 120)}`);
  await browser.close();
  process.exit(1);
}
await browser.close();

// The words are the storyboard's alone (narration.mjs), so retell.mjs can put them again over these screens.
const segments = narration(story);
const { next, endLines } = ending(story);
if (story.next && !next) console.warn('  next not used: a role video ends with practice (pipeline/narration.mjs)');

const recordedAt = serverDate.toISOString();
const capture = {
  id: story.id,
  title: story.title,
  role: story.role,
  roleLabel: story.roleLabel,
  audience: story.audience,
  hospital: 'Nana Hospitals, Theni',
  wave: story.wave,
  objectives: story.objectives,
  recap: story.recap,
  check: story.check ?? [],
  next,
  modules: story.modules ?? [],
  covers: story.covers ?? [],
  endLines,
  footer: `Fictional patients and data. Recorded on HC360 ${commit()} on ${new Date(recordedAt).toLocaleDateString('en-IN', { day: 'numeric', month: 'long', year: 'numeric' })}.`,
  app: { commit: commit(), recordedAt },
  viewport: VIEWPORT,
  minutes: Math.round((Date.now() - started) / 6000) / 10,
  serverErrors: failures,
  beats,
};
writeFileSync(join(OUT, 'capture.json'), JSON.stringify(capture, null, 1));
writeFileSync(join(OUT, 'segments.json'), JSON.stringify(segments, null, 1));
console.log(`✓ ${story.id}: ${beats.filter((b) => b.type === 'step').length} steps, ${shotNo} screenshots, ${capture.minutes} min`);
