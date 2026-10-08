import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as AdminApi from './api';
import { rotateDataKey, rotateSigningKey, type EncryptionKeysStatus } from './api';
import { EncryptionKeys } from './EncryptionKeys';

// The server, the toasts and the signed-in user are outside the test; the section's own behaviour is what is tested.
const readStatus = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof AdminApi>();
  return {
    ...actual,
    rotateDataKey: vi.fn(),
    rotateSigningKey: vi.fn(),
    adminQueries: {
      ...actual.adminQueries,
      encryptionKeys: () => ({ queryKey: ['admin', 'encryption-keys'], queryFn: () => readStatus() }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
const permissions = vi.hoisted(() => ({ manage: true }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => permissions.manage }));

function status(patch: Partial<EncryptionKeysStatus['dataKey']> = {}): EncryptionKeysStatus {
  return {
    dataKey: {
      keyId: '3F2A9C1B0D4E5F60',
      since: '2026-01-10T05:00:00Z',
      keys: 1,
      valuesToMove: 0,
      documentsToMove: 0,
      lastRotation: null,
      ...patch,
    },
    signingKey: { keyId: 'kX9dQ2mB7tLw4nVa', since: '2026-01-10T05:00:00Z', lastRotation: null },
  };
}

function renderSection() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <EncryptionKeys />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('EncryptionKeys', () => {
  beforeEach(() => {
    readStatus.mockReset();
    vi.mocked(rotateDataKey).mockReset();
    vi.mocked(rotateSigningKey).mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
    permissions.manage = true;
  });

  it('rotates the data key after asking, and shows what is left to move', async () => {
    const user = userEvent.setup();
    readStatus.mockResolvedValue(status());
    vi.mocked(rotateDataKey).mockResolvedValue(
      status({
        keyId: '7B1E0A44C2D9F3A8',
        keys: 2,
        valuesToMove: 1240,
        documentsToMove: 56,
        lastRotation: { at: '2026-10-08T10:00:00Z', by: 'IT Admin' },
      }),
    );
    renderSection();

    expect(await screen.findByText('3F2A9C1B0D4E5F60')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Rotate data key' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Rotate the data encryption key?' });
    expect(dialog).toHaveTextContent('The backup recovery key does not change.');
    await user.click(within(dialog).getByRole('button', { name: 'Rotate data key' }));

    await waitFor(() => expect(rotateDataKey).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('7B1E0A44C2D9F3A8')).toBeInTheDocument();
    expect(screen.getByText(/1,240 value\(s\) and 56 document\(s\) left/)).toBeInTheDocument();
    expect(screen.getByText(/2 keys kept/)).toBeInTheDocument();
    expect(notify.success).toHaveBeenCalledWith(
      'Data encryption key rotated',
      expect.stringContaining('in the background'),
    );
    expect(rotateSigningKey).not.toHaveBeenCalled();
  });

  it('rotates the sign-in key without signing anyone out', async () => {
    const user = userEvent.setup();
    readStatus.mockResolvedValue(status());
    vi.mocked(rotateSigningKey).mockResolvedValue(status());
    renderSection();

    await user.click(await screen.findByRole('button', { name: 'Rotate sign-in key' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Rotate the sign-in token key?' });
    await user.click(within(dialog).getByRole('button', { name: 'Rotate sign-in key' }));

    await waitFor(() => expect(rotateSigningKey).toHaveBeenCalledTimes(1));
    expect(notify.success).toHaveBeenCalledWith(
      'Sign-in token key rotated',
      expect.stringContaining('Nobody is signed out'),
    );
  });

  it('names only what is left to move', async () => {
    readStatus.mockResolvedValue(status({ keys: 2, valuesToMove: 4 }));
    renderSection();

    expect(await screen.findByText('Moving to the new key: 4 value(s) left.')).toBeInTheDocument();
  });

  it('shows the keys, but no buttons, to those who may not rotate them', async () => {
    permissions.manage = false;
    readStatus.mockResolvedValue(status({ lastRotation: { at: '2026-09-01T05:00:00Z', by: 'IT Admin' } }));
    renderSection();

    expect(await screen.findByText(/by IT Admin/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Rotate data key' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Rotate sign-in key' })).not.toBeInTheDocument();
  });
});
