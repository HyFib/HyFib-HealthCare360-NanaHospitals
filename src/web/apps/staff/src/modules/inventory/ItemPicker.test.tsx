import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { SearchPicker } from './ItemPicker';

describe('SearchPicker', () => {
  it('never submits the form around it with Enter, even with too little typed to search', async () => {
    const onSubmit = vi.fn((event: { preventDefault: () => void }) => event.preventDefault());
    const fetch = vi.fn(() => Promise.resolve<string[]>([]));
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <FluentProvider theme={webLightTheme}>
          <form onSubmit={onSubmit}>
            <SearchPicker<string>
              label="Item"
              queryKey={['test-picker']}
              fetch={fetch}
              getKey={(item) => item}
              renderOption={(item) => item}
              onPick={() => {}}
            />
            <button type="submit">Save</button>
          </form>
        </FluentProvider>
      </QueryClientProvider>,
    );

    await userEvent.type(screen.getByRole('combobox', { name: 'Item' }), 'p{Enter}');
    expect(onSubmit).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalled();
  });
});
