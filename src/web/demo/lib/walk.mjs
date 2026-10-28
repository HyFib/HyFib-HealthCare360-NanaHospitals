// Walks one role's part of the staff app in Chromium, as its user would look round it: every item of the role's own
// menu (its permissions decide what is there), every tab of each screen, the filters left on "choose…", the first
// row's drawer, the first records each list links to, and every patient's record and chart the role may read. It
// opens; it never presses a button that changes anything.
//
// capture.mjs walks the real server this way and keeps what it answers; verify.mjs walks the demo build the same
// way and checks every screen got its answers. Both pass how to navigate, wait and tell where the app is.

/** Records of one kind a menu screen's links lead to: the first two stand for the rest. */
export const PER_TEMPLATE = 2;
/** Links followed from a patient's record or chart: their visits, admissions, bills, results and prescriptions. */
export const PER_RECORD = 8;

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;
/** A path with its ids taken out: /patients/:id, so a list's first records stand for the rest. */
export const template = (path) =>
  path
    .split('?')[0]
    .replace(GUID, ':id')
    .replace(/\/\d+(?=\/|$)/g, '/:n');
const exactly = (text) => new RegExp(`^\\s*${text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*$`);

/**
 * @param {import('@playwright/test').Page} page signed in as the role, on its home screen
 * @param {object} how
 * @param {() => string} how.here the app's path now (/patients?tab=visits)
 * @param {(path: string) => Promise<void>} how.go opens a path as a link inside the app does, then waits
 * @param {() => Promise<void>} how.quiet waits for the screen's calls to be answered
 * @param {{ permissions: string[] }} how.user the signed-in user
 * @param {string[]} how.patients every patient's id
 * @param {() => boolean} how.outOfTime
 * @param {(path: string) => void} [how.onScreen] told of each screen as the walk reaches it
 */
export async function walkRole(page, { here, go, quiet, user, patients, outOfTime, onScreen = () => undefined }) {
  const visited = [];
  const followed = new Set();
  const perTemplate = new Map();
  const queue = [];

  /** Closes what is open over the screen; "Leave without saving?" is answered with leaving, as the walk moves on. */
  async function closeOverlays() {
    for (let i = 0; i < 3; i++) {
      const open = page.locator('[role="dialog"]:visible, [role="alertdialog"]:visible');
      if (!(await open.count())) return;
      const guard = open.filter({ has: page.getByRole('button', { name: 'Stay', exact: true }) }).last();
      if (await guard.count()) {
        await guard
          .getByRole('button')
          .filter({ hasNotText: /^\s*Stay\s*$/ })
          .last()
          .click({ timeout: 3000 });
      } else {
        await page.keyboard.press('Escape');
      }
      await page.waitForTimeout(250);
    }
  }

  async function open(path) {
    await closeOverlays();
    await go(path);
    await closeOverlays();
  }

  async function collectLinks(depth, limit) {
    if (depth >= 2) return;
    const current = here();
    const hrefs = await page.$$eval('#main a[href^="/"], #main a[href^="#/"]', (anchors) =>
      anchors.map((a) => a.getAttribute('href').replace(/^#/, '')),
    );
    for (const href of new Set(hrefs)) {
      if (!href || href.startsWith('/api/') || href === current || followed.has(href)) continue;
      const key = template(href);
      const count = perTemplate.get(key) ?? 0;
      if (count >= limit) continue;
      perTemplate.set(key, count + 1);
      followed.add(href);
      queue.push({ href, depth: depth + 1 });
    }
  }

  /** The first row of each list, opened as a user would: a drawer, or the record's own screen. */
  async function openFirstRow(path) {
    const row = page
      .locator('#main table tbody tr, #main [role="grid"] [role="row"]:not(:has([role="columnheader"]))')
      .first();
    try {
      if (!(await row.count()) || !(await row.isVisible())) return;
      const cells = row.locator('td, [role="gridcell"]');
      const cellCount = Math.min(await cells.count(), 4);
      for (let i = 0; i < cellCount; i++) {
        const cell = cells.nth(i);
        if ((await cell.locator('button, a, input, select, textarea, [role="checkbox"]').count()) > 0) continue;
        if (!(await cell.innerText()).trim()) continue;
        await cell.click({ timeout: 3000 });
        await quiet();
        if (here() !== path) {
          visited.push(here());
          onScreen(here());
          await open(path);
        } else {
          await closeOverlays();
        }
        return;
      }
    } catch {
      // A row that went away with a refresh.
    }
  }

  /** Each filter left on "choose…" gets its first choice: a ward, a store, a department, a status. */
  async function chooseFilters(depth, limit) {
    // Filters only: a choice in a form would leave it with unsaved changes, and the screen would ask before leaving.
    const selects = page.locator('#main select:visible:not(form select)');
    const count = Math.min(await selects.count(), 6);
    for (let i = 0; i < count; i++) {
      const select = selects.nth(i);
      try {
        if (await select.isDisabled()) continue;
        const choice = await select.evaluate((element) =>
          element.value === ''
            ? ([...element.options].find((o) => o.value !== '' && !o.disabled)?.value ?? null)
            : null,
        );
        if (choice === null) continue;
        await select.selectOption(choice, { timeout: 3000 });
        await quiet();
        await collectLinks(depth, limit);
      } catch {
        // A select that went away with the screen it was on.
      }
    }
  }

  async function explore(depth, limit = PER_TEMPLATE) {
    const path = here();
    visited.push(path);
    onScreen(path);
    await closeOverlays();
    await collectLinks(depth, limit);
    await openFirstRow(path);
    await chooseFilters(depth, limit);
    // Every tab of the screen: each loads what it shows when chosen.
    const tabs = page.locator('#main [role="tab"]');
    const tabCount = Math.min(await tabs.count(), 16);
    for (let i = 0; i < tabCount; i++) {
      const tab = tabs.nth(i);
      try {
        if ((await tab.getAttribute('aria-selected')) === 'true' || !(await tab.isVisible())) continue;
        await tab.click({ timeout: 3000 });
        await quiet();
        await closeOverlays();
        await collectLinks(depth, limit);
        await openFirstRow(here());
        await chooseFilters(depth, limit);
      } catch {
        // A tab that went away with the data that showed it.
      }
    }
  }

  async function drain(limit) {
    while (queue.length && !outOfTime()) {
      const next = queue.shift();
      await open(next.href);
      await explore(next.depth, limit);
    }
  }

  // Home, as the role lands on it after signing in.
  await quiet();
  await explore(0);
  await drain(PER_TEMPLATE);

  // The role's menu, as its user sees it: each category opened (one at a time, as the menu does), each item chosen.
  await open('/');
  const nav = page.getByRole('navigation', { name: 'Main navigation' });
  const plan = [];
  for (const top of await nav.locator('.fui-NavItem, .fui-NavCategoryItem').all()) {
    const label = (await top.innerText()).trim();
    if ((await top.getAttribute('aria-expanded')) === null) {
      plan.push({ label });
      continue;
    }
    if ((await top.getAttribute('aria-expanded')) !== 'true') {
      await top.click();
      await page.waitForTimeout(300);
    }
    for (const item of await nav.locator('.fui-NavSubItem:visible').allInnerTexts()) {
      plan.push({ category: label, label: item.trim() });
    }
  }
  const menu = [];
  for (const entry of plan) {
    if (outOfTime()) {
      menu.push({ ...entry, skipped: 'out of time' });
      continue;
    }
    try {
      await closeOverlays();
      // A screen of its own (the queue display board, for a TV) has no menu: home first.
      if (!(await nav.count())) {
        await open('/');
      }
      if (entry.category) {
        const category = nav
          .locator('.fui-NavCategoryItem')
          .filter({ hasText: exactly(entry.category) })
          .first();
        if ((await category.getAttribute('aria-expanded')) !== 'true') {
          await category.click();
          await page.waitForTimeout(300);
        }
        await nav
          .locator('.fui-NavSubItem:visible')
          .filter({ hasText: exactly(entry.label) })
          .first()
          .click();
      } else {
        await nav
          .locator('.fui-NavItem')
          .filter({ hasText: exactly(entry.label) })
          .first()
          .click();
      }
      await page.waitForTimeout(300);
      await closeOverlays();
      await quiet();
      menu.push({ ...entry, path: here() });
      await explore(0);
      await drain(PER_TEMPLATE);
    } catch (error) {
      menu.push({
        ...entry,
        error: String(error.message ?? error)
          .split('\n')[0]
          .slice(0, 200),
      });
    }
  }

  // Every patient's record and chart, as far as the role may read them, and what they link to.
  const may = (permission) => user.permissions.includes('*') || user.permissions.includes(permission);
  const records = [
    ...(may('patients.read') ? patients.map((id) => `/patients/${id}`) : []),
    ...(may('clinical.emr.read') ? patients.map((id) => `/emr/patients/${id}`) : []),
  ];
  for (const path of records) {
    if (outOfTime()) break;
    if (visited.includes(path)) continue;
    followed.add(path);
    await open(path);
    await explore(0, PER_RECORD);
    await drain(PER_RECORD);
  }

  await quiet();
  return { menu, visited: [...new Set(visited)] };
}
