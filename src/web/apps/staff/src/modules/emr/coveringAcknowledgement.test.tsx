import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { LabReportView } from '@/modules/lab/api';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis, type FakeRequest } from '@/test/appScreen';
import { CLINICAL_ANSWERS } from '@/test/accessibility/fixtures/clinical';

// A critical lab result acknowledged from the doctor's worklist where the lab asks for a second pathologist: a doctor
// who did not order the test is asked by the server to say that they cover the patient and why. The field appears, the
// acknowledgement waits for a reason of at least ten characters, and it is sent with the reason; the dialog passes
// axe-core with the field shown.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

/** The critical complete blood count on the worklist fixture (Kamala Bai). */
const REVIEW_ID = '0192a000-0000-7000-8000-000000004001';
const REPORT_ID = '0192a000-0000-7000-8000-000000004011';
const ACKNOWLEDGE = `POST /api/v1/emr/results/${REVIEW_ID}/acknowledge`;
const REASON = 'Night duty doctor covering ward 4';

/** The released report, as much of it as the review dialog reads: one critical haemoglobin. */
const REPORT = {
  reportId: REPORT_ID,
  sections: [
    {
      name: 'Haematology',
      tests: [
        {
          name: 'Complete blood count',
          results: [
            {
              resultId: '0192a000-0000-7000-8000-000000004021',
              name: 'Haemoglobin',
              value: '5.8',
              unit: 'g/dL',
              flag: 'CriticalLow',
              referenceText: '12.0 - 15.0',
            },
          ],
        },
      ],
    },
  ],
} as unknown as LabReportView;

/** The server's refusal of an acknowledgement by a doctor who did not order the test and gave no covering reason. */
const coveringReasonNeeded = () =>
  new Response(
    JSON.stringify({
      title: 'One or more fields are invalid.',
      status: 400,
      code: 'validation',
      detail:
        'You did not order this test. To acknowledge the critical result, record that you cover the patient and why.',
      errors: { coveringReason: ['You did not order this test. Record that you cover the patient and why.'] },
    }),
    { status: 400, headers: { 'content-type': 'application/problem+json' } },
  );

let server: ReturnType<typeof fakeServer>;
let sent: unknown[];

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  sent = [];
  server = fakeServer({
    ...CLINICAL_ANSWERS,
    [`GET /api/v1/lab/reports/${REPORT_ID}`]: REPORT,
    [ACKNOWLEDGE]: (request: FakeRequest) => {
      sent.push(request.body);
      const body = request.body as { coveringReason: string | null };
      return body.coveringReason ? null : coveringReasonNeeded();
    },
  });
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('acknowledging a critical result as a covering doctor', SCREEN_TESTS, () => {
  it('asks why the doctor covers the patient, and sends the acknowledgement with the reason', async () => {
    openScreen('/emr');
    await userEvent.click(await screen.findByText('Complete blood count', {}, SLOW));
    const dialog = await screen.findByRole('dialog', {}, SLOW);
    expect(await within(dialog).findByText('5.8 g/dL (Critical low)', { exact: false }, SLOW)).toBeInTheDocument();
    expect(within(dialog).queryByRole('textbox', { name: /You cover this patient because/ })).not.toBeInTheDocument();

    // The first try goes without a reason; the server answers that this doctor did not order the test.
    await userEvent.click(within(dialog).getByRole('button', { name: 'Acknowledge' }));
    const covering = await within(dialog).findByRole('textbox', { name: /You cover this patient because/ }, SLOW);
    expect(sent).toEqual([{ note: null, coveringReason: null }]);
    expect(within(dialog).getByRole('button', { name: 'Acknowledge' })).toBeDisabled();
    await expectNoAxeViolations();

    // A word or two says nothing: the button waits for ten characters.
    await userEvent.type(covering, 'on call');
    expect(within(dialog).getByRole('button', { name: 'Acknowledge' })).toBeDisabled();
    await userEvent.clear(covering);
    await userEvent.type(covering, REASON);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Acknowledge' }));

    await vi.waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1]).toEqual({ note: null, coveringReason: REASON });
    await vi.waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });
});
