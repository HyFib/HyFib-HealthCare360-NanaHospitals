import { useSession } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { TELEHEALTH_CONSENT_NOTE, TelehealthConsentDialog } from './SessionDialogs';

// The doctor records the patient's consent to be seen by video, given by voice at the start of the call (Telemedicine
// Practice Guidelines 2020): without it the patient cannot be admitted. The server is a fake.

const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const SESSION_ID = '0192a000-0000-7000-8000-00000000d101';
const onRecorded = vi.fn();
const onClose = vi.fn();
let server: ReturnType<typeof fakeServer>;

function showDialog() {
  server = fakeServer({
    [`POST /api/v1/telehealth/sessions/${SESSION_ID}/telehealth-consent`]: {
      id: SESSION_ID,
      telehealthConsentGivenAt: '2026-11-08T05:00:00Z',
    },
  });
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 3600_000,
    user: STAFF_USER,
    locked: false,
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <Hc360ThemeProvider>
        <TelehealthConsentDialog
          open
          sessionId={SESSION_ID}
          patientName="Lakshmi Video"
          onClose={onClose}
          onRecorded={onRecorded}
        />
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('consent to a video consultation', () => {
  it('records what the patient was told and agreed to, then lets the doctor admit them', async () => {
    const user = userEvent.setup();
    showDialog();
    const dialog = await screen.findByRole('dialog', { name: 'Consent to a video consultation' });
    expect(within(dialog).getByText(/Lakshmi Video has not consented to be seen by video/)).toBeInTheDocument();
    const notes = within(dialog).getByRole('textbox', { name: /What the patient was told and agreed to/ });
    expect(notes).toHaveValue(TELEHEALTH_CONSENT_NOTE);

    await user.click(within(dialog).getByRole('button', { name: 'The patient agrees' }));

    await waitFor(() => expect(onRecorded).toHaveBeenCalled());
    const posted = server.requests.find((r) => r.path.endsWith('/telehealth-consent'));
    expect(posted?.body).toEqual({ notes: TELEHEALTH_CONSENT_NOTE });
  });

  it('asks for a note before recording', async () => {
    const user = userEvent.setup();
    showDialog();
    const dialog = await screen.findByRole('dialog', { name: 'Consent to a video consultation' });
    await user.clear(within(dialog).getByRole('textbox', { name: /What the patient was told/ }));

    expect(within(dialog).getByRole('button', { name: 'The patient agrees' })).toBeDisabled();
  });

  it('has no accessibility problems', async () => {
    const { baseElement } = showDialog();
    await screen.findByRole('dialog', { name: 'Consent to a video consultation' });
    await expectNoAxeViolations(baseElement);
  });
});
