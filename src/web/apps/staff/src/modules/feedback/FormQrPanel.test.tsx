import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { deferred, dropFocusIfDisabled } from '@/test/busyButtons';
import type { FeedbackFormDto } from './api';
import { FormQrPanel } from './FormQrPanel';

// The printing and the toasts are outside the test: what is tested is where focus stays while a poster prints.
const printQrPoster = vi.hoisted(() => vi.fn());
vi.mock('./print', () => ({ printQrPoster }));
const notify = vi.hoisted(() => ({ success: vi.fn(), warning: vi.fn(), error: vi.fn(), info: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const form: FeedbackFormDto = {
  id: 'f1',
  code: 'OPD',
  title: 'Out-patient feedback',
  type: 'General',
  aspects: ['Doctor', 'Overall'],
  includesNps: true,
  allowsComment: true,
  introText: null,
  thankYouText: null,
  departmentId: null,
  publicToken: 'abc123',
  qrUrl: 'https://portal.nanahospitals.example/feedback/abc123',
  isActive: true,
  version: 1,
};

function renderPanel() {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <FormQrPanel form={form} canManage={false} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('FormQrPanel', () => {
  beforeEach(() => {
    printQrPoster.mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('keeps focus on Print poster while the poster prints, and prints one at a time', async () => {
    const printing = deferred();
    printQrPoster.mockReturnValue(printing.promise);
    renderPanel();
    const poster = screen.getByRole('button', { name: 'Print poster' });
    const kioskCard = screen.getByRole('button', { name: 'Print kiosk card' });

    poster.focus();
    await userEvent.keyboard('{Enter}');
    expect(printQrPoster).toHaveBeenCalledWith(expect.objectContaining({ url: form.qrUrl, kiosk: false }));
    dropFocusIfDisabled(poster);

    expect(poster).toHaveFocus();
    expect(poster).toHaveAttribute('aria-disabled', 'true');
    expect(kioskCard).toHaveAttribute('aria-disabled', 'true');
    await userEvent.keyboard('{Enter}');
    expect(printQrPoster).toHaveBeenCalledTimes(1);

    printing.resolve();
    await waitFor(() => expect(poster).not.toHaveAttribute('aria-disabled'));
    expect(poster).toHaveFocus();
    expect(kioskCard).not.toHaveAttribute('aria-disabled');
  });
});
