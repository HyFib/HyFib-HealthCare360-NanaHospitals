import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { PrescriptionEditor } from './PrescriptionEditor';
import type { CreatePrescriptionRequest, PrescriptionDto } from './prescriptionsApi';

// The server, the toasts and the router are outside the test: what the editor asks for and sends is what is tested.
vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn(), put: vi.fn() } }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@tanstack/react-router', () => ({ useNavigate: () => vi.fn() }));

function renderEditor() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <PrescriptionEditor patientId="p1" encounterId="e1" initialDraft={null} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('PrescriptionEditor', () => {
  beforeEach(() => {
    vi.mocked(api.get).mockReset().mockResolvedValue([]);
    vi.mocked(api.post).mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('asks for the generic name of a medicine typed in, in its details, before saving it', async () => {
    const user = userEvent.setup();
    vi.mocked(api.post).mockResolvedValue({
      id: 'rx1',
      prescriptionNumber: 'NHRX260000042',
      version: 1,
    } as PrescriptionDto);
    renderEditor();

    await user.type(screen.getByRole('combobox', { name: 'Medicine 1' }), 'Pan 40');
    await user.type(screen.getByRole('textbox', { name: 'Pan 40 dose' }), '1 tab');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));

    // The details open on the generic name, which says why; nothing is sent.
    const generic = await screen.findByRole('textbox', { name: /Generic name/ });
    expect(
      screen.getByText('Enter the generic name: the prescription names every medicine by it.'),
    ).toBeInTheDocument();
    expect(api.post).not.toHaveBeenCalled();

    await user.type(generic, 'Pantoprazole');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));

    await vi.waitFor(() => expect(api.post).toHaveBeenCalledTimes(1));
    const [path, body] = vi.mocked(api.post).mock.calls[0]!;
    expect(path).toBe('/api/v1/clinical/prescriptions');
    expect((body as CreatePrescriptionRequest).items[0]).toMatchObject({
      drugName: 'Pan 40',
      genericName: 'Pantoprazole',
    });
  });
});
