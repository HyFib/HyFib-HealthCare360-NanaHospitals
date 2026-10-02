import { ApiError } from '@hc360/core';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ErrorState } from './feedback';

describe('ErrorState', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
    // Fluent's MessageBar measures itself; jsdom has no ResizeObserver.
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );
    container = document.createElement('div');
    document.body.append(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.unstubAllGlobals();
  });

  const show = (error: unknown) => act(() => root.render(<ErrorState error={error} onRetry={() => undefined} />));
  const buttons = () => [...container.querySelectorAll('button')].map((button) => button.textContent);

  it('explains a feature an administrator switched off, without a retry or a support reference', () => {
    show(
      new ApiError({
        status: 503,
        title: 'Video consultations are turned off',
        detail: 'Book an in-person visit instead.',
        code: 'feature.disabled',
        traceId: '00-abc',
      }),
    );

    expect(container.textContent).toContain('Video consultations are turned off');
    expect(container.textContent).toContain('Book an in-person visit instead.');
    expect(container.textContent).not.toContain('Reference');
    expect(buttons()).not.toContain('Retry');
  });

  it('still offers a retry and the reference for a real failure', () => {
    show(new ApiError({ status: 500, title: 'Something went wrong', code: undefined, traceId: '00-abc' }));

    expect(container.textContent).toContain('Reference: 00-abc');
    expect(buttons()).toContain('Retry');
  });
});
