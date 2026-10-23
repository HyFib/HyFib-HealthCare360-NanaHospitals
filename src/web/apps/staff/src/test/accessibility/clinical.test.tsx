import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '../appScreen';
import { CLINICAL_ANSWERS, ENCOUNTER_ID } from './fixtures/clinical';

// The doctor's screens, whole (shell included) with canned data, checked with axe-core for WCAG 2.2 A and AA problems
// (SRS UX-10): the worklist with each of its lists, and the consultation of an open encounter. Colour contrast and
// target sizes are checked in a browser instead (FRONTEND-GUIDE.md).

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  server = fakeServer(CLINICAL_ANSWERS);
});

afterEach(() => {
  // A screen whose request went unanswered would show an error where its content should be, and hide that from axe.
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('doctor screens', SCREEN_TESTS, () => {
  it('worklist: the queue', async () => {
    openScreen('/emr');
    expect(await screen.findByText('GM-014', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Lakshmi Narayanan', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('X-ray chest PA view', {}, SLOW)).toBeInTheDocument();
    expect((await screen.findAllByText('Ward round (3)', {}, SLOW)).length).toBeGreaterThan(0);
    await expectNoAxeViolations();
  });

  it('worklist: the appointments', async () => {
    openScreen('/emr?view=appointments');
    expect(await screen.findByText('Priya Menon', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Arrived · GM-015', {}, SLOW)).toBeInTheDocument();
    expect((await screen.findAllByText('Ward round (3)', {}, SLOW)).length).toBeGreaterThan(0);
    await expectNoAxeViolations();
  });

  it('worklist: the ward round', async () => {
    openScreen('/emr?view=rounds');
    expect(await screen.findByText('ICU-02', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Vitals critical', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('X-ray chest PA view', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('encounter (consultation)', async () => {
    openScreen(`/emr/encounters/${ENCOUNTER_ID}`);
    expect(await screen.findByText('Allergies: Penicillin (anaphylaxis), Sulfonamides', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Payer: Self-pay', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Dictate with AI', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Type 2 diabetes, fair control on metformin.', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Template investigations (2)', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('104/min', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Dengue fever [classical dengue]', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('ORD2600457', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByDisplayValue('Dolo 650', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Continue Glycomet 500', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('RX2600309', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Dengue warning signs explained', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
