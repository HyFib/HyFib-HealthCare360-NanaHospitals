import { act, useState } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  askToLeaveWith,
  confirmLeaving,
  LEAVE_KEEPING_DRAFT,
  leavesDialog,
  leaveQuestionFor,
  sameValues,
  useLeaveGuard,
  useValuesChanged,
  wouldLoseChanges,
  type LeaveGuardOptions,
  type LeaveQuestion,
} from './unsavedChanges';

// The bookkeeping behind the unsaved-change guards (SRS UX-08). The dialogs and the router's blocker are tested in the
// staff app (src/test/unsavedChanges.test.tsx), with Fluent's real dialogs.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

let container: HTMLDivElement;
let root: Root;

beforeEach(() => {
  container = document.createElement('div');
  document.body.append(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
});

const screens = (from: string, to: string) => ({ current: { pathname: from }, next: { pathname: to } });

describe('sameValues', () => {
  it('compares form values by content', () => {
    expect(sameValues('a', 'a')).toBe(true);
    expect(sameValues('a', 'b')).toBe(false);
    expect(sameValues(null, undefined)).toBe(false);
    expect(sameValues({ name: 'Ravi', tags: ['a', 'b'] }, { name: 'Ravi', tags: ['a', 'b'] })).toBe(true);
    expect(sameValues({ name: 'Ravi', tags: ['a', 'b'] }, { name: 'Ravi', tags: ['b', 'a'] })).toBe(false);
    expect(sameValues({ a: 1 }, { a: 1, b: undefined })).toBe(false);
    expect(sameValues(new Date(5), new Date(5))).toBe(true);
    expect(sameValues(new Set(['x']), new Set(['x']))).toBe(true);
    expect(sameValues(new Map([['k', { v: 1 }]]), new Map([['k', { v: 1 }]]))).toBe(true);
    expect(sameValues(new Map([['k', { v: 1 }]]), new Map([['k', { v: 2 }]]))).toBe(false);
  });

  it('compares files and other objects by identity', () => {
    const file = new File(['x'], 'scan.pdf');
    expect(sameValues({ file }, { file })).toBe(true);
    expect(sameValues({ file }, { file: new File(['x'], 'scan.pdf') })).toBe(false);
  });
});

function Form(props: LeaveGuardOptions & { onGuard?: (guard: ReturnType<typeof useLeaveGuard>) => void }) {
  const guard = useLeaveGuard(props);
  props.onGuard?.(guard);
  return null;
}

describe('useLeaveGuard', () => {
  it('counts a form as unsaved while it is dirty and mounted', () => {
    act(() => root.render(<Form dirty={false} />));
    expect(wouldLoseChanges()).toBe(false);

    act(() => root.render(<Form dirty />));
    expect(wouldLoseChanges()).toBe(true);
    expect(wouldLoseChanges(screens('/patients/register', '/patients'))).toBe(true);

    act(() => root.render(<></>));
    expect(wouldLoseChanges()).toBe(false);
  });

  it('keeps the form when only the search parameters change, unless told otherwise', () => {
    const sameScreen = {
      current: { pathname: '/billing', search: { page: 1 } },
      next: { pathname: '/billing', search: {} },
    };
    act(() => root.render(<Form dirty />));
    expect(wouldLoseChanges(sameScreen)).toBe(false);

    act(() => root.render(<Form dirty leaves={() => true} />));
    expect(wouldLoseChanges(sameScreen)).toBe(true);
  });

  it("loses a dialog's form on Back and Forward too, which close a dialog opened through the address", () => {
    const list = { pathname: '/inventory/transfers', search: { send: true } };
    const closed = { pathname: '/inventory/transfers', search: {} };
    expect(leavesDialog(list, closed, 'BACK')).toBe(true);
    expect(leavesDialog(list, closed, 'FORWARD')).toBe(true);
    expect(leavesDialog(list, closed, 'PUSH')).toBe(false);
    expect(leavesDialog(list, { pathname: '/inventory/items' }, 'PUSH')).toBe(true);

    act(() => root.render(<Form dirty leaves={leavesDialog} />));
    expect(wouldLoseChanges({ current: list, next: closed, action: 'BACK' })).toBe(true);
    expect(wouldLoseChanges({ current: list, next: closed, action: 'REPLACE' })).toBe(false);
  });

  it('lets a save move on while it runs, but still has the browser ask before the window closes', () => {
    act(() => root.render(<Form dirty busy />));
    expect(wouldLoseChanges(screens('/ot/book', '/ot/bookings/1'))).toBe(false);
    expect(wouldLoseChanges()).toBe(true);
  });

  it('stops asking once leaving is allowed, until the form changes again', () => {
    let guard: ReturnType<typeof useLeaveGuard> | undefined;
    act(() => root.render(<Form dirty onGuard={(g) => (guard = g)} />));
    act(() => guard?.allowLeaving());
    expect(wouldLoseChanges()).toBe(false);

    act(() => root.render(<Form dirty={false} />));
    act(() => root.render(<Form dirty />));
    expect(wouldLoseChanges()).toBe(true);
  });
});

const DESTRUCTIVE: LeaveQuestion = { title: 'Leave?', body: 'Lost.', confirmLabel: 'Discard', destructive: true };

describe('leaveQuestionFor', () => {
  it('asks the app question unless every form leaving would lose asks its own', () => {
    act(() =>
      root.render(
        <>
          <Form dirty leaveQuestion={LEAVE_KEEPING_DRAFT} />
          <Form dirty />
        </>,
      ),
    );
    expect(leaveQuestionFor()).toBeUndefined();
  });

  it('prefers a question that says the changes are lost over one that says a draft is kept', () => {
    act(() =>
      root.render(
        <>
          <Form dirty leaveQuestion={DESTRUCTIVE} />
          <Form dirty leaveQuestion={LEAVE_KEEPING_DRAFT} />
        </>,
      ),
    );
    expect(leaveQuestionFor()).toBe(DESTRUCTIVE);

    act(() => root.render(<Form dirty leaveQuestion={LEAVE_KEEPING_DRAFT} />));
    expect(leaveQuestionFor()).toBe(LEAVE_KEEPING_DRAFT);
  });
});

describe('confirmLeaving', () => {
  it('goes on without asking when nothing would be lost', async () => {
    let asked = 0;
    const stop = askToLeaveWith(() => {
      asked++;
      return Promise.resolve(false);
    });
    try {
      act(() => root.render(<Form dirty={false} />));
      await expect(confirmLeaving()).resolves.toBe(true);
      expect(asked).toBe(0);
    } finally {
      stop();
    }
  });

  it('asks, stays on "Stay", and lets the navigations that follow "Leave" go', async () => {
    let answer = false;
    const stop = askToLeaveWith(() => Promise.resolve(answer));
    try {
      act(() => root.render(<Form dirty />));
      await expect(confirmLeaving()).resolves.toBe(false);
      expect(wouldLoseChanges()).toBe(true);

      answer = true;
      await expect(confirmLeaving()).resolves.toBe(true);
      expect(wouldLoseChanges()).toBe(false);
      expect(wouldLoseChanges(screens('/emr', '/login'))).toBe(false);
    } finally {
      stop();
    }
  });
});

function Values({
  open,
  onChanged,
}: {
  open?: boolean;
  onChanged: (changed: boolean, set: (v: string) => void) => void;
}) {
  const [value, setValue] = useState('');
  // A dialog that stays mounted resets its fields while rendering when it opens again.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setValue('');
    }
  }
  onChanged(useValuesChanged({ value }, open), setValue);
  return null;
}

describe('useValuesChanged', () => {
  it('tells whether the values differ from those the form opened with', () => {
    let changed = false;
    let set: (v: string) => void = () => undefined;
    const track = (c: boolean, s: (v: string) => void) => {
      changed = c;
      set = s;
    };
    act(() => root.render(<Values onChanged={track} />));
    expect(changed).toBe(false);

    act(() => set('Allergic to latex'));
    expect(changed).toBe(true);

    act(() => set(''));
    expect(changed).toBe(false);
  });

  it('starts again each time a dialog that stays mounted opens, and counts nothing while it is closed', () => {
    let changed = false;
    let set: (v: string) => void = () => undefined;
    const track = (c: boolean, s: (v: string) => void) => {
      changed = c;
      set = s;
    };
    act(() => root.render(<Values open onChanged={track} />));
    act(() => set('First note'));
    expect(changed).toBe(true);

    act(() => root.render(<Values open={false} onChanged={track} />));
    expect(changed).toBe(false);

    act(() => root.render(<Values open onChanged={track} />));
    expect(changed).toBe(false);
    act(() => set('Second note'));
    expect(changed).toBe(true);
  });
});
