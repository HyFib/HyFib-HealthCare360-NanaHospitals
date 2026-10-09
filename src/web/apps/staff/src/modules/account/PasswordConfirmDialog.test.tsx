import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { PasswordConfirmDialog } from './PasswordConfirmDialog';

function renderDialog(error?: unknown) {
  const onConfirm = vi.fn();
  render(
    <FluentProvider theme={webLightTheme}>
      <PasswordConfirmDialog
        open
        title="Sign out everywhere else?"
        confirmLabel="Sign out"
        error={error}
        onConfirm={onConfirm}
        onCancel={vi.fn()}
      >
        Your other devices will be signed out.
      </PasswordConfirmDialog>
    </FluentProvider>,
  );
  return { onConfirm };
}

describe('PasswordConfirmDialog', () => {
  it('goes ahead only with the password', async () => {
    const { onConfirm } = renderDialog();

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByText('Enter your password.')).toBeInTheDocument();
    expect(onConfirm).not.toHaveBeenCalled();

    await userEvent.type(screen.getByLabelText(/Your password/), 'Correct-Horse-1{Enter}');
    expect(onConfirm).toHaveBeenCalledWith('Correct-Horse-1');
  });

  it('shows what the server said about the password', () => {
    renderDialog(
      new ApiError({
        status: 400,
        title: 'Please correct the highlighted fields.',
        errors: { password: ['Password is incorrect.'] },
      }),
    );

    expect(screen.getByText('Password is incorrect.')).toBeInTheDocument();
  });
});
