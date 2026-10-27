import { hasPermission, type AuthUser } from '@hc360/core';
import { act, cleanup, fireEvent, screen, within } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { createMemoryHistory } from '@tanstack/react-router';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { vi } from 'vitest';
import { moduleManifests } from '@/lib/moduleRegistry';
import type { WardDto } from '@/modules/inpatient/api';
import { createAppRouter } from '@/router';
import { DEVICES_ANSWERS, SYSTEM_HEALTH_ANSWERS } from './accessibility/fixtures/admin';
import { BILLING_ANSWERS } from './accessibility/fixtures/billing';
import { CLINICAL_ANSWERS } from './accessibility/fixtures/clinical';
import { CALENDAR_ANSWERS, FRONT_DESK_HOME_ANSWERS, FRONT_OFFICE_ANSWERS } from './accessibility/fixtures/frontOffice';
import { PHARMACY_ANSWERS } from './accessibility/fixtures/pharmacy';
import { SHELL_SCREEN_ANSWERS } from './accessibility/fixtures/shell';
import { fakeServer, openScreen, STAFF_USER, stubBrowserApis } from './appScreen';
import type { FakeAnswers } from './fakeApi';
import { RECORD_ID, WARD_ID } from './roleAccessIds';

// Every built-in role sees the menu links its permissions allow (manifests), and the record screens those links open
// (a bill from the bills list, a chart from the charts list). A screen the role can reach must be one it can use:
//
// - it asks the server only for what the role may read;
// - each action button the role is shown sends only what the role may do.
//
// Each screen is opened once with every permission, to learn what it reads and what each of its buttons sends: a button
// that acts at once, opens a confirmation whose last button acts, or opens a menu whose items do either. A role whose
// permissions do not cover all of that opens the screen again as itself (a screen leaves out what its user may not see
// or do), and what it still asks for, or still offers, fails the test. serverAccess.json holds the roles' permissions
// and the server's read and write rules; StaffAccessFixtureTests keeps it current. The roleAccess.*.test.tsx files
// check a part of the screens each, so they run side by side; each mocks the realtime connection and the charts away
// (jsdom has no canvas to draw on).

interface ServerAccess {
  roles: Record<string, string[]>;
  /** "/api/v1/emr/templates/{id:guid} clinical.emr.read|clinical.templates.manage": every comma part must be held. */
  reads: string[];
  /** "POST /api/v1/mrd/records/{id:guid}/complete mrd.records", as for reads. */
  writes: string[];
}

export const access = JSON.parse(readFileSync(join(import.meta.dirname, 'serverAccess.json'), 'utf8')) as ServerAccess;

interface Rule {
  segments: string[];
  rules: string[][];
}

const rule = (path: string, rules: string): Rule => ({
  segments: path.split('/'),
  rules: rules.split(',').map((r) => r.split('|')),
});

export const reads = access.reads.map((line) => {
  const [path, rules] = line.split(' ');
  return rule(path!, rules!);
});

export const writes = new Map<string, Rule[]>();
for (const line of access.writes) {
  const [method, path, rules] = line.split(' ');
  writes.set(method!, [...(writes.get(method!) ?? []), rule(path!, rules!)]);
}

/** The server's rule for a path among `rules`; a literal segment beats a {parameter}. */
export function ruleFor(rules: readonly Rule[], path: string) {
  const parts = path.replace(/\/$/, '').split('/');
  const literal = (segments: string[]) => segments.filter((s) => !s.startsWith('{')).length;
  return rules
    .filter(
      (r) =>
        r.segments.length === parts.length &&
        r.segments.every((s, i) => s === parts[i] || (s.startsWith('{') && s.endsWith('}'))),
    )
    .sort((a, b) => literal(b.segments) - literal(a.segments))[0];
}

const allowed = (found: Rule | undefined, permissions: readonly string[]) =>
  found?.rules.every((anyOf) => anyOf.some((p) => permissions.includes(p))) ?? true;

const mayRead = (path: string, permissions: readonly string[]) => allowed(ruleFor(reads, path), permissions);

/** `write` is "POST /api/v1/..." as sent. */
const mayWrite = (write: string, permissions: readonly string[]) => {
  const [method, path] = write.split(' ');
  return allowed(ruleFor(writes.get(method!) ?? [], path!), permissions);
};

const WARD: WardDto = {
  id: WARD_ID,
  code: 'GWM',
  name: 'General Ward — Male',
  type: 'General',
  floor: '1',
  block: null,
  genderPolicy: 'MaleOnly',
  nurseStation: null,
  phone: null,
  departmentId: null,
  bedServiceItemId: null,
  bedServiceCode: null,
  displayOrder: 0,
  isActive: true,
  notes: null,
  totalBeds: 12,
  occupiedBeds: 0,
  version: 1,
};

/**
 * Data for the screens to work with, so that what a screen reads only once an earlier answer came back (a ward's rooms
 * after the wards) is asked for too: the screen tests' canned answers, and a ward for the in-patient screens. A screen
 * these do not feed (most of them: Learned.unanswered lists what each went without) is checked for what it reads on
 * opening and the buttons it shows with no records, not for the actions on a record's row; a record screen whose record
 * they do not answer shows its "not found" state, so only its first read is checked.
 */
const ANSWERS: FakeAnswers = {
  ...SHELL_SCREEN_ANSWERS,
  ...SYSTEM_HEALTH_ANSWERS,
  ...DEVICES_ANSWERS,
  ...FRONT_OFFICE_ANSWERS,
  ...FRONT_DESK_HOME_ANSWERS,
  ...CALENDAR_ANSWERS,
  ...BILLING_ANSWERS,
  ...CLINICAL_ANSWERS,
  ...PHARMACY_ANSWERS,
  'GET /api/v1/inpatient/wards': [WARD],
  // roleAccess/<module>.ts: each module's lists and records, for the screens the answers above do not feed.
  ...Object.assign(
    {},
    ...Object.values(import.meta.glob<{ ANSWERS: FakeAnswers }>('./roleAccess/*.ts', { eager: true })).map(
      (m) => m.ANSWERS,
    ),
  ),
};

export { RECORD_ID };

/**
 * The screens that may open without all of their data (path → why): what each one goes without is not checked. Any
 * other screen whose data the answers above miss, or that asks for nothing at all, fails the check, and so does a screen
 * listed here that no longer goes without: a screen cannot quietly fall back to an empty list.
 */
export const WITHOUT_DATA: Record<string, string> = {};

/** A screen to open: a menu link, or a record screen with the permission of the list it is opened from. */
interface Screen {
  label: string;
  to: string;
  permission: Parameters<typeof hasPermission>[1];
}

const links = moduleManifests.flatMap((m) => m.links);

/**
 * The record screens (routes with a $parameter), each reached from the menu link above it: the longest link whose path
 * starts its path (/mrd/records/$recordId from /mrd/records). Opened with a made-up id; `$name` is an integration.
 */
function recordScreens(): Screen[] {
  const router = createAppRouter(new QueryClient(), createMemoryHistory());
  const leaves = Object.values(
    router.routesById as unknown as Record<string, { id: string; fullPath: string; children?: unknown }>,
  )
    .filter((r) => r.id.startsWith('/_app/') && r.fullPath.includes('$'))
    .filter((r) => !r.children || Object.keys(r.children).length === 0);
  return leaves.map((route) => {
    const segments = route.fullPath.replace(/\/$/, '').split('/');
    const firstParameter = segments.findIndex((s) => s.startsWith('$'));
    let parent: (typeof links)[number] | undefined;
    for (let n = firstParameter; n > 1 && !parent; n--) {
      const prefix = segments.slice(0, n).join('/');
      parent = links.find((l) => l.to === prefix);
    }
    if (!parent) {
      throw new Error(`${route.fullPath} has no menu link above it: say which link opens it.`);
    }
    const to = segments.map((s) => (s === '$name' ? 'whatsapp' : s.startsWith('$') ? RECORD_ID : s)).join('/');
    return { label: `${parent.label} → ${route.fullPath}`, to, permission: parent.permission };
  });
}

export const screens: Screen[] = [
  ...links.filter((l) => !l.to.includes('$')).map((l) => ({ label: l.label, to: l.to, permission: l.permission })),
  ...recordScreens(),
];

/** Buttons that leave a screen as it was. */
const NOT_ACTIONS = /^(cancel|close|back|dismiss|keep editing|stay)$/i;

const buttonName = (button: HTMLElement) =>
  (button.getAttribute('aria-label') ?? button.textContent ?? '').replace(/\s+/g, ' ').trim();

/** The page's buttons a user may press (not those of the shell), by name, once each. */
function actionButtons(): string[] {
  const main = document.querySelector('main');
  if (!main) {
    return [];
  }
  const names = within(main as HTMLElement)
    .queryAllByRole('button')
    .filter(enabled)
    .map(buttonName)
    .filter((name) => name && !NOT_ACTIONS.test(name));
  return [...new Set(names)];
}

/** A menu item is learned and pressed as "Trigger › Item". */
const MENU = ' › ';

/** A button of what a table's first clickable row opens (a drawer, or a panel on the page) is "First row › Button". */
const ROW = `First row${MENU}`;

const enabled = (b: HTMLElement) => !b.hasAttribute('disabled') && b.getAttribute('aria-disabled') !== 'true';

const dialogs = () => screen.queryAllByRole('dialog').concat(screen.queryAllByRole('alertdialog'));

/** The first row of a table on the page that opens something when clicked. */
const clickableRow = () =>
  (document.querySelector('main')?.querySelector('tbody tr[tabindex="0"]') as HTMLElement | null) ?? null;

/** Where a row's buttons are: the drawer or dialog it opened, or else the page. */
const rowPanel = (before: readonly HTMLElement[]) =>
  dialogs()
    .filter((d) => !before.includes(d))
    .at(-1) ?? (document.querySelector('main') as HTMLElement);

/** Today, as a date or date-and-time input takes it. */
const today = () => new Date().toISOString().slice(0, 10);

/** A value a form will take for a field: what the field is for, from its type and its label. */
function sampleValue(input: HTMLInputElement | HTMLTextAreaElement) {
  const label = `${input.getAttribute('aria-label') ?? ''} ${input.labels?.[0]?.textContent ?? ''} ${input.name}`;
  switch (input.type) {
    case 'number': {
      const min = Number(input.getAttribute('min') ?? 1);
      return String(min > 1 ? min : 1);
    }
    case 'date':
      return today();
    case 'datetime-local':
      return `${today()}T09:00`;
    case 'time':
      return '09:00';
    case 'month':
      return today().slice(0, 7);
    case 'email':
      return 'test@example.com';
    case 'tel':
      return '9876543210';
    case 'url':
      return 'https://example.com';
  }
  if (/email/i.test(label)) return 'test@example.com';
  if (/phone|mobile/i.test(label)) return '9876543210';
  if (/pin ?code|pincode/i.test(label)) return '600001';
  return 'Test entry';
}

const isRequired = (input: Element) => input.hasAttribute('required') || input.getAttribute('aria-required') === 'true';

/**
 * Fills the empty fields of a form (its required ones, or all of them), so a button that waits for input can be pressed:
 * a text or number field gets a value of its kind, a list its first choice, a required checkbox a tick, a group of
 * options its first one.
 */
function fillFields(form: HTMLElement, all: boolean) {
  for (const input of form.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('input, textarea')) {
    if (input.disabled || input.readOnly || !(all || isRequired(input))) {
      continue;
    }
    if (input.type === 'checkbox') {
      if (!(input as HTMLInputElement).checked) fireEvent.click(input);
    } else if (input.type === 'radio') {
      const group = form.querySelectorAll<HTMLInputElement>(`input[type="radio"][name="${input.name}"]`);
      if (![...group].some((r) => r.checked)) fireEvent.click(input);
    } else if (
      !['hidden', 'file', 'search'].includes(input.type) &&
      input.value === '' &&
      input.getAttribute('role') !== 'combobox'
    ) {
      fireEvent.change(input, { target: { value: sampleValue(input) } });
    }
  }
  for (const select of form.querySelectorAll('select')) {
    const first = [...select.options].find((o) => o.value !== '' && !o.disabled);
    if (!select.disabled && select.value === '' && first && (all || isRequired(select))) {
      fireEvent.change(select, { target: { value: first.value } });
    }
  }
}

/**
 * Presses the button of `dialog` that acts, its last: as it opens, then with its required fields filled, then with all
 * of them, until it sends something or closes. A button still waiting for input after that leaves the last one that
 * can be pressed to act, as before.
 */
async function confirmIn(server: ReturnType<typeof fakeServer>, dialog: HTMLElement) {
  const from = server.requests.length;
  const acts = () =>
    within(dialog)
      .queryAllByRole('button')
      .filter((b) => !NOT_ACTIONS.test(buttonName(b)));
  for (const fill of [undefined, 'required', 'all'] as const) {
    if (fill) {
      fillFields(dialog, fill === 'all');
      await settle(server);
    }
    const submit = acts().at(-1);
    if (submit && enabled(submit)) {
      fireEvent.click(submit);
      await settle(server);
    }
    if (!dialog.isConnected || writesOf(server, from).length > 0) {
      return;
    }
  }
  const fallback = acts().filter(enabled).at(-1);
  if (fallback && fallback !== acts().at(-1)) {
    fireEvent.click(fallback);
    await settle(server);
  }
}

/**
 * Presses `name` on the page ("Trigger › Item" opens the menu, then presses its item; "First row › Button" clicks the
 * first clickable row, then the button in what it opened), then the button that acts in the confirmation or form that
 * opens, if one does. Returns whether one opened.
 */
async function press(server: ReturnType<typeof fakeServer>, name: string) {
  let panel = document.querySelector('main') as HTMLElement;
  let before = dialogs();
  if (name.startsWith(ROW)) {
    const row = clickableRow();
    if (!row) {
      return false;
    }
    fireEvent.click(row);
    await settle(server);
    panel = rowPanel(before);
    before = dialogs();
    name = name.slice(ROW.length);
  }
  const [trigger, item] = name.split(MENU);
  const button = within(panel)
    .queryAllByRole('button')
    .find((b) => buttonName(b) === trigger && enabled(b));
  if (!button) {
    return false;
  }
  fireEvent.click(button);
  await settle(server);
  if (item !== undefined) {
    const menuItem = screen.queryAllByRole('menuitem').find((m) => buttonName(m) === item && enabled(m));
    if (!menuItem) {
      return false;
    }
    fireEvent.click(menuItem);
    await settle(server);
  }
  // A confirmation or form that the button opened (not one already open, such as a row's drawer).
  const dialog = dialogs()
    .filter((d) => !before.includes(d))
    .at(-1);
  if (dialog) {
    await confirmIn(server, dialog);
  }
  return dialog !== undefined;
}

/**
 * The buttons of what the first clickable row opens, as "First row › Button": those of the drawer or dialog it opens,
 * or those it adds to the page. None when the row opens another screen (a record screen, checked as one) or nothing.
 */
async function rowButtons(server: ReturnType<typeof fakeServer>, opened: Opened): Promise<string[]> {
  const row = clickableRow();
  if (!row) {
    return [];
  }
  const start = opened.router.state.location.pathname;
  const onPage = new Set(actionButtons());
  const before = dialogs();
  fireEvent.click(row);
  await settle(server);
  if (opened.router.state.location.pathname !== start) {
    return [];
  }
  const panel = rowPanel(before);
  const names = within(panel)
    .queryAllByRole('button')
    .filter(enabled)
    .map(buttonName)
    .filter((name) => name && !NOT_ACTIONS.test(name) && (panel.tagName !== 'MAIN' || !onPage.has(name)));
  return [...new Set(names)].map((name) => `${ROW}${name}`);
}

/** The items of the menu open on the page, by name. */
const menuItems = () => [
  ...new Set(
    screen
      .queryAllByRole('menuitem')
      .filter(enabled)
      .map(buttonName)
      .filter((name) => name && !NOT_ACTIONS.test(name)),
  ),
];

type Opened = ReturnType<typeof openScreen>;

/**
 * Waits until the screen stops asking the server for anything (up to 3 s). Given the screen just `opened`, first waits
 * (up to 10 s) for its route to load: the first screen of a run waits for its code while it asks for nothing, and would
 * otherwise be taken as a screen that reads nothing and has no buttons.
 */
async function settle(server: ReturnType<typeof fakeServer>, opened?: Opened) {
  for (
    let waited = 0;
    opened && waited < 10_000 && (opened.router.state.status === 'pending' || opened.router.state.isLoading);
    waited += 50
  ) {
    await act(() => new Promise((resolve) => setTimeout(resolve, 50)));
  }
  let seen = -1;
  for (let waited = 0; waited < 3000 && seen !== server.requests.length; waited += 250) {
    seen = server.requests.length;
    await act(() => new Promise((resolve) => setTimeout(resolve, 250)));
  }
}

function close() {
  cleanup();
  vi.unstubAllGlobals();
  stubBrowserApis();
  vi.stubGlobal('print', () => undefined);
  vi.stubGlobal('open', () => null);
  vi.stubGlobal('confirm', () => true);
  URL.createObjectURL = () => 'blob:test';
  URL.revokeObjectURL = () => undefined;
  // jsdom does not lay pages out, so has no scrollIntoView (a tile that scrolls to its list).
  Element.prototype.scrollIntoView = () => undefined;
}

const getsOf = (server: ReturnType<typeof fakeServer>, from = 0) => [
  ...new Set(
    server.requests
      .slice(from)
      .filter((r) => r.method === 'GET')
      .map((r) => r.path),
  ),
];

const writesOf = (server: ReturnType<typeof fakeServer>, from: number) => [
  ...new Set(
    server.requests
      .slice(from)
      .filter((r) => r.method !== 'GET')
      .map((r) => `${r.method} ${r.path}`),
  ),
];

/** What a screen reads on opening, its buttons, and what each sends when pressed with every permission. */
export interface Learned {
  reads: string[];
  buttons: Map<string, string[]>;
  /** What it read on opening that the canned answers do not answer (a 404): it opened without that data. */
  unanswered: string[];
}

export async function learn(path: string): Promise<Learned> {
  let server = fakeServer(ANSWERS);
  let opened = openScreen(path, STAFF_USER);
  await settle(server, opened);
  // Where the screen opened, search included: a tab or step button changes only the search, and the view with it.
  const start = opened.router.state.location.href;
  const learned: Learned = {
    reads: getsOf(server),
    buttons: new Map(),
    unanswered: [...new Set(server.unanswered.filter((r) => r.startsWith('GET ')).map((r) => r.split('?')[0]!))],
  };
  const reopen = async () => {
    close();
    server = fakeServer(ANSWERS);
    opened = openScreen(path, STAFF_USER);
    await settle(server, opened);
  };
  const names = actionButtons();
  // A row clicked leaves the screen in another state (a drawer open, or another screen): back to it as it opened.
  if (clickableRow()) {
    names.push(...(await rowButtons(server, opened)));
    await reopen();
  }
  for (let i = 0; i < names.length; i++) {
    const name = names[i]!;
    const from = server.requests.length;
    const confirmed = await press(server, name);
    const sent = writesOf(server, from);
    if (sent.length > 0) {
      learned.buttons.set(name, sent);
    }
    // A menu opened: each of its items is pressed in turn.
    const items = !name.includes(MENU) && sent.length === 0 ? menuItems() : [];
    names.splice(i + 1, 0, ...items.map((item) => `${name}${MENU}${item}`));
    // Back to the screen as it opened, for the next button.
    if (
      confirmed ||
      items.length > 0 ||
      name.startsWith(ROW) ||
      opened.router.state.location.href !== start ||
      sent.length > 0
    ) {
      await reopen();
    }
  }
  close();
  return learned;
}

/**
 * Opens a screen as `user`, presses `name` as `learn` does and returns what was sent. The same button can do less for
 * a role (a dialog that leaves out Acknowledge, a menu without Reset password).
 */
async function pressAs(path: string, user: AuthUser, name: string): Promise<string[]> {
  const server = fakeServer(ANSWERS);
  await settle(server, openScreen(path, user));
  await press(server, name);
  const sent = writesOf(server, 0);
  close();
  return sent;
}

/** What a screen reads on opening as `user`, and the action buttons it shows them. */
async function asUser(path: string, user: AuthUser): Promise<{ reads: string[]; buttons: string[] }> {
  const server = fakeServer(ANSWERS);
  await settle(server, openScreen(path, user));
  const seen = { reads: getsOf(server), buttons: actionButtons() };
  close();
  return seen;
}

/**
 * Opens every `parts`-th screen from `part` with every permission to learn it, then as each built-in role that can
 * reach it whose permissions do not cover all of it; returns what each screen was learned to read and send, and what a
 * role was refused.
 */
export async function findRefusals(part: number, parts: number) {
  close();
  const mine = screens.filter((_, i) => i % parts === part);
  const learned = new Map<string, Learned>();
  for (const s of mine) {
    learned.set(s.to, await learn(s.to));
  }

  const refused: string[] = [];
  for (const [role, permissions] of Object.entries(access.roles)) {
    if (permissions.includes('*') || role === 'Patient') {
      continue;
    }
    const user: AuthUser = { ...STAFF_USER, roles: [role], permissions };
    for (const s of mine.filter((x) => hasPermission(user, x.permission))) {
      const all = learned.get(s.to)!;
      const doubtful = [...all.buttons].filter(([, sent]) => sent.some((w) => !mayWrite(w, permissions)));
      if (all.reads.every((p) => mayRead(p, permissions)) && doubtful.length === 0) {
        continue;
      }
      const seen = await asUser(s.to, user);
      refused.push(
        ...seen.reads.filter((p) => !mayRead(p, permissions)).map((p) => `${role}: "${s.label}" (${s.to}) reads ${p}`),
      );
      // A row's buttons are not on the page as it opens: each is pressed as the role, and does nothing if not shown.
      for (const [name] of doubtful.filter(([n]) => n.startsWith(ROW) || seen.buttons.includes(n.split(MENU)[0]!))) {
        refused.push(
          ...(await pressAs(s.to, user, name))
            .filter((w) => !mayWrite(w, permissions))
            .map((w) => `${role}: "${s.label}" (${s.to}) shows "${name}", which sends ${w}`),
        );
      }
    }
  }
  // Screens that opened without their data, or asked for nothing (not loaded in time), against WITHOUT_DATA.
  const gaps: string[] = [];
  for (const s of mine) {
    const l = learned.get(s.to)!;
    const empty = l.reads.length === 0 && l.buttons.size === 0;
    const without = l.unanswered.length > 0 || empty;
    if (without && !(s.to in WITHOUT_DATA)) {
      gaps.push(`"${s.label}" (${s.to}) ${empty ? 'asked for nothing' : `went without ${l.unanswered.join(', ')}`}`);
    } else if (!without && s.to in WITHOUT_DATA) {
      gaps.push(`"${s.label}" (${s.to}) has all its data now: take it out of WITHOUT_DATA`);
    }
  }
  return { learned, refused, gaps };
}
