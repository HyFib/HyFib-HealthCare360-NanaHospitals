// Shows a storyboard's author what a screen says to a screen reader, signed in as a role: the accessible names the
// storyboard points at (a button's name, a field's label) are read from here, not guessed. Also saves a picture.
//
//   node training/pipeline/explore.mjs <user> <path> [out.png] [--click "Button name"]... [--full]
//
// Prints the ARIA snapshot of the main region (and with --full, the whole page: menu, top bar).

import { createRequire } from 'node:module';

const { chromium } = createRequire(import.meta.url)('playwright');
const BASE = process.env.HC360_SERVER ?? 'https://localhost:7360';
const PASSWORD = process.env.HC360_DEMO_PASSWORD;
const [user, path, png, ...rest] = process.argv.slice(2);
const actions = [];
let full = false;
let signIn = true;
for (let i = 0; i < rest.length; i++) {
  if (rest[i] === '--click') actions.push(['click', rest[++i]]);
  else if (rest[i] === '--press') actions.push(['press', rest[++i]]);
  else if (rest[i] === '--full') full = true;
  else if (rest[i] === '--no-signin') signIn = false;
}

const browser = await chromium.launch({ args: ['--lang=en-IN'], env: { ...process.env, LANG: 'en_IN.UTF-8', LANGUAGE: 'en_IN' } });
const context = await browser.newContext({
  ignoreHTTPSErrors: true,
  viewport: { width: 1440, height: 810 },
  locale: 'en-IN',
  timezoneId: 'Asia/Kolkata',
});
// The server may be restarting for someone's capture (check.sh, make.sh): wait for it, up to 90 s.
let date;
for (let i = 0; !date; i++) {
  try {
    date = new Date((await fetch(`${BASE}/health/live`)).headers.get('date'));
  } catch (error) {
    if (i > 90) throw error;
    await new Promise((r) => setTimeout(r, 1000));
  }
}
await context.clock.install({ time: date });
const page = await context.newPage();
await page.goto(`${BASE}/login`);
if (signIn) {
  await page.locator('input[autocomplete="username"]').fill(user);
  await page.locator('input[autocomplete="current-password"]').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 30000 });
}
await page.waitForLoadState('networkidle');
if (signIn && path && path !== '/') {
  await page.evaluate((t) => {
    history.pushState(null, '', t);
    dispatchEvent(new PopStateEvent('popstate'));
  }, path);
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(1200);
}
for (const [kind, name] of actions) {
  if (kind === 'press') {
    await page.keyboard.press(name);
    await page.waitForTimeout(900);
    continue;
  }
  const target = name.startsWith('text=')
    ? page.getByText(name.slice(5), { exact: true }).first()
    : page.getByRole(name.match(/^(link|tab|button|menuitem|option|row|checkbox|radio)=/)?.[1] ?? 'button', {
        name: name.replace(/^(link|tab|button|menuitem|option|row|checkbox|radio)=/, ''),
        exact: true,
      }).first();
  await target.click();
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(900);
}
const root = full || !signIn ? page.locator('body') : page.locator('#main, main').first();
console.log(`# ${user} ${page.url()}`);
console.log(await root.ariaSnapshot());
const dialogs = page.locator('[role="dialog"]:visible, [role="menu"]:visible, [role="listbox"]:visible');
if (await dialogs.count()) {
  console.log('# dialog');
  console.log(await dialogs.last().ariaSnapshot());
}
if (png) await page.screenshot({ path: png });
await browser.close();
