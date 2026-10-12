import axe from 'axe-core';

/**
 * Accessibility checks for component and page tests (SRS UX-10), with axe-core: every rule that tests a WCAG 2.0, 2.1
 * or 2.2 success criterion at level A or AA.
 *
 * Two of those rules need a real browser, and are left out here (docs/architecture/FRONTEND-GUIDE.md, "Accessibility"):
 * - `color-contrast` reads the colours as painted and what lies under the text; jsdom paints nothing, so axe could only
 *   answer "cannot tell".
 * - `target-size` (24 px targets, WCAG 2.2) measures each control and its neighbours; in jsdom every box is 0 × 0 at the
 *   same spot, so axe would find each control covered by the next and call that a pass.
 * Rules about layout in general (a region that scrolls, text spacing) find nothing to check in jsdom, rather than a
 * problem.
 */
export const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

/** The rules above that only a browser can judge: run them there (axe-core's `color-contrast` and `target-size`). */
export const BROWSER_ONLY_RULES = ['color-contrast', 'target-size'];

/**
 * Left out of every check: the focus sentinels Tabster (Fluent's focus manager) puts at the edges of focus groups,
 * `<i tabindex="0" aria-hidden="true" data-tabster-dummy>`. They pass focus on the moment they get it, so they are
 * never a stop for anyone, but axe reports each one as focusable content hidden from screen readers.
 */
export const NOT_CHECKED = '[data-tabster-dummy]';

/**
 * What axe asks a person to review ("needs review", not a violation) and is right as it is, by the axe check that
 * could not decide. Everything else axe asks to review fails the check, as a violation does: axe reports some problems
 * only that way, such as two fields with the same id (duplicate-id-aria), or an aria-label on a plain element that also
 * has text, which screen readers never read (aria-prohibited-attr).
 */
export const REVIEWED: Record<string, { why: string; applies?: (element: Element) => boolean }> = {
  'focusable-modal-open': {
    why:
      'The page behind an open modal dialog: Fluent hides it from screen readers and its focus trap keeps Tab inside ' +
      'the dialog, which axe cannot see (aria-hidden-focus).',
  },
  'th-has-data-cells': {
    why:
      'A table without rows yet, its empty state shown under it; or one with a heading row across the whole table ' +
      'over each group of rows. axe takes a cell that spans every column for part of each column, and then finds the ' +
      'wrong column headers for the cells under it.',
    applies: (table) => !hasDataCells(table) || hasFullWidthRow(table),
  },
};

const hasDataCells = (table: Element) => table.querySelector('td, [role="cell"], [role="gridcell"]') !== null;

function hasFullWidthRow(table: Element): boolean {
  const rows = table instanceof HTMLTableElement ? [...table.rows] : [];
  const width = (row: HTMLTableRowElement) => [...row.cells].reduce((sum, cell) => sum + cell.colSpan, 0);
  const columns = Math.max(0, ...rows.map(width));
  return columns > 1 && rows.some((row) => row.cells.length === 1 && width(row) === columns);
}

export interface AxeCheckOptions {
  /**
   * Rules to leave out of this check, each with the reason why it cannot apply to what is checked. Never to let a real
   * problem through.
   */
  skip?: Record<string, string>;
}

const isReviewed = (node: axe.NodeResult) => {
  const checks = [...node.any, ...node.all, ...node.none];
  return (
    checks.length > 0 &&
    checks.every((check) => {
      const reviewed = REVIEWED[check.id];
      return reviewed !== undefined && (!reviewed.applies || (node.element && reviewed.applies(node.element)));
    })
  );
};

/**
 * The WCAG A and AA problems axe-core finds in `context` (the whole page by default): its violations, and what it asks
 * a person to review unless that is listed in REVIEWED (the rule's `nodes` then hold only the rest).
 */
export async function axeViolations(
  context: Element | Document = document.body,
  options: AxeCheckOptions = {},
): Promise<axe.Result[]> {
  const rules: Record<string, { enabled: boolean }> = {};
  for (const id of [...BROWSER_ONLY_RULES, ...Object.keys(options.skip ?? {})]) {
    rules[id] = { enabled: false };
  }
  const results = await axe.run(
    { include: context, exclude: NOT_CHECKED },
    {
      runOnly: { type: 'tag', values: WCAG_TAGS },
      rules,
      resultTypes: ['violations', 'incomplete'],
      elementRef: true,
    },
  );
  const toReview = results.incomplete
    .map((result) => ({
      ...result,
      help: `${result.help} (axe asks for review)`,
      nodes: result.nodes.filter((node) => !isReviewed(node)),
    }))
    .filter((result) => result.nodes.length > 0);
  return [...results.violations, ...toReview];
}

const shorten = (text: string, length: number) => (text.length > length ? `${text.slice(0, length - 1)}…` : text);

/** One paragraph per rule: what it asks, where it failed and what would fix each place. */
export function describeViolations(violations: axe.Result[]): string {
  return violations
    .map((violation) => {
      const nodes = violation.nodes.map((node) => {
        // One space before the attribute, not \s+: that would be tried again from every space of a long run (CodeQL
        // js/polynomial-redos).
        const html = shorten(node.html.replace(/\sclass="[^"]*"/g, ''), 240);
        const fix = (node.failureSummary ?? '').replace(/\n/g, '\n      ');
        return `  - ${node.target.join(' ')}\n    ${html}\n      ${fix}`;
      });
      return `${violation.id} (${violation.impact ?? 'no impact given'}): ${violation.help}\n  ${violation.helpUrl}\n${nodes.join('\n')}`;
    })
    .join('\n\n');
}

/**
 * Fails the test with a readable report when axe-core finds WCAG A or AA problems in `context` (the whole page by
 * default). Content Fluent renders in portals (dialogs, popovers, menus, tooltips) is outside a component's container:
 * check `document.body`, or the dialog itself.
 */
export async function expectNoAxeViolations(
  context: Element | Document = document.body,
  options: AxeCheckOptions = {},
): Promise<void> {
  const violations = await axeViolations(context, options);
  if (violations.length > 0) {
    const count = violations.reduce((sum, violation) => sum + violation.nodes.length, 0);
    const skipped = Object.entries(options.skip ?? {}).map(([id, reason]) => `\n(${id} not checked: ${reason})`);
    throw new Error(
      `axe-core found ${count} accessibility problem${count === 1 ? '' : 's'} (WCAG 2.2 A/AA):\n\n` +
        `${describeViolations(violations)}${skipped.join('')}`,
    );
  }
}
