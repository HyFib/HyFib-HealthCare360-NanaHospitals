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
// that acts at once, opens a confirmation whose last button acts, or opens a menu whose items do either. A role whose permissions do not cover all of that
// opens the screen again as itself (a screen leaves out what its user may not see or do), and what it still asks for,
// or still offers, fails the test. serverAccess.json holds the roles' permissions and the server's read and write rules;
// StaffAccessFixtureTests keeps it current. The roleAccess.*.test.tsx files check a part of the screens each, so they
// run side by side; each mocks the realtime connection away.

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

const enabled = (b: HTMLElement) => !b.hasAttribute('disabled') && b.getAttribute('aria-disabled') !== 'true';

/**
 * Presses `name` on the page ("Trigger › Item" opens the menu, then presses its item), then the last button of the
 * confirmation that opens, if one does. Returns whether a confirmation opened.
 */
async function press(server: ReturnType<typeof fakeServer>, name: string) {
  const [trigger, item] = name.split(MENU);
  const button = within(document.querySelector('main') as HTMLElement)
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
  // A confirmation: its last button is the one that acts.
  const dialog = screen.queryAllByRole('dialog').concat(screen.queryAllByRole('alertdialog')).at(-1);
  const confirm = dialog
    ? within(dialog)
        .queryAllByRole('button')
        .filter((b) => enabled(b) && !NOT_ACTIONS.test(buttonName(b)))
        .at(-1)
    : undefined;
  if (confirm) {
    fireEvent.click(confirm);
    await settle(server);
  }
  return dialog !== undefined;
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

/** Waits until the screen stops asking the server for anything (up to 3 s). */
async function settle(server: ReturnType<typeof fakeServer>) {
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
  await settle(server);
  const learned: Learned = {
    reads: getsOf(server),
    buttons: new Map(),
    unanswered: [...new Set(server.unanswered.filter((r) => r.startsWith('GET ')).map((r) => r.split('?')[0]!))],
  };
  const reopen = async () => {
    close();
    server = fakeServer(ANSWERS);
    opened = openScreen(path, STAFF_USER);
    await settle(server);
  };
  const names = actionButtons();
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
    if (confirmed || items.length > 0 || opened.router.state.location.pathname !== path || sent.length > 0) {
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
  openScreen(path, user);
  await settle(server);
  await press(server, name);
  const sent = writesOf(server, 0);
  close();
  return sent;
}

/** What a screen reads on opening as `user`, and the action buttons it shows them. */
async function asUser(path: string, user: AuthUser): Promise<{ reads: string[]; buttons: string[] }> {
  const server = fakeServer(ANSWERS);
  openScreen(path, user);
  await settle(server);
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
      for (const [name] of doubtful.filter(([n]) => seen.buttons.includes(n.split(MENU)[0]!))) {
        refused.push(
          ...(await pressAs(s.to, user, name))
            .filter((w) => !mayWrite(w, permissions))
            .map((w) => `${role}: "${s.label}" (${s.to}) shows "${name}", which sends ${w}`),
        );
      }
    }
  }
  return { learned, refused };
}
