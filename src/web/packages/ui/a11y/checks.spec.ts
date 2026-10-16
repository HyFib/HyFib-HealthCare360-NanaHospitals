import { expect, test, type Page } from '@playwright/test';
import { browserAxeViolations, focusProblems } from '../src/testing/browser';

// The browser checks themselves (src/testing/browser.ts): what they fail on and what they leave alone, on small pages
// of their own. The apps' screens are checked with them in apps/*/a11y.

const page = (body: string) =>
  `<!doctype html><html lang="en"><head><title>Check</title></head><body>${body}</body></html>`;

async function show(target: Page, body: string) {
  await target.setContent(page(`<main>${body}</main>`));
}

const ids = async (target: Page) => (await browserAxeViolations(target)).map((violation) => violation.id);

test.describe('axe-core in the browser', () => {
  test('passes a page without problems', async ({ page: target }) => {
    await show(target, '<h1>Patients</h1><label>Name <input></label><button type="button">Save</button>');
    expect(await browserAxeViolations(target)).toEqual([]);
  });

  test('fails on text without enough contrast', async ({ page: target }) => {
    await show(target, '<h1>Patients</h1><p style="color: #999; background: #fff">Fever for three days</p>');
    expect(await ids(target)).toContain('color-contrast');
  });

  test('fails on targets smaller than 24 px with others close by (WCAG 2.2)', async ({ page: target }) => {
    const button = (name: string) =>
      `<button type="button" aria-label="${name}" style="width: 16px; height: 16px; padding: 0; margin: 0"></button>`;
    await show(target, `<h1>Patients</h1><div style="display: flex">${button('Edit')}${button('Delete')}</div>`);
    expect(await ids(target)).toContain('target-size');
  });

  test('measures text on a gradient against each of its colour stops', async ({ page: target }) => {
    const panel = (from: string, to: string) =>
      `<div style="background: linear-gradient(145deg, ${from}, ${to}); color: #fff; padding: 16px">` +
      '<p>One connected system for the whole hospital</p></div>';
    await show(target, `<h1>Sign in</h1>${panel('#064F6B', '#016D95')}`);
    expect(await browserAxeViolations(target)).toEqual([]);

    await show(target, `<h1>Sign in</h1>${panel('#064F6B', '#4A9AC0')}`);
    const [violation] = await browserAxeViolations(target);
    expect(violation?.id).toBe('color-contrast');
    expect(violation?.nodes[0]?.summary).toMatch(/on the gradient/);
  });
});

test.describe('the focus walk', () => {
  test('passes focus the browser or the page draws', async ({ page: target }) => {
    await show(
      target,
      '<style>.ring { position: relative; outline: none } .ring:focus-visible::after { content: ""; position: absolute;' +
        ' inset: -2px; border: 2px solid #000 }</style>' +
        '<button type="button">Browser ring</button><button type="button" class="ring">Ring on ::after</button>' +
        '<a href="#x" style="outline: none; box-shadow: none">x</a>',
    );
    const { stops, problems } = await focusProblems(target);
    expect(stops).toBe(3);
    // The link draws nothing on focus.
    expect(problems).toEqual([{ element: '<a> "x"', problem: 'no visible focus indicator' }]);
  });

  test('finds focus hidden under a bar stuck to the bottom of the window', async ({ page: target }) => {
    await target.setViewportSize({ width: 800, height: 400 });
    await show(
      target,
      '<div style="height: 360px"></div><button type="button">Save</button>' +
        '<div style="position: fixed; bottom: 0; left: 0; right: 0; height: 120px; background: #eee"></div>',
    );
    const { problems } = await focusProblems(target);
    expect(problems).toEqual([{ element: '<button> "Save"', problem: 'focused but covered by something else' }]);
  });

  test('starts from the main content through the skip link', async ({ page: target }) => {
    await target.setContent(
      page(
        '<a href="#main">Skip to main content</a><nav><a href="#a">A</a><a href="#b">B</a></nav>' +
          '<main id="main" tabindex="-1"><button type="button">Save</button></main>',
      ),
    );
    const { stops, problems } = await focusProblems(target, { from: 'skip-link' });
    // The skip link, then Save: the navigation is skipped.
    expect(stops).toBe(2);
    expect(problems).toEqual([]);
  });

  test('refuses to start from the main content without a skip link', async ({ page: target }) => {
    await show(target, '<button type="button">Save</button>');
    await expect(focusProblems(target, { from: 'skip-link' })).rejects.toThrow(/Skip to main content/);
  });
});
