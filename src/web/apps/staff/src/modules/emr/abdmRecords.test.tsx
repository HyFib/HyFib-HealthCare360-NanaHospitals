import type * as Core from '@hc360/core';
import { saveFile } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { useAbdmRecordDownload, useAbdmRecordsAvailable } from './abdmRecords';
import { abdmPrescriptionBundle } from './prescriptionsApi';

// The server, the switches, permissions, toasts and the browser's save dialog are outside the test.
const state = vi.hoisted(() => ({ switchedOn: true, canExport: true }));
vi.mock('@/lib/features', () => ({ useFeatureOn: (feature: string) => feature === 'abdm' && state.switchedOn }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string) => permission === 'abdm.export' && state.canExport,
}));
vi.mock('@/lib/api', () => ({ api: { download: vi.fn() } }));
vi.mock('@hc360/core', async (importOriginal) => ({ ...(await importOriginal<typeof Core>()), saveFile: vi.fn() }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const PRESCRIPTION_ID = '0192a000-0000-7000-8000-00000000e501';

function wrapper({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      {children}
    </QueryClientProvider>
  );
}

describe('ABDM health records', () => {
  beforeEach(() => {
    state.switchedOn = true;
    state.canExport = true;
    vi.mocked(api.download).mockReset();
    vi.mocked(saveFile).mockReset();
    Object.values(notify).forEach((fn) => fn.mockReset());
  });

  it('are offered only while switched on, to staff with the ABDM export permission', () => {
    expect(renderHook(() => useAbdmRecordsAvailable()).result.current).toBe(true);
    state.switchedOn = false;
    expect(renderHook(() => useAbdmRecordsAvailable()).result.current).toBe(false);
    state.switchedOn = true;
    state.canExport = false;
    expect(renderHook(() => useAbdmRecordsAvailable()).result.current).toBe(false);
  });

  it('download a signed prescription under the name the server gave it', async () => {
    const blob = new Blob(['{"resourceType":"Bundle","type":"document"}'], { type: 'application/fhir+json' });
    vi.mocked(api.download).mockResolvedValue({
      blob,
      fileName: 'prescription-NHRX260000123-abdm.json',
      headers: new Headers(),
    });
    const { result } = renderHook(() => useAbdmRecordDownload(), { wrapper });

    act(() => result.current.mutate(abdmPrescriptionBundle(PRESCRIPTION_ID)));

    await waitFor(() => expect(saveFile).toHaveBeenCalledWith(blob, 'prescription-NHRX260000123-abdm.json'));
    expect(api.download).toHaveBeenCalledWith(`/api/v1/abdm/prescriptions/${PRESCRIPTION_ID}/bundle`);
    expect(notify.success).toHaveBeenCalledWith('ABDM record downloaded', 'It is in your Downloads folder.');
  });

  it('say why the server refused, for example while the doctor has not signed with their DSC', async () => {
    const refusal = new Error(
      'This hospital issues prescriptions only once the prescribing doctor has signed them with their DSC.',
    );
    vi.mocked(api.download).mockRejectedValue(refusal);
    const { result } = renderHook(() => useAbdmRecordDownload(), { wrapper });

    act(() => result.current.mutate(abdmPrescriptionBundle(PRESCRIPTION_ID)));

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(refusal, 'Could not download the ABDM record'));
    expect(saveFile).not.toHaveBeenCalled();
  });
});
