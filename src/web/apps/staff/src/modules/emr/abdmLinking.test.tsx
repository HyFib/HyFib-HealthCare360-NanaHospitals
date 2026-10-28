import { ApiError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { FOLLOW_EVERY_MS, FOLLOW_TIMES, useAbdmLink, type AbdmLinkStatus, type AbdmLinkView } from './abdmLinking';

// The server and the toasts are outside the test; the clock is the test's, so two minutes of asking take no time.
vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn() } }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const REPORT_ID = '0192a000-0000-7000-8000-00000000e601';
const LINK_ID = '0192a000-0000-7000-8000-00000000e701';

const link = (status: AbdmLinkStatus, answer: Partial<AbdmLinkView> = {}): AbdmLinkView => ({
  id: LINK_ID,
  patientId: '0192a000-0000-7000-8000-00000000e801',
  abhaAddress: 'meera.iyer@sbx',
  status,
  records: [
    { type: 'LabReport', id: REPORT_ID, referenceNumber: `LAB-${REPORT_ID}`, display: 'Lab report, 28 Oct 2026' },
  ],
  createdAt: '2026-10-28T10:00:00Z',
  sentAt: '2026-10-28T10:00:00Z',
  answeredAt: null,
  errorCode: null,
  error: null,
  ...answer,
});

function renderLink() {
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return renderHook(() => useAbdmLink(), { wrapper }).result;
}

/** Links the lab report as the button does, through `linking` (one screen's hook) when given. */
async function linkTheReport(linking = renderLink()) {
  await act(async () => {
    linking.current.mutate({ type: 'LabReport', id: REPORT_ID });
    await vi.advanceTimersByTimeAsync(0);
  });
}

/** The Idempotency-Key each request to link was sent with. */
const keysSent = () => vi.mocked(api.post).mock.calls.map(([, , options]) => options?.idempotencyKey);

/** Lets the screen wait for ABDM the given number of times, each as long as it waits before asking again. */
const waitFor = (times: number) => act(() => vi.advanceTimersByTimeAsync(times * FOLLOW_EVERY_MS));

describe('useAbdmLink', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.mocked(api.get).mockReset();
    vi.mocked(api.post).mockReset();
    Object.values(notify).forEach((fn) => fn.mockReset());
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("sends the record to ABDM and tells when ABDM links it to the patient's ABHA", async () => {
    vi.mocked(api.post).mockResolvedValue(link('AwaitingToken'));
    vi.mocked(api.get).mockResolvedValueOnce(link('Sent')).mockResolvedValueOnce(link('Linked'));

    await linkTheReport();

    expect(api.post).toHaveBeenCalledWith(
      '/api/v1/abdm/links',
      { records: [{ type: 'LabReport', id: REPORT_ID }] },
      { idempotencyKey: expect.any(String) },
    );
    expect(notify.info).toHaveBeenCalledWith(
      'Sent to ABDM',
      'It links the record to meera.iyer@sbx. You are told here when it answers.',
    );

    await waitFor(1);
    expect(api.get).toHaveBeenCalledWith(`/api/v1/abdm/links/${LINK_ID}`);
    expect(notify.success).not.toHaveBeenCalled();

    await waitFor(1);
    expect(notify.success).toHaveBeenCalledWith(
      "Linked to the patient's ABHA",
      'The patient sees it in their PHR app, on meera.iyer@sbx.',
    );

    await waitFor(FOLLOW_TIMES);
    expect(api.get).toHaveBeenCalledTimes(2);
  });

  it('says why ABDM did not link the record', async () => {
    vi.mocked(api.post).mockResolvedValue(link('Sent'));
    vi.mocked(api.get).mockResolvedValue(
      link('Failed', { errorCode: 'ABDM-1013', error: 'The ABHA address is not active.' }),
    );

    await linkTheReport();
    await waitFor(1);

    expect(notify.warning).toHaveBeenCalledWith('ABDM did not link the record', 'The ABHA address is not active.');
    expect(notify.success).not.toHaveBeenCalled();
  });

  it('tells at once when the request could not be sent to ABDM', async () => {
    vi.mocked(api.post).mockResolvedValue(
      link('Failed', { errorCode: 'abdm.unreachable', error: 'The ABDM gateway could not be reached.' }),
    );

    await linkTheReport();
    await waitFor(FOLLOW_TIMES);

    expect(notify.warning).toHaveBeenCalledWith(
      'ABDM did not link the record',
      'The ABDM gateway could not be reached.',
    );
    expect(notify.info).not.toHaveBeenCalled();
    expect(api.get).not.toHaveBeenCalled();
  });

  it('says ABDM has not answered when it has not within two minutes, and stops asking', async () => {
    vi.mocked(api.post).mockResolvedValue(link('Sent'));
    vi.mocked(api.get).mockResolvedValue(link('Sent'));

    await linkTheReport();
    await waitFor(FOLLOW_TIMES - 1);
    expect(notify.info).toHaveBeenCalledTimes(1);

    await waitFor(1);
    expect(FOLLOW_TIMES * FOLLOW_EVERY_MS).toBe(120_000);
    expect(api.get).toHaveBeenCalledTimes(FOLLOW_TIMES);
    expect(notify.info).toHaveBeenLastCalledWith(
      'ABDM has not answered yet',
      'The patient sees the record in their PHR app once ABDM links it to meera.iyer@sbx.',
    );

    await waitFor(FOLLOW_TIMES);
    expect(api.get).toHaveBeenCalledTimes(FOLLOW_TIMES);
  });

  it('asks again after the WiFi drops', async () => {
    vi.mocked(api.post).mockResolvedValue(link('Sent'));
    vi.mocked(api.get)
      .mockRejectedValueOnce(new ApiError({ status: 0, title: 'Cannot reach the HealthCare360 server' }))
      .mockResolvedValueOnce(link('Linked'));

    await linkTheReport();
    await waitFor(2);

    expect(api.get).toHaveBeenCalledTimes(2);
    expect(notify.success).toHaveBeenCalledTimes(1);
  });

  it('stops asking, without a word, once the server refuses the question (signed out)', async () => {
    vi.mocked(api.post).mockResolvedValue(link('Sent'));
    vi.mocked(api.get).mockRejectedValue(new ApiError({ status: 401, title: 'Sign in again' }));

    await linkTheReport();
    await waitFor(FOLLOW_TIMES);

    expect(api.get).toHaveBeenCalledTimes(1);
    expect(notify.info).toHaveBeenCalledTimes(1);
    expect(notify.success).not.toHaveBeenCalled();
    expect(notify.warning).not.toHaveBeenCalled();
  });

  it.each([
    ['the WiFi dropped', new ApiError({ status: 0, title: 'Cannot reach the HealthCare360 server' })],
    [
      'the server is still at work on it',
      new ApiError({ status: 409, title: 'The original request is still being processed' }),
    ],
    ['the server failed', new ApiError({ status: 503, title: 'Service unavailable' })],
  ])('links the record again as the same request when its answer was lost: %s', async (_, lost) => {
    vi.mocked(api.post).mockRejectedValueOnce(lost).mockResolvedValueOnce(link('Linked'));
    const linking = renderLink();

    await linkTheReport(linking);
    await linkTheReport(linking);

    // The server answers the second as it answered the first: ABDM is asked once.
    const [first, second] = keysSent();
    expect(first).toEqual(expect.any(String));
    expect(second).toBe(first);
  });

  it('links the record again as a new request once the server has answered', async () => {
    vi.mocked(api.post)
      .mockRejectedValueOnce(
        new ApiError({ status: 422, title: 'The patient has no ABHA address.', code: 'abdm.no_abha_address' }),
      )
      .mockResolvedValueOnce(link('Linked'))
      .mockResolvedValueOnce(link('Linked'));
    const linking = renderLink();

    // Refused, then linked once the ABHA address is recorded, then linked again: the server stores each answer under
    // its key, so each must be a request of its own.
    await linkTheReport(linking);
    await linkTheReport(linking);
    await linkTheReport(linking);

    const keys = keysSent();
    expect(keys).toEqual([expect.any(String), expect.any(String), expect.any(String)]);
    expect(new Set(keys).size).toBe(3);
  });

  it('says why the server would not link the record, for example no ABHA address on the patient', async () => {
    const refusal = new ApiError({
      status: 422,
      title: 'The patient has no ABHA address. Record it on the patient before linking their records.',
      code: 'abdm.no_abha_address',
    });
    vi.mocked(api.post).mockRejectedValue(refusal);

    await linkTheReport();

    expect(notify.error).toHaveBeenCalledWith(refusal, "Could not link the record to the patient's ABHA");
    expect(notify.info).not.toHaveBeenCalled();
    expect(api.get).not.toHaveBeenCalled();
  });
});
