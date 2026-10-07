import '@testing-library/jest-dom/vitest';

// As in the staff app's test setup: jsdom does no layout, so offsetParent is always null and every box is empty.
// Tabster, which Fluent uses to move focus, reads both to tell whether an element is shown, and without them it finds
// nothing to focus. A dialog would then focus its own surface instead of its first button; Tabster makes a dialog the
// active modal only when focus is inside it, so its next aria-hidden pass (due within 250 ms of any DOM change) would
// hide the open dialog. So shown elements are reported as laid out, as in a browser.
function isShown(element: Element): boolean {
  if (!element.isConnected) {
    return false;
  }
  for (let current: Element | null = element; current; current = current.parentElement) {
    if (getComputedStyle(current).display === 'none') {
      return false;
    }
  }
  return true;
}
Object.defineProperty(HTMLElement.prototype, 'offsetParent', {
  configurable: true,
  get(this: HTMLElement): Element | null {
    const { body, documentElement } = this.ownerDocument;
    return this !== body && this !== documentElement && isShown(this) ? this.parentElement : null;
  },
});
HTMLBodyElement.prototype.getBoundingClientRect = function getBoundingClientRect() {
  const box = { x: 0, y: 0, top: 0, left: 0, right: 1280, bottom: 800, width: 1280, height: 800 };
  return { ...box, toJSON: () => box } as DOMRect;
};
