import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as AdminApi from './api';
import { retireDataKey, rotateDataKey, rotateSigningKey, type EarlierDataKey, type EncryptionKeysStatus } from './api';
import { EncryptionKeys } from './EncryptionKeys';

// The server, the toasts and the signed-in user are outside the test; the section's own behaviour is what is tested.
const readStatus = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof AdminApi>();
  return {
    ...actual,
    rotateDataKey: vi.fn(),
    rotateSigningKey: vi.fn(),
    retireDataKey: vi.fn(),
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
      earlier: [],
      ...patch,
    },
    signingKey: { keyId: 'kX9dQ2mB7tLw4nVa', since: '2026-01-10T05:00:00Z', lastRotation: null },
  };
}

function earlierKey(patch: Partial<EarlierDataKey> = {}): EarlierDataKey {
  return {
    keyId: '5C0FFEE5C0FFEE51',
    since: '2024-01-10T05:00:00Z',
    values: 0,
    documents: 0,
    expiring: 0,
    retireFrom: null,
    canRetire: true,
    ...patch,
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
    vi.mocked(retireDataKey).mockReset();
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

  it('lists the earlier keys, with what each still encrypts or when it can be retired', async () => {
    readStatus.mockResolvedValue(
      status({
        keys: 4,
        earlier: [
          earlierKey({ keyId: 'AAAA000011112222', values: 12, documents: 3, canRetire: false }),
          earlierKey({ keyId: 'BBBB000011112222', expiring: 2, canRetire: false }),
          earlierKey({ keyId: 'CCCC000011112222', retireFrom: '2026-10-11T10:00:00Z', canRetire: false }),
          earlierKey({ keyId: 'DDDD000011112222' }),
        ],
      }),
    );
    renderSection();

    const list = await screen.findByRole('list', { name: 'Earlier keys' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(4);
    expect(items[0]).toHaveTextContent('Still encrypts 12 value(s) and 3 document(s), moving to the current key.');
    expect(items[1]).toHaveTextContent(
      'Still encrypts 2 stored answer(s) to repeated requests, which expire within a day.',
    );
    expect(items[2]).toHaveTextContent('Nothing is encrypted with it any more. It can be retired from');
    expect(items[3]).toHaveTextContent('Nothing is encrypted with it any more.');

    // Only a key nothing is encrypted with, a day after the rotation, can be retired.
    expect(screen.getAllByRole('button', { name: /^Retire key/ })).toHaveLength(1);
    expect(items[3]).toContainElement(screen.getByRole('button', { name: 'Retire key DDDD000011112222' }));
    expect(screen.getByText(/The original key is always kept/)).toBeInTheDocument();
  });

  it('retires an earlier key once the password is given again', async () => {
    const user = userEvent.setup();
    readStatus.mockResolvedValue(status({ keys: 3, earlier: [earlierKey()] }));
    vi.mocked(retireDataKey)
      .mockRejectedValueOnce(
        new ApiError({
          status: 400,
          title: 'Please correct the highlighted fields.',
          errors: { password: ['Password is incorrect.'] },
        }),
      )
      .mockResolvedValueOnce(status({ keys: 2 }));
    renderSection();

    await user.click(await screen.findByRole('button', { name: 'Retire key 5C0FFEE5C0FFEE51' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Retire key 5C0FFEE5C0FFEE51?' });
    expect(dialog).toHaveTextContent('Backups made before keep their own copy of the key');

    // The password first.
    await user.click(within(dialog).getByRole('button', { name: 'Retire key' }));
    expect(await within(dialog).findByText('Enter your password.')).toBeInTheDocument();
    expect(retireDataKey).not.toHaveBeenCalled();
    await user.type(within(dialog).getByLabelText(/Your password/), 'wrong{Enter}');
    expect(await within(dialog).findByText('Password is incorrect.')).toBeInTheDocument();
    await user.clear(within(dialog).getByLabelText(/Your password/));
    await user.type(within(dialog).getByLabelText(/Your password/), 'Correct-Horse-1{Enter}');

    await waitFor(() => expect(retireDataKey).toHaveBeenLastCalledWith('5C0FFEE5C0FFEE51', 'Correct-Horse-1'));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(screen.queryByRole('list', { name: 'Earlier keys' })).not.toBeInTheDocument();
    expect(notify.success).toHaveBeenCalledWith('Data key retired', expect.stringContaining('they still restore'));
  });

  it('says why a key could not be retired after all', async () => {
    const user = userEvent.setup();
    readStatus.mockResolvedValue(status({ keys: 3, earlier: [earlierKey()] }));
    const refusal = new ApiError({
      status: 422,
      title: 'Cannot complete this action',
      detail: 'Key 5C0FFEE5C0FFEE51 still encrypts 1 value. Retire it once nothing is left on it.',
    });
    vi.mocked(retireDataKey).mockRejectedValueOnce(refusal);
    renderSection();

    await user.click(await screen.findByRole('button', { name: 'Retire key 5C0FFEE5C0FFEE51' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Retire key 5C0FFEE5C0FFEE51?' });
    await user.type(within(dialog).getByLabelText(/Your password/), 'Correct-Horse-1{Enter}');

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(refusal, 'The key was not retired'));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });

  it('shows the keys, but no buttons, to those who may not rotate them', async () => {
    permissions.manage = false;
    readStatus.mockResolvedValue(
      status({ lastRotation: { at: '2026-09-01T05:00:00Z', by: 'IT Admin' }, keys: 3, earlier: [earlierKey()] }),
    );
    renderSection();

    expect(await screen.findByText(/by IT Admin/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Rotate data key' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Rotate sign-in key' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Retire key/ })).not.toBeInTheDocument();
  });
});
