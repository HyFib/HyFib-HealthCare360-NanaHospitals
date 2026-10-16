import type { AuthUser, PagedResult } from '@hc360/core';
import { createFakeFetch as createSharedFakeFetch, type FakeAnswers } from '@hc360/ui/testing/fake-api';

// A fake HC360 server for whole-screen tests: canned JSON by method and path. No test framework in here, so the same
// answers also feed the built app in a real browser (a11y/*.spec.ts).

export { json, problem, type FakeAnswer, type FakeAnswers, type FakeRequest } from '@hc360/ui/testing/fake-api';

export const FACILITY_ID = '0192a000-0000-7000-8000-0000000000f1';

/** A super administrator ("*"): every module and every screen. */
export const STAFF_USER: AuthUser = {
  id: '0192a000-0000-7000-8000-0000000000a1',
  userName: 'meera.iyer',
  displayName: 'Dr. Meera Iyer',
  type: 'staff',
  facilityId: FACILITY_ID,
  facilityName: 'Nana Hospitals, Pune',
  practitionerId: '0192a000-0000-7000-8000-0000000000d1',
  patientId: null,
  roles: ['SuperAdministrator'],
  permissions: ['*'],
  facilities: [
    { id: FACILITY_ID, code: 'NHP', name: 'Nana Hospitals, Pune' },
    { id: '0192a000-0000-7000-8000-0000000000f2', code: 'NHM', name: 'Nana Hospitals, Mumbai' },
  ],
  mfaEnabled: true,
  hasPin: true,
  mustChangePassword: false,
  restriction: null,
};

export function paged<T>(items: T[]): PagedResult<T> {
  return { items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 };
}

/** What the shell asks for on every screen. */
export const SHELL_ANSWERS: FakeAnswers = {
  'GET /api/v1/settings/public': {},
  'GET /api/v1/notifications/unread-count': { count: 2 },
  'GET /api/v1/notifications': paged([]),
};

/**
 * A `fetch` that answers from `answers` and what the shell asks for, keyed by method and path ("GET /api/v1/patients");
 * the query string is ignored. A key may end in "/*" to answer every path below it. Anything else is a 404, and is
 * listed in `unanswered`.
 */
export function createFakeFetch(answers: FakeAnswers) {
  return createSharedFakeFetch({ ...SHELL_ANSWERS, ...answers });
}
