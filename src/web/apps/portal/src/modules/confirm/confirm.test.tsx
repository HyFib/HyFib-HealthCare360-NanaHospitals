import { FluentProvider } from '@fluentui/react-components';
import { ApiError, useSession } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { createFakeFetch, json, type FakeRequest } from '@hc360/ui/testing/fake-api';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WIDE_LAYOUT_QUERY } from '@/lib/hooks';
import { useProfileStore } from '@/lib/profileStore';
import { portalTypeScale } from '@/lib/theme';
import type { PortalProfile, RecordsToConfirm } from '@/lib/types';
import { createPortalRouter } from '@/router';
import { PORTAL_ANSWERS, PROFILE, USER } from '@/test/fixtures';
import {
  checkConfirm,
  confirmErrorKey,
  dateOfBirthOf,
  emptyConfirm,
  refusalNumber,
  toConfirmRequest,
  waitOf,
} from './model';

/**
 * "Confirm your details" (ASVS V6.6.1): a record on the patient's number that nobody has verified is shown only once the
 * patient's date of birth or UHID is given. End to end in jsdom (router, signed-in frame, queries, i18n) against a fake
 * server, and the form's own rules.
 */

// The toasts are outside the test.
const toast = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), failure: vi.fn() }));
vi.mock('@/lib/toast', () => ({ useToast: () => toast }));

const SLOW = { timeout: 8000 };
const PATH = '/api/v1/portal/records-to-confirm';
const MOTHER: PortalProfile = {
  ...PROFILE,
  id: '6a1d7e4f-9d2b-4b64-8b4f-3c2e7f8a0b21',
  uhid: 'NH26000124',
  fullName: 'Kamala Verma',
};

let profiles: PortalProfile[];
let status: RecordsToConfirm;
let answerConfirm: (request: FakeRequest) => unknown;
let server: ReturnType<typeof createFakeFetch>;

beforeEach(() => {
  toast.success.mockClear();
  useProfileStore.getState().select(null);
  vi.stubGlobal(
    'matchMedia',
    (query: string): MediaQueryList =>
      ({
        matches: query === WIDE_LAYOUT_QUERY,
        media: query,
        onchange: null,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
        addListener: () => undefined,
        removeListener: () => undefined,
        dispatchEvent: () => false,
      }) as MediaQueryList,
  );
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
  profiles = [];
  status = { unconfirmed: 1, attemptsLeft: 5, retryAfterSeconds: null };
  answerConfirm = () => json({ title: 'Refused', status: 422, code: 'portal.confirm_nothing' }, 422);
  server = createFakeFetch({
    ...PORTAL_ANSWERS,
    'GET /api/v1/portal/profiles': () => profiles,
    [`GET ${PATH}`]: () => status,
    [`POST ${PATH}`]: (request: FakeRequest) => answerConfirm(request),
  });
  vi.stubGlobal('fetch', vi.fn(server.fetch));
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  sessionStorage.clear();
});

function openAt(path: string) {
  useSession.getState().setAuth({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', user: USER });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <Hc360ThemeProvider>
        <FluentProvider theme={portalTypeScale}>
          <RouterProvider router={createPortalRouter(queryClient, createMemoryHistory({ initialEntries: [path] }))} />
        </FluentProvider>
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

const confirmations = () => server.requests.filter((r) => r.method === 'POST' && r.path === PATH).map((r) => r.body);

describe('confirming records on the number', () => {
  it('shows no record until the date of birth matches, then shows it', async () => {
    let tries = 0;
    answerConfirm = (request) => {
      tries += 1;
      if ((request.body as { dateOfBirth: string | null }).dateOfBirth !== '1987-03-14') {
        return json(
          {
            title: 'Refused',
            status: 422,
            code: 'portal.confirm_no_match',
            detail: 'No match.',
            data: { attemptsLeft: 4 },
          },
          422,
        );
      }
      profiles = [PROFILE];
      status = { unconfirmed: 0, attemptsLeft: 4, retryAfterSeconds: null };
      return { confirmed: 1, unconfirmed: 0 };
    };
    openAt('/portal/home');

    // Signed in, but nothing of the record is shown before it is confirmed: not its name, only that one waits.
    expect(await screen.findByRole('heading', { level: 1, name: 'Confirm your details' }, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/One record registered with your mobile number is not confirmed yet/)).toBeInTheDocument();
    expect(screen.queryByText(/Asha/)).not.toBeInTheDocument();
    await expectNoAxeViolations();

    const user = userEvent.setup();
    // A date that is not on the calendar is refused before a try is spent.
    await user.type(screen.getByRole('textbox', { name: 'Day' }), '31');
    await user.type(screen.getByRole('textbox', { name: 'Month' }), '02');
    await user.type(screen.getByRole('textbox', { name: 'Year' }), '1987');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));
    expect(await screen.findByText(/Enter a real date/)).toBeInTheDocument();
    expect(tries).toBe(0);

    await user.clear(screen.getByRole('textbox', { name: 'Day' }));
    await user.type(screen.getByRole('textbox', { name: 'Day' }), '13');
    await user.clear(screen.getByRole('textbox', { name: 'Month' }));
    await user.type(screen.getByRole('textbox', { name: 'Month' }), '3');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));
    expect(
      await screen.findByText(/These details do not match a record registered with this number/),
    ).toBeInTheDocument();
    expect(screen.getByText('Tries left today: 4.')).toBeInTheDocument();

    await user.clear(screen.getByRole('textbox', { name: 'Day' }));
    await user.type(screen.getByRole('textbox', { name: 'Day' }), '14');
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    expect(await screen.findByRole('heading', { level: 1, name: /Asha/ }, SLOW)).toBeInTheDocument();
    expect(confirmations()).toEqual([
      { dateOfBirth: '1987-03-13', uhid: null },
      { dateOfBirth: '1987-03-14', uhid: null },
    ]);
  });

  it('says on the home page that a family member’s record waits, and confirms it with the UHID', async () => {
    profiles = [PROFILE];
    answerConfirm = () => {
      profiles = [PROFILE, MOTHER];
      status = { unconfirmed: 0, attemptsLeft: 5, retryAfterSeconds: null };
      return { confirmed: 1, unconfirmed: 0 };
    };
    openAt('/portal/home');

    expect(await screen.findByText('Some records on this number are not confirmed yet', {}, SLOW)).toBeInTheDocument();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Confirm details' }));
    const sheet = await screen.findByRole('dialog', { name: 'Confirm your details' });
    await user.click(within(sheet).getByRole('radio', { name: 'UHID' }));
    await user.type(within(sheet).getByRole('textbox', { name: /The patient’s UHID/ }), ' nh26000124 ');
    await user.click(within(sheet).getByRole('button', { name: 'Confirm' }));

    await vi.waitFor(() => expect(toast.success).toHaveBeenCalledWith('The record is confirmed. You can see it now.'));
    expect(confirmations()).toEqual([{ dateOfBirth: null, uhid: 'NH26000124' }]);
    await vi.waitFor(() =>
      expect(screen.queryByText('Some records on this number are not confirmed yet')).not.toBeInTheDocument(),
    );
  });

  it('after five wrong tries in a day, says how long to wait and asks for nothing', async () => {
    status = { unconfirmed: 2, attemptsLeft: 0, retryAfterSeconds: 3 * 3600 - 120 };
    openAt('/portal/home');

    expect(await screen.findByText(/Too many wrong tries for this number/, {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/You can try again in about 3 hours/)).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Day' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Confirm' })).not.toBeInTheDocument();
  });

  it('with nothing waiting, an account that sees no records is told the number is on no registration yet', async () => {
    status = { unconfirmed: 0, attemptsLeft: 5, retryAfterSeconds: null };
    openAt('/portal/home');

    expect(
      await screen.findByRole('heading', { level: 1, name: 'No hospital records use this number yet' }, SLOW),
    ).toBeInTheDocument();
  });
});

describe('the form’s rules', () => {
  it('takes a date of birth only as a real date, day and month in any width', () => {
    expect(dateOfBirthOf('14', '3', '1987')).toBe('1987-03-14');
    expect(dateOfBirthOf(' 4 ', '03', '1987')).toBe('1987-03-04');
    expect(dateOfBirthOf('29', '02', '2023')).toBeNull();
    expect(dateOfBirthOf('29', '02', '2024')).toBe('2024-02-29');
    expect(dateOfBirthOf('14', '13', '1987')).toBeNull();
    expect(dateOfBirthOf('14', '03', '87')).toBeNull();
    expect(dateOfBirthOf('', '', '')).toBeNull();
  });

  it('checks what the server checks, so no try is spent on a mistake', () => {
    const today = '2026-11-14';
    expect(checkConfirm({ ...emptyConfirm(), day: '14', month: '3', year: '1987' }, today)).toEqual([]);
    expect(checkConfirm({ ...emptyConfirm(), day: '15', month: '11', year: '2026' }, today)).toEqual([
      { path: 'dateOfBirth', key: 'confirm.dob.past' },
    ]);
    expect(checkConfirm({ ...emptyConfirm(), day: '1', month: '1', year: '1899' }, today)).toEqual([
      { path: 'dateOfBirth', key: 'confirm.dob.past' },
    ]);
    expect(checkConfirm({ ...emptyConfirm(), method: 'uhid', uhid: 'NH 26' }, today)).toEqual([
      { path: 'uhid', key: 'confirm.uhid.invalid' },
    ]);
    expect(checkConfirm({ ...emptyConfirm(), method: 'uhid', uhid: 'NH26000124' }, today)).toEqual([]);
  });

  it('sends one of the two details, as the server expects them', () => {
    expect(toConfirmRequest({ ...emptyConfirm(), day: '14', month: '3', year: '1987', uhid: 'NH1' })).toEqual({
      dateOfBirth: '1987-03-14',
      uhid: null,
    });
    expect(toConfirmRequest({ ...emptyConfirm(), method: 'uhid', uhid: ' nh26000124 ', day: '14' })).toEqual({
      dateOfBirth: null,
      uhid: 'NH26000124',
    });
  });

  it('reads the server’s refusals and how long to wait', () => {
    const noMatch = new ApiError({ status: 422, code: 'portal.confirm_no_match', data: { attemptsLeft: 2 } });
    expect(confirmErrorKey(noMatch)).toBe('confirm.error.noMatch');
    expect(refusalNumber(noMatch, 'attemptsLeft')).toBe(2);
    const throttled = new ApiError({ status: 422, code: 'portal.confirm_throttled', data: { retryAfterSeconds: 61 } });
    expect(confirmErrorKey(throttled)).toBe('confirm.error.throttled');
    expect(refusalNumber(throttled, 'retryAfterSeconds')).toBe(61);
    expect(confirmErrorKey(new ApiError({ status: 422, code: 'portal.confirm_nothing' }))).toBe(
      'confirm.error.nothing',
    );
    expect(confirmErrorKey(new Error('offline'))).toBeNull();

    expect(waitOf(5 * 3600)).toEqual({ key: 'confirm.wait.hours', count: 5 });
    expect(waitOf(3601)).toEqual({ key: 'confirm.wait.hours', count: 2 });
    expect(waitOf(3600)).toEqual({ key: 'confirm.wait.minutes', count: 60 });
    expect(waitOf(61)).toEqual({ key: 'confirm.wait.minutes', count: 2 });
    expect(waitOf(5)).toEqual({ key: 'confirm.wait.minute', count: 1 });
  });
});
