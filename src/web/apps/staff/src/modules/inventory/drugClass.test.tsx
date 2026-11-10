import type * as Core from '@hc360/core';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ApprovalRequestDto } from '@/modules/approvals/api';
import { ITEMS } from '@/test/accessibility/fixtures/pharmacy';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import { ANSWERS } from '@/test/roleAccess/inventory';
import type { ItemDetail, SaveItemRequest } from './api';

// Item master › an item: a drug handled less strictly (no longer controlled or high-alert, or a lower schedule) keeps its
// class until a pharmacist approves the change; the rest of the item is saved at once. Made stricter, it applies at once.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const TRAMADOL = ITEMS.find((i) => i.code === 'TRM50')!;
const path = `/api/v1/inventory/items/${TRAMADOL.id}`;

const REQUEST: ApprovalRequestDto = {
  id: '0192a000-0000-7000-8000-00000000c501',
  kind: 'inventory.drug_class',
  title: 'Drug classification',
  summary: 'TRM50 Tramadol 50 mg capsule: Schedule H1, not controlled, not high-alert',
  details: [],
  reason: 'Listed in the H1 register only.',
  status: 'Pending',
  isExpired: false,
  subjectType: 'Item',
  subjectId: TRAMADOL.id,
  patientId: null,
  requestedBy: '0192a000-0000-7000-8000-0000000000a1',
  requestedByName: 'Dr. Meera Iyer',
  requestedAt: new Date().toISOString(),
  expiresAt: new Date(Date.now() + 6 * 86_400_000).toISOString(),
  decidedBy: null,
  decidedByName: null,
  decidedAt: null,
  decisionNote: null,
  outcome: null,
  canDecide: false,
  canWithdraw: true,
  version: 1,
};

/** The item open in the master; the save answers with the approval it waits on, or nothing (saved at once). */
function open(item: ItemDetail = TRAMADOL, waits = true) {
  stubBrowserApis();
  const server = fakeServer({ ...ANSWERS, [`GET ${path}`]: item, [`PUT ${path}`]: waits ? REQUEST : null });
  openScreen(`/inventory/items?edit=${item.id}`);
  return server;
}

const saved = (server: ReturnType<typeof fakeServer>) =>
  server.requests.find((r) => r.method === 'PUT' && r.path === path)?.body as SaveItemRequest | undefined;

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('a drug made less strictly handled', SCREEN_TESTS, () => {
  it('says the change waits for a pharmacist, asks why, and keeps the rest of the save', async () => {
    const server = open();
    const user = userEvent.setup();
    const ndps = await screen.findByRole('switch', { name: /Narcotic or psychotropic/ }, SLOW);
    expect(screen.queryByText(/would be handled less strictly/)).not.toBeInTheDocument();

    await user.click(ndps);
    expect(await screen.findByText(/would be handled less strictly/)).toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: /Why the change/ }), 'Listed in the H1 register only.');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(saved(server)).toBeDefined(), SLOW);
    expect(saved(server)).toMatchObject({
      isControlled: false,
      schedule: 'H1',
      classChangeReason: 'Listed in the H1 register only.',
    });
    expect(
      await screen.findByText(/stays as it was until a pharmacist approves the change/, {}, SLOW),
    ).toBeInTheDocument();
  });

  it('asks nothing when the drug is made stricter, and saves it as high-alert at once', async () => {
    const server = open(TRAMADOL, false);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('switch', { name: /High-alert medicine/ }, SLOW));

    expect(screen.queryByText(/would be handled less strictly/)).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: /Why the change/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(saved(server)).toBeDefined(), SLOW);
    expect(saved(server)).toMatchObject({ isHighAlert: true, isControlled: true, classChangeReason: null });
    expect(await screen.findByText('Item saved', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByText(/until a pharmacist approves/)).not.toBeInTheDocument();
  });

  it('marks a high-alert medicine in the item list', async () => {
    stubBrowserApis();
    fakeServer({
      ...ANSWERS,
      'GET /api/v1/inventory/items': paged([
        {
          id: TRAMADOL.id,
          code: TRAMADOL.code,
          name: TRAMADOL.name,
          genericName: TRAMADOL.genericName,
          strength: TRAMADOL.strength,
          dosageForm: TRAMADOL.dosageForm,
          type: TRAMADOL.type,
          categoryName: 'Analgesics',
          schedule: TRAMADOL.schedule,
          isControlled: TRAMADOL.isControlled,
          isColdChain: false,
          baseUnit: TRAMADOL.baseUnit,
          purchaseUnit: TRAMADOL.purchaseUnit,
          gstRate: TRAMADOL.gstRate,
          reorderLevel: TRAMADOL.reorderLevel,
          isActive: true,
          isHighAlert: true,
        },
      ]),
    });
    openScreen('/inventory/items');

    expect(await screen.findByText('High alert', {}, SLOW)).toBeInTheDocument();
  });

  it('shows a change already waiting for approval', async () => {
    open({
      ...TRAMADOL,
      pendingClass: {
        approvalId: REQUEST.id,
        asked: { schedule: 'H1', isControlled: false, isHighAlert: false, telemedicineList: null },
        requestedBy: 'Ravi Store',
        requestedAt: '2026-11-04T09:30:00Z',
      },
    });

    expect(await screen.findByText(/Ravi Store asked on .* to be handled less strictly/, {}, SLOW)).toBeInTheDocument();
  });
});

describe("a medicine's NPPA ceiling price", SCREEN_TESTS, () => {
  it('raised, waits for someone who approves prices and asks why; the save carries the reason', async () => {
    const server = open({ ...TRAMADOL, ceilingPrice: 2, ceilingNotification: 'S.O. 1(E) of 1-Apr-2026' }, true);
    const user = userEvent.setup();
    const ceiling = await screen.findByRole('spinbutton', { name: /NPPA ceiling price/ }, SLOW);
    expect(screen.queryByText(/waits for someone who approves prices/)).not.toBeInTheDocument();

    await user.clear(ceiling);
    await user.type(ceiling, '3');
    expect(await screen.findByText(/waits for someone who approves prices/)).toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: /Why the price change/ }), 'NPPA revised the ceiling');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(saved(server)).toBeDefined(), SLOW);
    expect(saved(server)).toMatchObject({ ceilingPrice: 3, priceChangeReason: 'NPPA revised the ceiling' });
  });

  it('lowered, applies at once and asks nothing', async () => {
    open({ ...TRAMADOL, ceilingPrice: 2 }, false);
    const user = userEvent.setup();
    const ceiling = await screen.findByRole('spinbutton', { name: /NPPA ceiling price/ }, SLOW);
    await user.clear(ceiling);
    await user.type(ceiling, '1.5');
    expect(screen.queryByText(/waits for someone who approves prices/)).not.toBeInTheDocument();
  });
});
