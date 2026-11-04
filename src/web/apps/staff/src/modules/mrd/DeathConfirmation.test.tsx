import type * as Core from '@hc360/core';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ApprovalRequestDto } from '@/modules/approvals/api';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import { paged } from '@/test/fakeApi';
import { ANSWERS } from '@/test/roleAccess/births-deaths';
import { RECORD_ID } from '@/test/roleAccessIds';
import type { DeathRecordDto, DeathRegister } from './vital';

// Maker-checker on the medical certificate of cause of death: a second doctor confirms it before the death is reported
// to the Registrar. The certifying doctor asks; while the request waits, the certificate is not changed.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const path = `/api/v1/mrd/deaths/${RECORD_ID}`;
const CONFIRMED = ANSWERS[`GET ${path}`] as DeathRecordDto;
const UNCONFIRMED: DeathRecordDto = {
  ...CONFIRMED,
  confirmedBy: null,
  confirmerRegistrationNumber: null,
  confirmedAt: null,
};
const WAITING: DeathRecordDto = {
  ...UNCONFIRMED,
  confirmationWaitingSince: new Date(Date.now() - 3_600_000).toISOString(),
};

const REQUEST: ApprovalRequestDto = {
  id: '0192a000-0000-7000-8000-00000000d401',
  kind: 'mrd.death_certificate',
  title: 'Death certificate',
  summary: `Death certificate ${CONFIRMED.serialNumber}`,
  details: [{ label: 'Death register no.', value: CONFIRMED.serialNumber }],
  reason: 'A second doctor confirms the cause of death before the hospital reports it to the Registrar.',
  status: 'Pending',
  isExpired: false,
  subjectType: 'DeathRecord',
  subjectId: RECORD_ID,
  patientId: CONFIRMED.patientId,
  requestedBy: '0192a000-0000-7000-8000-00000000d402',
  requestedByName: CONFIRMED.certifiedBy,
  requestedAt: WAITING.confirmationWaitingSince!,
  expiresAt: new Date(Date.now() + 6 * 86_400_000).toISOString(),
  decidedBy: null,
  decidedByName: null,
  decidedAt: null,
  decisionNote: null,
  outcome: null,
  canDecide: false,
  canWithdraw: true,
  version: 3,
};

function open(death: DeathRecordDto) {
  stubBrowserApis();
  const server = fakeServer({
    ...ANSWERS,
    [`GET ${path}`]: death,
    'GET /api/v1/approvals': paged(death.confirmationWaitingSince ? [REQUEST] : []),
    [`POST ${path}/confirmation`]: WAITING,
  });
  openScreen(`/mrd/deaths/${RECORD_ID}`);
  return server;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('a death certificate confirmed by a second doctor', SCREEN_TESTS, () => {
  it('is not reported until it is confirmed, and its certifier asks for the confirmation', async () => {
    const server = open(UNCONFIRMED);

    expect(await screen.findByText('Not yet confirmed', undefined, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Not yet confirmed by a second doctor')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Report to Registrar' })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Ask for confirmation' }));

    await waitFor(() => expect(server.requests.some((r) => r.method === 'POST')).toBe(true));
    const post = server.requests.find((r) => r.method === 'POST')!;
    expect(post.path).toBe(`${path}/confirmation`);
    expect(post.body).toEqual({ version: UNCONFIRMED.version });
  });

  it('is not corrected or marked entered in error while the request for it waits', async () => {
    open(WAITING);

    expect(await screen.findByText('Waiting for approval', undefined, SLOW)).toBeInTheDocument();
    // The request, opened from the banner (a link without an address is a button).
    expect(screen.getByRole('button', { name: REQUEST.summary })).toBeInTheDocument();
    for (const name of ['Correct', 'Entered in error', 'Ask for confirmation', 'Report to Registrar']) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }
    expect(screen.queryByText('Not yet confirmed')).not.toBeInTheDocument();
  });

  it.each([
    ['still to be confirmed', true],
    ['confirmed', false],
  ])('is offered for reporting from the month register only once confirmed (%s)', async (_, awaitingConfirmation) => {
    const register = ANSWERS['GET /api/v1/mrd/deaths'] as DeathRegister;
    stubBrowserApis();
    fakeServer({
      ...ANSWERS,
      'GET /api/v1/mrd/deaths': {
        ...register,
        rows: register.rows.map((r) => (r.id === RECORD_ID ? { ...r, awaitingConfirmation } : r)),
      },
    });
    openScreen('/mrd/deaths');

    expect(await screen.findByText(CONFIRMED.serialNumber, undefined, SLOW)).toBeInTheDocument();
    // The register's other death is already reported, so this one is the only one that may be offered.
    expect(screen.queryAllByRole('button', { name: 'Report to Registrar' })).toHaveLength(awaitingConfirmation ? 0 : 1);
    expect(screen.queryAllByText('Not yet confirmed')).toHaveLength(awaitingConfirmation ? 1 : 0);
  });

  it('names the second doctor once confirmed, and is then reported', async () => {
    open(CONFIRMED);

    expect(
      await screen.findByText(`Dr. Meera Iyer (registration KMC 2011/06/2231)`, { exact: false }, SLOW),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Report to Registrar' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ask for confirmation' })).not.toBeInTheDocument();
    expect(screen.queryByText('Not yet confirmed')).not.toBeInTheDocument();
  });
});
