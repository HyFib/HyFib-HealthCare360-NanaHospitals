import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '../appScreen';
import { ASHA, BILLING_ANSWERS, DRAFT_ID } from './fixtures/billing';

// The OP billing counter, whole (shell included) with canned data, checked with axe-core for WCAG 2.2 A and AA problems
// (SRS UX-10): the patient's visits with services to bill, and a draft bill being made. Colour contrast and target
// sizes are checked in a browser instead (FRONTEND-GUIDE.md).

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  server = fakeServer(BILLING_ANSWERS);
});

afterEach(() => {
  // A screen whose request went unanswered would show an error where its content should be, and hide that from axe.
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('OP billing', SCREEN_TESTS, () => {
  it('before a patient is chosen', async () => {
    openScreen('/billing/op');
    expect(await screen.findByText('Start with the patient', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it("the patient's visits, services to bill and unpaid bills", async () => {
    openScreen(`/billing/op?patientId=${ASHA.id}`);
    expect(await screen.findByText('Complete blood count', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('NHOB260000154', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText(/Penicillin/, {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('a draft bill being made', async () => {
    openScreen(`/billing/op?patientId=${ASHA.id}&billId=${DRAFT_ID}`);
    expect(await screen.findByText('Draft bill', {}, SLOW)).toBeInTheDocument();
    expect((await screen.findAllByText('Complete blood count', {}, SLOW)).length).toBeGreaterThan(0);
    expect(await screen.findByRole('combobox', { name: 'Bill at a package' }, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});

describe('the rate list', SCREEN_TESTS, () => {
  it("names each service in English and, marked as the facility's language, in Tamil", async () => {
    openScreen('/billing/rate-list');
    // WCAG 3.1.2: a screen reader reads the Tamil name as Tamil.
    expect(await screen.findByText('சிறிய காயக் கட்டு', {}, SLOW)).toHaveAttribute('lang', 'ta');
    await expectNoAxeViolations();
  });
});
