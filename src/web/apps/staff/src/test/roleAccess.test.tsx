import type * as Core from '@hc360/core';
import { hasPermission, type AuthUser } from '@hc360/core';
import { act, cleanup } from '@testing-library/react';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { moduleManifests } from '@/lib/moduleRegistry';
import type { WardDto } from '@/modules/inpatient/api';
import { DEVICES_ANSWERS, SYSTEM_HEALTH_ANSWERS } from './accessibility/fixtures/admin';
import { BILLING_ANSWERS } from './accessibility/fixtures/billing';
import { CLINICAL_ANSWERS } from './accessibility/fixtures/clinical';
import { CALENDAR_ANSWERS, FRONT_DESK_HOME_ANSWERS, FRONT_OFFICE_ANSWERS } from './accessibility/fixtures/frontOffice';
import { PHARMACY_ANSWERS } from './accessibility/fixtures/pharmacy';
import { SHELL_SCREEN_ANSWERS } from './accessibility/fixtures/shell';
import { fakeServer, openScreen, STAFF_USER, stubBrowserApis } from './appScreen';
import type { FakeAnswers } from './fakeApi';

// Every built-in role sees the menu links its permissions allow (manifests). A link the role sees must open a screen it
// can use: one that asks the server only for what the role may read. Each link is opened once with every permission to
// learn what its screen reads; a role whose permissions do not cover one of those reads opens the link again as itself
// (a screen leaves out what its user may not see), and what it still asks for and may not read fails the test.
// serverAccess.json holds the roles' permissions and the server's read rules; StaffAccessFixtureTests keeps it current.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

interface ServerAccess {
  roles: Record<string, string[]>;
  /** "/api/v1/emr/templates/{id:guid} clinical.emr.read|clinical.templates.manage": every comma part must be held. */
  reads: string[];
}

const access = JSON.parse(readFileSync(join(import.meta.dirname, 'serverAccess.json'), 'utf8')) as ServerAccess;

const reads = access.reads.map((line) => {
  const [path, rules] = line.split(' ');
  return { segments: path!.split('/'), rules: rules!.split(',').map((rule) => rule.split('|')) };
});

/** The server's rule for a GET path; a literal segment beats a {parameter}. */
function readRule(path: string) {
  const parts = path.replace(/\/$/, '').split('/');
  const literal = (segments: string[]) => segments.filter((s) => !s.startsWith('{')).length;
  return reads
    .filter(
      (r) =>
        r.segments.length === parts.length &&
        r.segments.every((s, i) => s === parts[i] || (s.startsWith('{') && s.endsWith('}'))),
    )
    .sort((a, b) => literal(b.segments) - literal(a.segments))[0];
}

const mayRead = (path: string, permissions: readonly string[]) =>
  readRule(path)?.rules.every((anyOf) => anyOf.some((p) => permissions.includes(p))) ?? true;

const WARD: WardDto = {
  id: '0192a000-0000-7000-8000-00000000e001',
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
 * these do not feed reads only what it asks for on opening.
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
};

/** Links to a screen of their own; "/emr/patients/$patientId" needs a record and is opened from one. */
const links = moduleManifests.flatMap((m) => m.links.filter((l) => !l.to.includes('$')));

/** The GET paths a screen asks for once it has opened, waiting until it stops asking (up to 3 s). */
async function readsOf(path: string, user: AuthUser): Promise<string[]> {
  const server = fakeServer(ANSWERS);
  openScreen(path, user);
  let seen = -1;
  for (let waited = 0; waited < 3000 && seen !== server.requests.length; waited += 250) {
    seen = server.requests.length;
    await act(() => new Promise((resolve) => setTimeout(resolve, 250)));
  }
  cleanup();
  vi.unstubAllGlobals();
  stubBrowserApis();
  return [...new Set(server.requests.filter((r) => r.method === 'GET').map((r) => r.path))];
}

describe('every role can use the screens its menu shows', () => {
  it('finds the roles and the read rules', () => {
    expect(Object.keys(access.roles)).toContain('FrontOffice');
    expect(readRule('/api/v1/patients')).toBeDefined();
  });

  it('asks the server only for what the role may read', { timeout: 1_200_000 }, async () => {
    stubBrowserApis();
    const everything: Record<string, string[]> = {};
    for (const link of links) {
      everything[link.to] = await readsOf(link.to, STAFF_USER);
    }

    const refused: string[] = [];
    for (const [role, permissions] of Object.entries(access.roles)) {
      if (permissions.includes('*') || role === 'Patient') {
        continue;
      }
      const user: AuthUser = { ...STAFF_USER, roles: [role], permissions };
      for (const link of links.filter((l) => hasPermission(user, l.permission))) {
        if (everything[link.to]!.every((p) => mayRead(p, permissions))) {
          continue;
        }
        const denied = (await readsOf(link.to, user)).filter((p) => !mayRead(p, permissions));
        refused.push(...denied.map((p) => `${role}: "${link.label}" (${link.to}) reads ${p}`));
      }
    }

    expect(refused, `Menu links whose screen asks for what the role may not read:\n${refused.join('\n')}`).toEqual([]);
  });
});
