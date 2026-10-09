import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as AdminApi from './api';
import { confirmRecoveryKey, createRecoveryKey, type RecoveryKeyStatus } from './api';
import { RecoveryKeyDialog } from './RecoveryKeyDialog';

// The server, the toasts and the signed-in user are outside the test; the dialog's own steps are what is tested.
const readStatus = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof AdminApi>();
  return {
    ...actual,
    createRecoveryKey: vi.fn(),
    confirmRecoveryKey: vi.fn(),
    adminQueries: {
      ...actual.adminQueries,
      recoveryKey: () => ({ queryKey: ['admin', 'backups', 'recovery-key'], queryFn: () => readStatus() }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
const permissions = vi.hoisted(() => ({ manage: true }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => permissions.manage }));

const ORIGINAL_ID = '3F2A9C1B0D4E5F60';
const NEW_ID = '7B1E0A44C2D9F3A8';
const NEW_KEY = 'AB12-CD34-EF56-GH78-JK9M-NP0Q-RS1T-VW2X-YZ3A-B4CD-E5FG-H6JK-M7NP-Q8RS';

function status(patch: Partial<RecoveryKeyStatus> = {}): RecoveryKeyStatus {
  return {
    keyId: ORIGINAL_ID,
    original: true,
    since: null,
    by: null,
    originalShown: true,
    originalKeyId: ORIGINAL_ID,
    ...patch,
  };
}

function fieldError(field: string, message: string) {
  return new ApiError({ status: 400, title: 'Please correct the highlighted fields.', errors: { [field]: [message] } });
}

function renderDialog() {
  const onClose = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <RecoveryKeyDialog open onClose={onClose} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return { onClose };
}

describe('RecoveryKeyDialog', () => {
  beforeEach(() => {
    readStatus.mockReset();
    vi.mocked(createRecoveryKey).mockReset();
    vi.mocked(confirmRecoveryKey).mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
    permissions.manage = true;
  });

  it('says so when nobody holds the key the backups need', async () => {
    readStatus.mockResolvedValue(status({ originalShown: false }));
    renderDialog();

    expect(await screen.findByText(/Nobody holds the recovery key/)).toBeInTheDocument();
  });

  it('shows a new key once, after the password, and uses it once it is typed back', async () => {
    const user = userEvent.setup();
    readStatus.mockResolvedValue(status());
    vi.mocked(createRecoveryKey)
      .mockRejectedValueOnce(fieldError('password', 'Password is incorrect.'))
      .mockResolvedValueOnce({ keyId: NEW_ID, key: NEW_KEY, expiresAt: '2026-10-09T07:00:00Z' });
    vi.mocked(confirmRecoveryKey)
      .mockRejectedValueOnce(fieldError('key', 'This is not the new key.'))
      .mockResolvedValueOnce(status({ keyId: NEW_ID, original: false, since: '2026-10-09T06:35:00Z', by: 'admin' }));
    const { onClose } = renderDialog();

    expect(await screen.findByText(/still use the original recovery key/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Make a new key' }));

    // The password first.
    await user.click(screen.getByRole('button', { name: 'Show the new key' }));
    expect(await screen.findByText('Enter your password.')).toBeInTheDocument();
    expect(createRecoveryKey).not.toHaveBeenCalled();
    await user.type(screen.getByLabelText(/Your password/), 'wrong{Enter}');
    expect(await screen.findByText('Password is incorrect.')).toBeInTheDocument();
    await user.clear(screen.getByLabelText(/Your password/));
    await user.type(screen.getByLabelText(/Your password/), 'Correct-Horse-1{Enter}');

    // Then the key, in groups, with its id.
    expect(await screen.findByRole('group', { name: 'New recovery key' })).toHaveTextContent(
      NEW_KEY.replaceAll('-', ''),
    );
    expect(screen.getByText(NEW_ID)).toBeInTheDocument();
    expect(createRecoveryKey).toHaveBeenLastCalledWith('Correct-Horse-1');

    // Typed back from the copy, with the key no longer on screen.
    await user.click(screen.getByRole('button', { name: 'I have written it down' }));
    expect(screen.queryByRole('group', { name: 'New recovery key' })).not.toBeInTheDocument();
    await user.type(screen.getByLabelText(/Recovery key/), 'AB12 CD34 wrong{Enter}');
    expect(await screen.findByText('This is not the new key.')).toBeInTheDocument();

    // Back to check the copy, then again; what was typed stays, to be corrected.
    await user.click(screen.getByRole('button', { name: 'Back to the key' }));
    expect(screen.getByRole('group', { name: 'New recovery key' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'I have written it down' }));
    expect(screen.getByLabelText(/Recovery key/)).toHaveValue('AB12 CD34 wrong');
    await user.clear(screen.getByLabelText(/Recovery key/));
    await user.type(screen.getByLabelText(/Recovery key/), NEW_KEY.toLowerCase());
    await user.click(screen.getByRole('button', { name: 'Use the new key' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(confirmRecoveryKey).toHaveBeenLastCalledWith(NEW_ID, NEW_KEY.toLowerCase());
    expect(notify.success).toHaveBeenCalledWith('New recovery key in use', expect.stringContaining(NEW_ID));
  });

  it('starts again when the new key is no longer waiting', async () => {
    const user = userEvent.setup();
    readStatus.mockResolvedValue(status());
    vi.mocked(createRecoveryKey).mockResolvedValue({ keyId: NEW_ID, key: NEW_KEY, expiresAt: '2026-10-09T07:00:00Z' });
    vi.mocked(confirmRecoveryKey).mockRejectedValue(
      new ApiError({
        status: 422,
        title: 'Cannot complete this action',
        code: 'backup.recovery_key_not_waiting',
        detail: 'This recovery key is no longer waiting to be confirmed, so nothing changed. Make a new one.',
      }),
    );
    renderDialog();

    await user.click(await screen.findByRole('button', { name: 'Make a new key' }));
    await user.type(screen.getByLabelText(/Your password/), 'Correct-Horse-1{Enter}');
    await user.click(await screen.findByRole('button', { name: 'I have written it down' }));
    await user.type(screen.getByLabelText(/Recovery key/), `${NEW_KEY}{Enter}`);

    await waitFor(() =>
      expect(notify.error).toHaveBeenCalledWith(expect.any(ApiError), 'The new recovery key was not put to use'),
    );
    expect(await screen.findByRole('button', { name: 'Make a new key' })).toBeInTheDocument();
  });

  it('shows the key in use, but no way to make one, to those who may not', async () => {
    permissions.manage = false;
    readStatus.mockResolvedValue(
      status({ keyId: NEW_ID, original: false, since: '2026-10-09T06:35:00Z', by: 'IT Admin' }),
    );
    renderDialog();

    expect(await screen.findByText(NEW_ID)).toBeInTheDocument();
    expect(screen.getByText(/by IT Admin/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Make a new key' })).not.toBeInTheDocument();
    expect(screen.getByText(/SuperAdmin by default/)).toBeInTheDocument();
  });
});
