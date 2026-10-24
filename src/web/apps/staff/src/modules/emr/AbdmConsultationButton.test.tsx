import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Core from '@hc360/core';
import { saveFile } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { consultationExportable } from './abdmConsultation';
import { AbdmConsultationButton } from './AbdmConsultationButton';
import type { EncounterStatus, EncounterType } from './api';

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

const ENCOUNTER_ID = '0192a000-0000-7000-8000-00000000c501';

function renderButton(status: EncounterStatus = 'Signed', type: EncounterType = 'OutPatient') {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <AbdmConsultationButton encounter={{ id: ENCOUNTER_ID, status, type }} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('AbdmConsultationButton', () => {
  beforeEach(() => {
    state.switchedOn = true;
    state.canExport = true;
    vi.mocked(api.download).mockReset();
    vi.mocked(saveFile).mockReset();
    Object.values(notify).forEach((fn) => fn.mockReset());
  });

  it('is offered for a signed or amended consultation, never an unsigned one or an in-patient note', () => {
    expect(consultationExportable({ status: 'Signed', type: 'OutPatient' })).toBe(true);
    expect(consultationExportable({ status: 'Amended', type: 'Emergency' })).toBe(true);
    expect(consultationExportable({ status: 'InProgress', type: 'OutPatient' })).toBe(false);
    expect(consultationExportable({ status: 'Signed', type: 'InPatient' })).toBe(false);
  });

  it('is not shown while the note is not signed', () => {
    renderButton('InProgress');
    expect(screen.queryByRole('button', { name: /ABDM/ })).not.toBeInTheDocument();
  });

  it('is not offered without the ABDM export permission, or while ABDM is switched off', () => {
    state.canExport = false;
    renderButton();
    expect(screen.queryByRole('button', { name: /ABDM/ })).not.toBeInTheDocument();
  });

  it('downloads the consultation as an ABDM OP consultation record by the encounter id', async () => {
    const blob = new Blob(['{"resourceType":"Bundle"}'], { type: 'application/fhir+json' });
    vi.mocked(api.download).mockResolvedValue({ blob, fileName: 'op-consultation-abdm.json', headers: new Headers() });
    renderButton('Amended');

    await userEvent.click(screen.getByRole('button', { name: 'Download ABDM record' }));

    await waitFor(() => expect(saveFile).toHaveBeenCalledWith(blob, 'op-consultation-abdm.json'));
    expect(api.download).toHaveBeenCalledWith(`/api/v1/abdm/bundles/op-consultations/${ENCOUNTER_ID}`);
  });
});
