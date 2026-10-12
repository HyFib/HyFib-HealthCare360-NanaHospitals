import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError, formatDate } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as PatientsApi from './api';
import { consentApi, type ConsentDto } from './api';
import { ConsentsPanel, RecordConsentForm } from './ConsentsPanel';

// The server, the toasts and the signed-in user are outside the test; the panel's own behaviour is what is tested.
const listConsents = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof PatientsApi>();
  return {
    ...actual,
    consentApi: { capture: vi.fn(), revoke: vi.fn() },
    patientQueries: {
      ...actual.patientQueries,
      consents: (id: string) => ({ queryKey: actual.patientKeys.consents(id), queryFn: () => listConsents(id) }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

function renderWithProviders(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>{ui}</FluentProvider>
    </QueryClientProvider>,
  );
}

function renderForm() {
  const onClose = vi.fn();
  renderWithProviders(<RecordConsentForm patientId="p1" onClose={onClose} />);
  return { onClose, save: screen.getByRole('button', { name: 'Save' }) };
}

function consent(patch: Partial<ConsentDto>): ConsentDto {
  return {
    id: 'c1',
    type: 'DataSharing',
    consentVersion: '1.0',
    granted: true,
    capturedAt: '2026-09-20T05:00:00Z',
    capturedByName: 'Asha Nair',
    witnessName: null,
    signedByRelation: null,
    signedByName: null,
    documentFileId: null,
    revokedAt: null,
    revokedReason: null,
    revokedByName: null,
    notes: null,
    isActive: true,
    ...patch,
  };
}

describe('RecordConsentForm', () => {
  beforeEach(() => {
    vi.mocked(consentApi.capture).mockReset().mockResolvedValue({ id: 'new' });
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('saves only once the consent is chosen, then says what changes and closes', async () => {
    const user = userEvent.setup();
    const { onClose, save } = renderForm();
    expect(save).toBeDisabled();

    await user.selectOptions(screen.getByRole('combobox', { name: /^Consent/ }), 'DataSharing');
    await waitFor(() => expect(save).toBeEnabled());
    await user.click(save);

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(consentApi.capture).toHaveBeenCalledWith('p1', {
      type: 'DataSharing',
      consentVersion: null,
      granted: true,
      witnessName: null,
      signedByRelation: null,
      signedByName: null,
      documentFileId: null,
      notes: null,
    });
    expect(notify.success).toHaveBeenCalledWith(
      'Consent recorded',
      'HC360 can now share this patient’s records with other care providers.',
    );
  });

  it('needs the name and relation of someone signing on the patient’s behalf', async () => {
    const user = userEvent.setup();
    const { save } = renderForm();
    await user.selectOptions(screen.getByRole('combobox', { name: /^Consent/ }), 'Anaesthesia');
    await user.click(screen.getByRole('radio', { name: 'Refused' }));
    await user.click(screen.getByRole('radio', { name: 'Someone on their behalf' }));
    await waitFor(() => expect(save).toBeDisabled());

    await user.type(screen.getByRole('textbox', { name: /^Name of the person signing/ }), 'Ravi Kumar');
    await user.type(screen.getByRole('textbox', { name: /^Relation to the patient/ }), 'son');
    await waitFor(() => expect(save).toBeEnabled());
    await user.click(save);

    await waitFor(() =>
      expect(consentApi.capture).toHaveBeenCalledWith(
        'p1',
        expect.objectContaining({
          type: 'Anaesthesia',
          granted: false,
          signedByName: 'Ravi Kumar',
          signedByRelation: 'son',
        }),
      ),
    );
    expect(notify.success).toHaveBeenCalledWith('Refusal recorded', 'Tell the treating doctor.');
  });

  it('shows the server’s answers in the dialog, not in a toast', async () => {
    const user = userEvent.setup();
    const { onClose, save } = renderForm();
    vi.mocked(consentApi.capture)
      .mockRejectedValueOnce(
        new ApiError({
          status: 403,
          title: 'Not allowed',
          code: 'patient.restricted',
          detail: 'This is a restricted record.',
        }),
      )
      .mockRejectedValueOnce(
        new ApiError({
          status: 400,
          title: 'Please correct the highlighted fields.',
          errors: { witnessName: ['Contains characters that are not allowed.'] },
        }),
      );
    await user.selectOptions(screen.getByRole('combobox', { name: /^Consent/ }), 'Research');
    await waitFor(() => expect(save).toBeEnabled());

    await user.click(save);
    expect(await screen.findByText('This is a restricted record.')).toBeInTheDocument();
    expect(screen.getByText('The consent was not saved')).toBeInTheDocument();

    await user.click(save);
    expect(await screen.findByText('Contains characters that are not allowed.')).toBeInTheDocument();
    expect(screen.queryByText('The consent was not saved')).not.toBeInTheDocument();
    const witness = screen.getByRole('textbox', { name: /^Witness/ });
    expect(witness).toHaveFocus();
    expect(save).toBeDisabled();

    await user.type(witness, 'Sister Mary');
    await waitFor(() => expect(save).toBeEnabled());
    expect(onClose).not.toHaveBeenCalled();
    expect(notify.error).not.toHaveBeenCalled();
  });
});

describe('ConsentsPanel', () => {
  beforeEach(() => {
    listConsents.mockReset();
  });

  it('lists each consent with its decision, and offers withdrawal only for one that stands', async () => {
    listConsents.mockResolvedValue([
      consent({ id: 'given', notes: 'Wants records sent to Apollo only.' }),
      consent({
        id: 'withdrawn',
        type: 'SmsWhatsAppUpdates',
        capturedAt: '2026-06-01T05:00:00Z',
        signedByName: 'Ravi Kumar',
        signedByRelation: 'son',
        witnessName: 'Sister Mary',
        revokedAt: '2026-09-01T05:00:00Z',
        revokedByName: 'Dr. Priya Shah',
        revokedReason: 'Patient asked at the desk.',
        isActive: false,
      }),
    ]);
    renderWithProviders(<ConsentsPanel patientId="p1" />);

    await screen.findByText('Sharing records with other care providers');
    const [, given, withdrawn] = within(screen.getByRole('table', { name: 'Consents' })).getAllByRole('row');
    expect(given).toHaveTextContent('Sharing records with other care providers');
    expect(given).toHaveTextContent('Wants records sent to Apollo only.');
    expect(within(given!).getByText('Given')).toBeInTheDocument();
    expect(given).toHaveTextContent('by Asha Nair');

    expect(withdrawn).toHaveTextContent('SMS and WhatsApp updates');
    expect(within(withdrawn!).getByText('Withdrawn')).toBeInTheDocument();
    expect(withdrawn).toHaveTextContent('by Dr. Priya Shah');
    expect(withdrawn).toHaveTextContent('Reason: Patient asked at the desk.');
    expect(withdrawn).toHaveTextContent('Ravi Kumar (son)');
    expect(withdrawn).toHaveTextContent('Witness: Sister Mary');

    const withdraw = screen.getAllByRole('button', { name: /^Withdraw consent/ });
    expect(withdraw).toHaveLength(1);
    expect(withdraw[0]).toHaveAccessibleName(
      `Withdraw consent: Sharing records with other care providers, given ${formatDate('2026-09-20T05:00:00Z')}`,
    );
    expect(screen.getByRole('button', { name: 'Record consent' })).toBeInTheDocument();
  });

  it('explains a restricted record and hands over to the page’s emergency access', async () => {
    const user = userEvent.setup();
    listConsents.mockRejectedValue(
      new ApiError({ status: 403, title: 'Not allowed', code: 'patient.restricted', detail: 'Restricted.' }),
    );
    renderWithProviders(<ConsentsPanel patientId="p1" />);

    expect(await screen.findByText('Restricted record')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Request emergency access' }));
    await waitFor(() => expect(listConsents).toHaveBeenCalledTimes(2));
  });

  it('asks before Esc or Cancel throw a consent being recorded away (SRS UX-08)', async () => {
    const user = userEvent.setup();
    listConsents.mockResolvedValue([]);
    renderWithProviders(<ConsentsPanel patientId="p1" />);
    const record = await screen.findByRole('button', { name: 'Record consent' });
    record.focus();
    await user.keyboard('{Enter}');
    const dialog = await screen.findByRole('dialog');
    const kind = within(dialog).getByRole('combobox', { name: /^Consent/ });
    await user.selectOptions(kind, 'DataSharing');

    kind.focus();
    await user.keyboard('{Escape}');
    const question = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
    within(question).getByRole('button', { name: 'Keep editing' }).focus();
    await user.keyboard('{Enter}');
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(dialog).toBeInTheDocument();
    expect(kind).toHaveValue('DataSharing');
    await waitFor(() => expect(kind).toHaveFocus());

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    within(await screen.findByRole('alertdialog'))
      .getByRole('button', { name: 'Discard changes' })
      .focus();
    await user.keyboard('{Enter}');

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(record).toHaveFocus());
    expect(consentApi.capture).not.toHaveBeenCalled();
  });
});
