import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { AddFamilySheet } from './AddFamilySheet';
import type * as FamilyApi from './api';
import { familyApi } from './api';

// The server and the toasts are outside the test; the sheet's own behaviour is what is tested.
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof FamilyApi>()),
  familyApi: { requestCode: vi.fn(), verifyCode: vi.fn(), end: vi.fn() },
}));
const toast = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), failure: vi.fn() }));
vi.mock('@/lib/toast', () => ({ useToast: () => toast }));

const SLOW = { timeout: 8000 };

beforeAll(() => {
  window.matchMedia ??= ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  })) as typeof window.matchMedia;
});

beforeEach(() => {
  vi.mocked(familyApi.requestCode)
    .mockReset()
    .mockResolvedValue({
      codeId: 'code-1',
      expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
    });
  vi.mocked(familyApi.verifyCode).mockReset().mockResolvedValue({
    linkId: 'link-1',
    patientId: 'patient-2',
    fullName: 'Meera Verma',
    uhid: 'NH26000124',
    relation: 'Parent',
    source: 'PortalCode',
    linkedAt: '2026-10-07T09:00:00Z',
  });
  toast.success.mockClear();
});

function renderSheet() {
  const onClose = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <AddFamilySheet open onClose={onClose} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return { onClose };
}

async function fillDetails(user: ReturnType<typeof userEvent.setup>) {
  await user.type(await screen.findByRole('textbox', { name: /^Their UHID/ }, SLOW), 'nh26000124');
  await user.type(screen.getByRole('textbox', { name: /^Their mobile number/ }), '98111 22233');
  await user.selectOptions(screen.getByRole('combobox', { name: /^They are your/ }), 'Parent');
  await user.click(screen.getByRole('button', { name: 'Send the code' }));
}

describe('AddFamilySheet', () => {
  it('checks the details before asking for a code', async () => {
    const user = userEvent.setup();
    renderSheet();
    await user.click(await screen.findByRole('button', { name: 'Send the code' }, SLOW));

    expect(await screen.findByText('Enter the UHID printed on their hospital card or a bill.')).toBeInTheDocument();
    expect(screen.getByText('Enter their 10-digit mobile number.')).toBeInTheDocument();
    expect(screen.getByText('Choose how they are related to you.')).toBeInTheDocument();
    expect(familyApi.requestCode).not.toHaveBeenCalled();
  });

  it('adds the relative with the code they pass on', async () => {
    const user = userEvent.setup();
    const { onClose } = renderSheet();
    await fillDetails(user);

    expect(familyApi.requestCode).toHaveBeenCalledWith({
      uhid: 'NH26000124',
      phone: '+919811122233',
      relation: 'Parent',
    });
    expect(
      await screen.findByText(
        'If the UHID and the mobile number ending 2233 belong to the same patient, we have sent that number a code by SMS. Ask them for it. It works for 10 minutes.',
      ),
    ).toBeInTheDocument();
    const add = screen.getByRole('button', { name: 'Add to my family' });
    expect(add).toBeDisabled();
    await user.type(screen.getByRole('textbox', { name: /^Code/ }), '48 29 13');
    await user.click(add);

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(familyApi.verifyCode).toHaveBeenCalledWith('code-1', '482913');
    expect(toast.success).toHaveBeenCalledWith('Meera Verma is now in your family.');
  });

  it('says when the code is wrong, and when a new one is needed', async () => {
    const user = userEvent.setup();
    const { onClose } = renderSheet();
    vi.mocked(familyApi.verifyCode)
      .mockRejectedValueOnce(
        new ApiError({ status: 400, title: 'Invalid', errors: { code: ['The code is incorrect.'] } }),
      )
      .mockRejectedValueOnce(
        new ApiError({ status: 422, title: 'Not allowed', code: 'portal.family_code_expired', detail: 'English.' }),
      );
    await fillDetails(user);
    const code = await screen.findByRole('textbox', { name: /^Code/ });

    await user.type(code, '111111');
    await user.click(screen.getByRole('button', { name: 'Add to my family' }));
    expect(
      await screen.findByText('The code is not right. Check the SMS they received and try again.'),
    ).toBeInTheDocument();

    await user.type(code, '222222');
    await user.click(screen.getByRole('button', { name: 'Add to my family' }));
    expect(await screen.findByText('This code can no longer be used. Ask for a new one.')).toBeInTheDocument();
    expect(code).toBeDisabled();

    // Back to the details as typed, for a new code.
    await user.click(screen.getByRole('button', { name: 'Ask for a new code' }));
    expect(await screen.findByRole('textbox', { name: /^Their UHID/ })).toHaveValue('nh26000124');
    expect(onClose).not.toHaveBeenCalled();
  });

  it('shows a refusal of the details in the patient’s language', async () => {
    const user = userEvent.setup();
    renderSheet();
    vi.mocked(familyApi.requestCode).mockRejectedValueOnce(
      new ApiError({ status: 422, title: 'Not allowed', code: 'portal.family_code_throttled', detail: 'English.' }),
    );
    await fillDetails(user);

    expect(
      await screen.findByText('Several codes were asked for just now. Please try again in a few minutes.'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send the code' })).toBeEnabled();
  });

  it('closes on "Not now" without asking while nothing was typed', async () => {
    const user = userEvent.setup();
    const { onClose } = renderSheet();

    await user.click(await screen.findByRole('button', { name: 'Not now' }, SLOW));

    expect(onClose).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });

  it('asks before "Not now" or Esc throw the typed details away (SRS UX-08)', async () => {
    const user = userEvent.setup();
    const { onClose } = renderSheet();
    const uhid = await screen.findByRole('textbox', { name: /^Their UHID/ }, SLOW);
    await user.type(uhid, 'nh26000124');

    await user.click(screen.getByRole('button', { name: 'Not now' }));
    let question = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
    expect(question).toHaveTextContent('What you changed here has not been saved.');
    within(question).getByRole('button', { name: 'Keep editing' }).focus();
    await user.keyboard('{Enter}');
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(onClose).not.toHaveBeenCalled();
    expect(uhid).toHaveValue('nh26000124');

    uhid.focus();
    await user.keyboard('{Escape}');
    question = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
    await user.click(within(question).getByRole('button', { name: 'Discard changes' }));

    expect(onClose).toHaveBeenCalledTimes(1);
    expect(familyApi.requestCode).not.toHaveBeenCalled();
  });
});
