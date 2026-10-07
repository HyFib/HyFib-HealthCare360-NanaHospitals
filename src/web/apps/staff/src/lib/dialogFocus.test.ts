import type { KeyboardEvent } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ignoreEscapeWhile, keepFocusInOpenDialogs } from './dialogFocus';

// jsdom sends no focusout when a focused element is disabled or removed; blur() sends the one a browser sends first.
const settle = () => new Promise<void>((resolve) => queueMicrotask(resolve));

describe('keepFocusInOpenDialogs', () => {
  let stop: () => void;
  let dialog: HTMLDivElement;
  let button: HTMLButtonElement;

  beforeEach(() => {
    stop = keepFocusInOpenDialogs(document);
    dialog = document.createElement('div');
    dialog.setAttribute('role', 'dialog');
    dialog.setAttribute('aria-modal', 'true');
    dialog.tabIndex = -1;
    button = document.createElement('button');
    dialog.append(button);
    document.body.append(dialog);
    button.focus();
  });

  afterEach(() => {
    stop();
    dialog.remove();
  });

  it('keeps focus in the open dialog when the focused button is disabled', async () => {
    button.blur();
    button.disabled = true;
    await settle();

    expect(dialog).toHaveFocus();
  });

  it('keeps focus in the open dialog when the focused element is removed', async () => {
    button.blur();
    button.remove();
    await settle();

    expect(dialog).toHaveFocus();
  });

  it('leaves focus alone when the dialog itself closes', async () => {
    button.blur();
    dialog.remove();
    await settle();

    expect(document.body).toHaveFocus();
  });

  it('leaves focus alone when it moves to another element', async () => {
    const other = document.createElement('button');
    dialog.append(other);
    other.focus();
    await settle();

    expect(other).toHaveFocus();
  });

  it('ignores elements outside a modal dialog', async () => {
    const outside = document.createElement('button');
    document.body.append(outside);
    outside.focus();
    outside.blur();
    await settle();

    expect(document.body).toHaveFocus();
    outside.remove();
  });
});

describe('ignoreEscapeWhile', () => {
  const press = (key: string, busy: boolean) => {
    const event = { key, preventDefault: vi.fn() };
    ignoreEscapeWhile(busy)(event as unknown as KeyboardEvent<HTMLElement>);
    return event.preventDefault;
  };

  it('handles Escape while busy, so the dialog stays open, and only then', () => {
    expect(press('Escape', true)).toHaveBeenCalled();
    expect(press('Escape', false)).not.toHaveBeenCalled();
    expect(press('Enter', true)).not.toHaveBeenCalled();
  });
});
