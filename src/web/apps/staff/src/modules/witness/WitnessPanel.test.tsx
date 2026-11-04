import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type * as WitnessApi from './api';
import { WitnessPanel } from './WitnessPanel';

const confirm = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof WitnessApi>();
  return { ...actual, witnessApi: { ...actual.witnessApi, confirm } };
});

const CONFIRMED: WitnessApi.WitnessConfirmed = {
  confirmationId: 'c1',
  userId: 'u2',
  name: 'Sister Rani Thomas',
  expiresAt: '2026-11-04T10:05:00Z',
};

function renderPanel(witness: WitnessApi.WitnessConfirmed | null, onChange = vi.fn(), onSubmit = vi.fn()) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            onSubmit();
          }}
        >
          <WitnessPanel purpose="mar.dose" patientId="p1" witness={witness} onChange={onChange} />
          {/* As in the dose dialogs: the record's own submit button, which Enter in a field would otherwise press. */}
          <button type="submit">Record given</button>
        </form>
      </FluentProvider>
    </QueryClientProvider>,
  );
  return { onChange, onSubmit };
}

describe('WitnessPanel', () => {
  it('confirms the witness on Enter, never the form it sits in', async () => {
    confirm.mockResolvedValue(CONFIRMED);
    const { onChange, onSubmit } = renderPanel(null);

    await userEvent.type(screen.getByLabelText(/Witness's user name/), 'nurse2');
    await userEvent.type(screen.getByLabelText(/Witness's password/), 'Witness-pass-1{Enter}');

    await waitFor(() => expect(onChange).toHaveBeenCalledWith(CONFIRMED));
    expect(confirm).toHaveBeenCalledWith({
      userName: 'nurse2',
      password: 'Witness-pass-1',
      code: null,
      purpose: 'mar.dose',
      patientId: 'p1',
    });
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('names the witness once confirmed, and lets another be asked', async () => {
    const { onChange } = renderPanel(CONFIRMED);

    expect(screen.getByRole('status')).toHaveTextContent('Witnessed by Sister Rani Thomas');
    expect(screen.queryByLabelText(/Witness's password/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Change witness' }));
    expect(onChange).toHaveBeenCalledWith(null);
  });
});
