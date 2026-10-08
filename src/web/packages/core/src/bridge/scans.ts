import { isDesktop, onBridgeEvent } from './bridge';

/**
 * Serial barcode scanners (INT-BAR). HC360 Desktop reads a scanner on a COM port and sends each code as a
 * `scanner.scan` event; it is typed into the focused field of the window in front and followed by Enter, as a scanner
 * that types like a keyboard would. So every scan box works with either kind of scanner, and a scan with no field
 * focused, or in a window behind, goes nowhere.
 */
export function startSerialScans(): () => void {
  if (!isDesktop) {
    return () => undefined;
  }
  return onBridgeEvent<{ text?: string | null }>('scanner.scan', (payload) => {
    if (payload?.text && document.hasFocus()) {
      typeScan(document.activeElement, payload.text);
    }
  });
}

/** Fields a scan may be typed into: never a password, a checkbox or a button. */
const TEXT_TYPES = new Set(['text', 'search', 'tel', 'url', 'email', 'number']);

/**
 * Types a scanned code into a text field as a keyboard scanner would: the code at the cursor (replacing any selection),
 * then Enter, which in a text area starts a new line unless a handler took it. React sees the change as typing.
 * Returns whether the field took the scan.
 */
export function typeScan(target: Element | null, text: string): boolean {
  const field =
    target instanceof HTMLInputElement && TEXT_TYPES.has(target.type)
      ? target
      : target instanceof HTMLTextAreaElement
        ? target
        : null;
  if (!field || field.disabled || field.readOnly) {
    return false;
  }

  insert(field, text);
  const enter = { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true };
  const unhandled = field.dispatchEvent(new KeyboardEvent('keydown', enter));
  if (unhandled && field instanceof HTMLTextAreaElement) {
    insert(field, '\n');
  }
  field.dispatchEvent(new KeyboardEvent('keyup', enter));
  return true;
}

function insert(field: HTMLInputElement | HTMLTextAreaElement, text: string): void {
  // Fields without a cursor (number, email) report none: the code goes at the end.
  let start = field.value.length;
  let end = start;
  try {
    start = field.selectionStart ?? start;
    end = field.selectionEnd ?? start;
  } catch {
    // Older engines throw instead.
  }
  const value = field.value.slice(0, start) + text + field.value.slice(end);

  // The prototype's setter, not the field's own: React watches the field's, and would take the new value for its own
  // and skip onChange.
  const prototype = field instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(prototype, 'value')?.set?.call(field, value);
  try {
    field.setSelectionRange(start + text.length, start + text.length);
  } catch {
    // Number and email fields have no cursor to move.
  }
  field.dispatchEvent(new Event('input', { bubbles: true }));
}
