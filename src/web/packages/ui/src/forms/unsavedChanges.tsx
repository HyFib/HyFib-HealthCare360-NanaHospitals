import { useRestoreFocusTarget } from '@fluentui/react-components';
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { ConfirmDialog } from '../feedback/feedback';

// Unsaved-change guards (SRS UX-08): typing is never lost without a question. A form with changes asks "Discard your
// changes?" before it closes (Cancel, the close button, Esc, a click outside), and the app asks "Leave without saving?"
// before it leaves the screen (a menu link, Back) or the window closes. Forms tell the guard whether they have changes;
// one blocker per app (LeaveGuard, in each app's root route) asks every registered form before a navigation.

/** Where the app is, as the router hands it to a navigation blocker (TanStack Router's locations fit). */
export interface Place {
  pathname: string;
  search?: unknown;
}

export interface Navigation {
  current: Place;
  next: Place;
}

/** What the app asks before leaving a form's changes behind. */
export interface LeaveQuestion {
  title: string;
  body: ReactNode;
  confirmLabel: string;
  /** Leaving loses the changes for good. False when a draft is kept on this computer. */
  destructive?: boolean;
}

interface Registration {
  /** Whether leaving for `navigation.next` loses the changes; without a navigation, whether closing the window would. */
  blocks: (navigation?: Navigation) => boolean;
  question: () => LeaveQuestion | undefined;
  /** The user chose to leave the changes behind (confirmLeaving): leaving no longer asks. */
  allow: () => void;
}

const registrations = new Set<Registration>();

type RegisterCloseRequest = (requestClose: () => void) => () => void;

/** From a dialog whose content mounts while it is open: where the form inside takes the dialog's close requests. */
const CloseRequestContext = createContext<RegisterCloseRequest | null>(null);

function blocking(navigation?: Navigation): Registration[] {
  return [...registrations].filter((registration) => registration.blocks(navigation));
}

/**
 * Whether leaving now loses unsaved changes: going to `navigation.next`, or, without a navigation, closing or reloading
 * the window. For each app's LeaveGuard and its beforeunload prompt.
 */
export function wouldLoseChanges(navigation?: Navigation): boolean {
  return blocking(navigation).length > 0;
}

/**
 * What to ask before leaving: undefined for the app's own "Leave without saving?". A form's own question is asked only
 * when it covers everything leaving would lose: a form that keeps a draft does not speak for another that would lose
 * its changes for good.
 */
export function leaveQuestionFor(navigation?: Navigation): LeaveQuestion | undefined {
  const questions = blocking(navigation).map((registration) => registration.question());
  if (questions.some((question) => question === undefined)) {
    return undefined;
  }
  return questions.findLast((question) => question?.destructive !== false) ?? questions.at(-1);
}

type Ask = (navigation?: Navigation) => Promise<boolean>;

let asker: Ask | null = null;

/** For each app's LeaveGuard: how it asks "Leave without saving?" for `confirmLeaving`. Returns how to stop. */
export function askToLeaveWith(ask: Ask): () => void {
  asker = ask;
  return () => {
    if (asker === ask) {
      asker = null;
    }
  };
}

/**
 * Before leaving in a way the router cannot stop first (signing out, switching facility): asks "Leave without saving?"
 * when a form has unsaved changes, and answers whether to go on. Without a navigation, every form counts, as when the
 * window closes. Once the user chose to leave, the navigations that follow do not ask again.
 */
export async function confirmLeaving(navigation?: Navigation): Promise<boolean> {
  const losing = blocking(navigation);
  if (losing.length === 0 || !asker) {
    return true;
  }
  const leave = await asker(navigation);
  if (leave) {
    losing.forEach((registration) => registration.allow());
  }
  return leave;
}

/** By default a form is lost when the app moves to another screen; a change of search parameters keeps it. */
export function leavesScreen(current: Place, next: Place): boolean {
  return current.pathname !== next.pathname;
}

export interface LeaveGuardOptions {
  /** The form differs from what was saved: react-hook-form's `formState.isDirty`, or `useValuesChanged(...)`. */
  dirty: boolean;
  /**
   * A save is running. Navigations are not asked about meanwhile, so a save that goes on to the new record does not
   * ask; closing the window still is.
   */
  busy?: boolean;
  /** Whether moving from `current` to `next` loses the form. By default, moving to another screen does. */
  leaves?: (current: Place, next: Place) => boolean;
  /** Asked instead of "Leave without saving?". */
  leaveQuestion?: LeaveQuestion;
}

export interface LeaveGuard {
  /** Lets the app leave without asking, after a save or once the user chose to discard the changes. */
  allowLeaving: () => void;
}

/**
 * For a form that is the whole screen: leaving it with unsaved changes (a menu link, Back, closing the window) asks
 * first. Forms that close (dialogs, drawers, editors) use `useUnsavedChanges`, which includes this.
 */
export function useLeaveGuard({
  dirty,
  busy = false,
  leaves = leavesScreen,
  leaveQuestion,
}: LeaveGuardOptions): LeaveGuard {
  const leaving = useRef(false);
  // As last rendered: the router asks during a navigation, which a save's success handler may start before the form
  // renders again (still busy).
  const latest = useRef({ busy, leaves, leaveQuestion });
  useEffect(() => {
    latest.current = { busy, leaves, leaveQuestion };
  });

  useEffect(() => {
    if (!dirty) {
      return undefined;
    }
    // Changed (again): leaving asks, even if it was allowed before (a dialog opened again, say).
    leaving.current = false;
    const registration: Registration = {
      blocks: (navigation) => {
        if (leaving.current) {
          return false;
        }
        const { busy: saving, leaves: loses } = latest.current;
        return navigation ? !saving && loses(navigation.current, navigation.next) : true;
      },
      question: () => latest.current.leaveQuestion,
      allow: () => {
        leaving.current = true;
      },
    };
    registrations.add(registration);
    return () => {
      registrations.delete(registration);
    };
  }, [dirty]);

  const allowLeaving = useCallback(() => {
    leaving.current = true;
  }, []);
  return { allowLeaving };
}

export interface UnsavedChangesOptions extends LeaveGuardOptions {
  /** Closes the form: the parent's `onClose`. */
  onClose: () => void;
  /** False while a dialog that stays mounted is closed: nothing is asked then. */
  open?: boolean;
}

export interface UnsavedChangesState extends LeaveGuard {
  /** Closes without asking: after a save, or when the user chose to discard the changes. */
  close: () => void;
  /**
   * Closes, asking first when there are unsaved changes: for Cancel and the close button. Does nothing while a save
   * runs.
   */
  requestClose: () => void;
  /** For a Fluent `Dialog` or `OverlayDrawer`: Esc and a click outside close through `requestClose`. */
  onOpenChange: (event: unknown, data: { open: boolean }) => void;
  /** "Discard your changes?" is showing. */
  confirming: boolean;
  /** The user chose to keep editing. */
  keepEditing: () => void;
  /** The user chose to discard the changes: closes the form. */
  discard: () => void;
}

/**
 * The guard of a form that closes (a dialog, a drawer, an editor on a page), without its question's look: closing it
 * with unsaved changes asks first, and so does leaving the screen or closing the window. Apps with their own
 * confirmation dialog (the patient portal) build on this; the staff app uses `useUnsavedChanges`.
 */
export function useUnsavedChangesState({
  dirty,
  busy = false,
  open = true,
  onClose,
  leaves,
  leaveQuestion,
}: UnsavedChangesOptions): UnsavedChangesState {
  const [confirming, setConfirming] = useState(false);
  const asking = useRef(false);
  const { allowLeaving } = useLeaveGuard({ dirty: open && dirty, busy, leaves, leaveQuestion });
  const returnFocus = useReturnFocus();

  const close = () => {
    allowLeaving();
    onClose();
  };

  const requestClose = () => {
    if (busy || confirming || asking.current) {
      return;
    }
    if (!dirty) {
      close();
      return;
    }
    // The question opens over the form. Closing it returns focus to where it was: Cancel, the close button, or the
    // field the user pressed Esc in.
    asking.current = true;
    void returnFocus().then(() => {
      asking.current = false;
      setConfirming(true);
    });
  };

  // In a dialog whose content mounts while it is open (CloseRequestScope), the dialog's Esc and click outside come here.
  const registerWithDialog = useContext(CloseRequestContext);
  const latestRequestClose = useRef(requestClose);
  useEffect(() => {
    latestRequestClose.current = requestClose;
  });
  useEffect(() => {
    if (!registerWithDialog || !open) {
      return undefined;
    }
    return registerWithDialog(() => latestRequestClose.current());
  }, [registerWithDialog, open]);

  return {
    allowLeaving,
    close,
    requestClose,
    onOpenChange: (_, data) => {
      if (!data.open) {
        requestClose();
      }
    },
    confirming,
    keepEditing: () => setConfirming(false),
    discard: () => {
      setConfirming(false);
      close();
    },
  };
}

export interface UnsavedChangesGuard extends UnsavedChangesState {
  /** "Discard your changes?": render it next to the form's dialog or drawer. */
  dialogs: ReactNode;
}

/**
 * Keeps typing from being lost in a form that closes (a dialog, a drawer, an editor): closing it with unsaved changes
 * asks "Discard your changes?" (Cancel, the close button, Esc, a click outside), and leaving the screen (a menu link,
 * Back) or closing the window asks too. Once saved, it closes without asking; while the save runs, it does not close.
 *
 * ```tsx
 * const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });
 * <Dialog open onOpenChange={guard.onOpenChange}> … <Button onClick={guard.requestClose}>Cancel</Button> … </Dialog>
 * {guard.dialogs}
 * ```
 */
export function useUnsavedChanges(options: UnsavedChangesOptions): UnsavedChangesGuard {
  const state = useUnsavedChangesState(options);
  return {
    ...state,
    dialogs: (
      <ConfirmDialog
        open={state.confirming}
        title="Discard your changes?"
        confirmLabel="Discard changes"
        cancelLabel="Keep editing"
        destructive
        onConfirm={state.discard}
        onCancel={state.keepEditing}
      >
        What you changed here has not been saved.
      </ConfirmDialog>
    ),
  };
}

export interface CloseRequests {
  /** For the dialog's or drawer's `onOpenChange`: Esc and a click outside. */
  onOpenChange: (event: unknown, data: { open: boolean }) => void;
  /** Closes through the guard of the form inside, if it has one: for a close button outside the form. */
  requestClose: () => void;
  register: RegisterCloseRequest;
}

/**
 * For a dialog or drawer whose form is a child component mounted while it is open (`{open && <Body … />}`): Esc and a
 * click outside close through the guard of the form inside (`useUnsavedChanges` in the child), which asks first when
 * there are unsaved changes; with no guard inside, they close it unless `busy`. Wrap the content in
 * `<CloseRequestScope value={…}>`.
 *
 * ```tsx
 * const closing = useCloseRequests({ onClose });
 * <Dialog open={open} onOpenChange={closing.onOpenChange}>
 *   <DialogSurface><CloseRequestScope value={closing}>{open && <Body onClose={onClose} />}</CloseRequestScope></DialogSurface>
 * </Dialog>
 * ```
 */
export function useCloseRequests({ onClose, busy = false }: { onClose: () => void; busy?: boolean }): CloseRequests {
  const inner = useRef<(() => void) | null>(null);
  const register = useCallback<RegisterCloseRequest>((requestClose) => {
    // The form's own guard comes first; one in a dialog opened from the form keeps to that dialog.
    if (inner.current) {
      return () => undefined;
    }
    inner.current = requestClose;
    return () => {
      if (inner.current === requestClose) {
        inner.current = null;
      }
    };
  }, []);
  const requestClose = () => {
    if (inner.current) {
      inner.current();
    } else if (!busy) {
      onClose();
    }
  };
  return {
    onOpenChange: (_, data) => {
      if (!data.open) {
        requestClose();
      }
    },
    requestClose,
    register,
  };
}

/** Lets the guard of the form inside answer the dialog's close requests (see `useCloseRequests`). */
export function CloseRequestScope({ value, children }: { value: CloseRequests; children: ReactNode }) {
  return <CloseRequestContext.Provider value={value.register}>{children}</CloseRequestContext.Provider>;
}

/** What the staff app asks before leaving a screen with unsaved changes. */
export const LEAVE_WITHOUT_SAVING: LeaveQuestion = {
  title: 'Leave without saving?',
  body: 'What you changed here has not been saved.',
  confirmLabel: 'Discard changes',
  destructive: true,
};

/** Asked instead by a form that keeps a draft on this computer while it is typed in (useFormDraft). */
export const LEAVE_KEEPING_DRAFT: LeaveQuestion = {
  title: 'Leave without saving?',
  body: 'It is not saved yet. A draft is kept on this computer and offered again when you come back.',
  confirmLabel: 'Leave',
  destructive: false,
};

/**
 * Whether a form's values differ from the ones it opened with, for forms that keep their fields in `useState`:
 * `useValuesChanged({ title, notes, items })`. Arrays, plain objects, dates, sets and maps compare by content; other
 * objects (files) by identity.
 *
 * A form mounted while it is open starts from the values of its first render. A dialog that stays mounted passes its
 * `open`: each time it opens, the values it shows then (after resetting them while rendering) are the starting point,
 * and while it is closed nothing counts as changed.
 */
export function useValuesChanged(values: unknown, open = true): boolean {
  const [initial, setInitial] = useState<{ values: unknown } | null>(open ? { values } : null);
  const latest = useRef(values);
  useEffect(() => {
    latest.current = values;
  });
  useEffect(() => {
    setInitial(open ? { values: latest.current } : null);
  }, [open]);
  return open && initial !== null && !sameValues(initial.values, values);
}

/** Deep equality of form values (see `useValuesChanged`). */
export function sameValues(a: unknown, b: unknown): boolean {
  if (Object.is(a, b)) {
    return true;
  }
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) {
    return false;
  }
  if (Array.isArray(a) || Array.isArray(b)) {
    return (
      Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((item, i) => sameValues(item, b[i]))
    );
  }
  if (a instanceof Date || b instanceof Date) {
    return a instanceof Date && b instanceof Date && a.getTime() === b.getTime();
  }
  if (a instanceof Set || b instanceof Set) {
    return a instanceof Set && b instanceof Set && a.size === b.size && [...a].every((item) => b.has(item));
  }
  if (a instanceof Map || b instanceof Map) {
    return (
      a instanceof Map &&
      b instanceof Map &&
      a.size === b.size &&
      [...a].every(([key, value]) => b.has(key) && sameValues(value, b.get(key)))
    );
  }
  if (!isPlainObject(a) || !isPlainObject(b)) {
    return false;
  }
  const keys = Object.keys(a);
  return (
    keys.length === Object.keys(b).length &&
    keys.every((key) => Object.hasOwn(b, key) && sameValues((a as Record<string, unknown>)[key], b[key]))
  );
}

function isPlainObject(value: object): value is Record<string, unknown> {
  const prototype: unknown = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

// Where focus goes back to. Fluent returns focus to a closing dialog's opener only when that opener is a Tabster restore
// target (useRestoreFocusTarget); otherwise Tabster picks an older target still on the page, often the button that
// opened the dialog the question opened over, behind it. So the element the user was on is made a target just before a
// question opens. Tabster records a target when it is focused, so it must be focused once it is one.

let lastFocused: WeakRef<HTMLElement> | null = null;
let focusWatchers = 0;

function rememberFocus(event: FocusEvent) {
  if (event.target instanceof HTMLElement && canReturnTo(event.target)) {
    lastFocused = new WeakRef(event.target);
  }
}

function canReturnTo(element: Element | null): element is HTMLElement {
  return (
    element instanceof HTMLElement &&
    element.isConnected &&
    element !== element.ownerDocument.body &&
    element !== element.ownerDocument.documentElement &&
    // A dialog's own surface is where focus is lost from, never a place to return it to.
    !element.matches('[role="dialog"], [role="alertdialog"]')
  );
}

const TABSTER_ATTRIBUTE = 'data-tabster';

function withRestoreTarget(value: string | null, restorer: unknown): string | null {
  if (!value) {
    return JSON.stringify({ restorer });
  }
  try {
    const parsed: unknown = JSON.parse(value);
    if (typeof parsed !== 'object' || parsed === null || 'restorer' in parsed) {
      // Already a restore target, or a restore source (a dialog of its own): leave it as it is.
      return null;
    }
    return JSON.stringify({ ...parsed, restorer });
  } catch {
    return null;
  }
}

/**
 * Returns a function to call before a question (a confirmation dialog) opens over a form; open the question once the
 * promise it returns has settled. When the question closes, focus returns to the element the user was on (the button
 * they pressed, or the field they were typing in), not to an element behind the form.
 */
export function useReturnFocus(): () => Promise<void> {
  const target = useRestoreFocusTarget();
  const attribute = target[TABSTER_ATTRIBUTE];

  useEffect(() => {
    if (focusWatchers++ === 0) {
      document.addEventListener('focusin', rememberFocus, true);
    }
    return () => {
      if (--focusWatchers === 0) {
        document.removeEventListener('focusin', rememberFocus, true);
      }
    };
  }, []);

  return useCallback(() => {
    const active = document.activeElement;
    // A click outside a dialog leaves focus on the page: the field the user was in before is the place to return to.
    const element = canReturnTo(active) ? active : lastFocused?.deref();
    if (!attribute || !element || !canReturnTo(element)) {
      return Promise.resolve();
    }
    const restorer: unknown = (JSON.parse(attribute) as { restorer?: unknown }).restorer;
    const before = element.getAttribute(TABSTER_ATTRIBUTE);
    const marked = withRestoreTarget(before, restorer);
    if (marked === null) {
      return Promise.resolve();
    }
    element.setAttribute(TABSTER_ATTRIBUTE, marked);
    // Unmarked once focus is back, so the element does not draw focus from later dialogs.
    const unmark = () => {
      if (element.getAttribute(TABSTER_ATTRIBUTE) === marked) {
        if (before === null) {
          element.removeAttribute(TABSTER_ATTRIBUTE);
        } else {
          element.setAttribute(TABSTER_ATTRIBUTE, before);
        }
      }
    };
    const hadFocus = document.activeElement === element;
    // Tabster sees the attribute in a mutation observer, a microtask queued before this one, and records the element if
    // it has focus; an element without focus is recorded when it is focused. Only then may the question take focus.
    return new Promise<void>((resolve) => {
      queueMicrotask(() => {
        if (!hadFocus && element.isConnected) {
          element.focus({ preventScroll: true });
        }
        element.addEventListener('focus', unmark, { once: true });
        resolve();
      });
    });
  }, [attribute]);
}
