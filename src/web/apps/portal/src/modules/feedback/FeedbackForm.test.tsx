import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { SubmitFeedbackRequest } from '@/lib/types';
import { FeedbackForm } from './FeedbackForm';

const questions = { aspects: ['Overall', 'Doctor'] as const, includesNps: true, allowsComment: true };

function renderForm(onSubmit: (body: SubmitFeedbackRequest) => Promise<void>) {
  return render(
    <FluentProvider theme={webLightTheme}>
      <FeedbackForm questions={{ ...questions, aspects: [...questions.aspects] }} intro="Tell us about your visit." privacyNote="Anonymous." onSubmit={onSubmit} />
    </FluentProvider>,
  );
}

describe('FeedbackForm', () => {
  it('asks for at least one answer before sending', async () => {
    const onSubmit = vi.fn(async () => undefined);
    renderForm(onSubmit);
    await userEvent.click(screen.getByRole('button', { name: 'Send feedback' }));
    expect(await screen.findByText('Please answer at least one question.')).toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('sends star ratings, the recommendation score, a comment and a call-back number', async () => {
    const onSubmit = vi.fn(async (body: SubmitFeedbackRequest) => void body);
    renderForm(onSubmit);

    const overall = screen.getByRole('radiogroup', { name: 'Overall experience' });
    await userEvent.click(within(overall).getByRole('radio', { name: '4 of 5 stars' }));
    await userEvent.click(screen.getByRole('radio', { name: '9' }));
    await userEvent.type(screen.getByRole('textbox', { name: /Anything else/ }), 'Great care');
    await userEvent.click(screen.getByRole('checkbox', { name: 'Please call me back about this' }));
    await userEvent.type(await screen.findByRole('textbox', { name: /Phone number to call/ }), '98765 43210');
    await userEvent.click(screen.getByRole('button', { name: 'Send feedback' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0]?.[0]).toEqual({
      ratings: { overall: 4 },
      nps: 9,
      comment: 'Great care',
      wantsCallback: true,
      contactPhone: '+919876543210',
    });
  });

  it('checks the call-back number', async () => {
    const onSubmit = vi.fn(async () => undefined);
    renderForm(onSubmit);
    await userEvent.click(screen.getByRole('radio', { name: '0 = not at all likely' }));
    await userEvent.click(screen.getByRole('checkbox', { name: 'Please call me back about this' }));
    await userEvent.type(await screen.findByRole('textbox', { name: /Phone number to call/ }), '12345');
    await userEvent.click(screen.getByRole('button', { name: 'Send feedback' }));
    expect(await screen.findByText('Enter a 10-digit mobile number.')).toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();
  });
});
