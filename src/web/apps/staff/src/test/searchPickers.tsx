import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import type { UserEvent } from '@testing-library/user-event';
import type { ReactNode } from 'react';

// Pickers that search as the user types run the search a moment (200–300 ms) after the last key. A choice made as that
// search started used to be lost: the options went away while it ran, and nothing was chosen. A fast machine clicked
// first; the release build on Windows, a slow machine or a quick person did not.

/** Longer than any picker's wait after the last key, so the search for what was typed has started. */
const PAST_THE_SEARCH_DELAY_MS = 400;

/** Renders a picker with a query cache of its own, in Fluent's light theme. Answer its API with `fakeServer`. */
export function renderPicker(ui: ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>{ui}</FluentProvider>
    </QueryClientProvider>,
  );
  return { queryClient };
}

/**
 * Types `more` into `combobox` and chooses `option` 400 ms later, as the search for the longer text runs. The option
 * must be on show already, from the search before: it is the one clicked, as a person's pointer would be on it. Typing
 * goes on without a click, which would close the open list.
 */
export async function typeAndChooseAsTheSearchRuns(
  user: UserEvent,
  combobox: HTMLElement,
  more: string,
  option: string | RegExp,
) {
  await user.type(combobox, more, { skipClick: true });
  const shown = screen.getByRole('option', { name: option });
  await new Promise((resolve) => setTimeout(resolve, PAST_THE_SEARCH_DELAY_MS));
  await user.click(shown);
}
