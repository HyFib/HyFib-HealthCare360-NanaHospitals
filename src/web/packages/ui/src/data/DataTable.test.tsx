import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DataTable, type Column } from './DataTable';

interface Row {
  id: string;
  name: string;
  /** Natural widths of the row's buttons, in px. */
  buttons: number[];
}

const columns: Column<Row>[] = [{ key: 'name', header: 'Name', render: (row) => row.name }];

// jsdom does no layout: the fake gives each row's buttons their natural width, but only while they are measured at
// their natural width (max-content), and the cells their padding.
function fakeLayout() {
  const real = window.getComputedStyle.bind(window);
  return vi.spyOn(window, 'getComputedStyle').mockImplementation((element, pseudo) => {
    if (element instanceof HTMLElement && element.matches('[data-row-actions]')) {
      const natural = [...element.querySelectorAll('button')].reduce((sum, b) => sum + Number(b.dataset.width), 0);
      return { width: element.style.width === 'max-content' ? `${natural}px` : '999px' } as CSSStyleDeclaration;
    }
    if (element instanceof HTMLTableCellElement) {
      return { paddingLeft: '8px', paddingRight: '8px' } as CSSStyleDeclaration;
    }
    return real(element, pseudo);
  });
}

describe('DataTable row actions', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
    container = document.createElement('div');
    document.body.append(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.restoreAllMocks();
  });

  const render = (rows: Row[]) =>
    act(() =>
      root.render(
        <DataTable
          ariaLabel="Patients"
          columns={columns}
          rows={rows}
          getRowId={(row) => row.id}
          rowActions={(row) =>
            row.buttons.map((width, index) => (
              <button key={index} type="button" data-width={width}>
                Action {index + 1}
              </button>
            ))
          }
        />,
      ),
    );

  const actionsHeader = () => container.querySelector<HTMLTableCellElement>('thead th[aria-label="Actions"]');

  it('makes the actions column as wide as the widest row of buttons, with the cell padding', () => {
    fakeLayout();

    render([
      { id: '1', name: 'Asha Devi', buttons: [60] },
      { id: '2', name: 'Ravi Shankar', buttons: [64.2, 82] },
    ]);

    expect(actionsHeader()?.style.width).toBe('163px');
    expect(actionsHeader()?.style.boxSizing).toBe('border-box');
    // Measured, the buttons go back to filling their cell.
    expect(container.querySelector<HTMLElement>('[data-row-actions]')?.style.width).toBe('');
  });

  it('narrows the column again when the widest buttons go', () => {
    fakeLayout();
    render([
      { id: '1', name: 'Asha Devi', buttons: [60] },
      { id: '2', name: 'Ravi Shankar', buttons: [64.2, 82] },
    ]);

    render([{ id: '1', name: 'Asha Devi', buttons: [60] }]);

    expect(actionsHeader()?.style.width).toBe('76px');
  });

  it('leaves the width alone when there is nothing to measure', () => {
    render([{ id: '1', name: 'Asha Devi', buttons: [60] }]);

    // Without layout every width is 0, as for a table that is not on screen.
    expect(actionsHeader()?.style.width).toBe('');
  });
});

describe('DataTable rows that open', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
    container = document.createElement('div');
    document.body.append(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  const pressEnter = (element: Element) => {
    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    act(() => {
      element.dispatchEvent(event);
    });
    return event;
  };

  it('opens a row on Enter only when the row itself has focus, not a button in it', () => {
    const onRowClick = vi.fn();
    act(() =>
      root.render(
        <DataTable
          ariaLabel="Patients"
          columns={columns}
          rows={[{ id: '1', name: 'Asha Devi', buttons: [] }]}
          getRowId={(row) => row.id}
          onRowClick={onRowClick}
          rowActions={() => <button type="button">More actions</button>}
        />,
      ),
    );
    const row = container.querySelector('tbody tr')!;

    // The button's own Enter (which opens its menu) still happens.
    expect(pressEnter(row.querySelector('button')!).defaultPrevented).toBe(false);
    expect(onRowClick).not.toHaveBeenCalled();

    pressEnter(row);
    expect(onRowClick).toHaveBeenCalledWith({ id: '1', name: 'Asha Devi', buttons: [] });
  });
});

describe('DataTable width', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
    container = document.createElement('div');
    document.body.append(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  it('keeps a wide table in its own scroll box instead of widening the page around it', () => {
    act(() =>
      root.render(
        <DataTable
          ariaLabel="Backups"
          columns={[
            { key: 'started', header: 'Started', width: 900, render: (row: Row) => row.name },
            { key: 'file', header: 'File', width: 900, render: (row: Row) => row.name },
          ]}
          rows={[{ id: '1', name: 'Asha Devi', buttons: [] }]}
          getRowId={(row) => row.id}
        />,
      ),
    );

    // jsdom does no layout, so the rule that does it is checked. With overflow alone, the table's minimum width still
    // widens the section, the page and the window frame; a grid column that may shrink to nothing keeps it in the box.
    const box = getComputedStyle(container.querySelector('table')!.parentElement!);
    expect(box.overflowX).toBe('auto');
    expect(box.display).toBe('grid');
    expect(box.gridTemplateColumns).toBe('minmax(0, 1fr)');
  });
});
