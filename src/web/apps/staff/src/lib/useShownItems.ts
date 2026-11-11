import { useCallback, useLayoutEffect, useRef } from 'react';

/**
 * Remembers every item a search has shown, by key, and finds the one chosen among them. A picker that searches as the
 * user types runs the next search a moment after the last key; an option chosen then may no longer be in the results,
 * so the choice is looked up here and not in the results as they are by then. Kept for the life of the picker.
 */
export function useShownItems<T>(
  items: readonly T[],
  keyOf: (item: T) => string,
): (key: string | undefined) => T | undefined {
  const shown = useRef(new Map<string, T>());
  // Before the browser paints, so an option on screen is always one that can be found.
  useLayoutEffect(() => {
    for (const item of items) {
      shown.current.set(keyOf(item), item);
    }
  }, [items, keyOf]);
  return useCallback((key: string | undefined) => (key === undefined ? undefined : shown.current.get(key)), []);
}
