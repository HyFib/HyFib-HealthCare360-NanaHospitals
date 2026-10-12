import { afterEach, describe, expect, it } from 'vitest';
import { axeViolations, expectNoAxeViolations } from './axe';

/** The check itself: what it fails on, and what it leaves alone. */

function page(html: string) {
  // eslint-disable-next-line no-unsanitized/property -- the tests' own fixed markup, never user input.
  document.body.innerHTML = `<main><h1>Patients</h1>${html}</main>`;
}

const ids = async () => (await axeViolations()).map((violation) => violation.id);

afterEach(() => {
  document.body.innerHTML = '';
});

describe('expectNoAxeViolations', () => {
  it('passes a page without problems', async () => {
    page('<label>Name <input></label><button type="button">Save</button>');
    await expectNoAxeViolations();
  });

  it('fails on a WCAG violation, saying where and how to fix it', async () => {
    page('<input><button type="button"><svg aria-hidden="true"></svg></button>');
    const error = await expectNoAxeViolations().catch((thrown: unknown) => thrown);
    expect(error).toBeInstanceOf(Error);
    const message = (error as Error).message;
    expect(message).toMatch(/^axe-core found 2 accessibility problems/);
    expect(message).toMatch(/button-name \(critical\)[\s\S]*<button type="button">/);
    expect(message).toMatch(/label \(critical\)[\s\S]*<input>[\s\S]*aria-label attribute does not exist/);
  });

  it('fails on what axe only asks to review: an aria-label on a plain element with text, two fields with one id', async () => {
    page(
      '<div aria-label="Low">L</div><label for="x">Systolic</label><input id="x"><input id="x" aria-label="Diastolic">',
    );
    expect(await ids()).toEqual(expect.arrayContaining(['aria-prohibited-attr', 'duplicate-id-aria']));
  });

  it("leaves out Tabster's focus sentinels", async () => {
    page('<i tabindex="0" aria-hidden="true" data-tabster-dummy=""></i><button type="button">Save</button>');
    expect(await ids()).toEqual([]);
  });

  it('accepts a table without rows yet, and one with a heading row over each group of rows', async () => {
    page(`
      <table aria-label="Patients"><thead><tr><th>Name</th><th>UHID</th></tr></thead><tbody></tbody></table>
      <table aria-label="Results">
        <thead><tr><th>Test</th><th>Result</th><th>Flag</th></tr></thead>
        <tbody>
          <tr><th colspan="3" scope="colgroup">Complete blood count</th></tr>
          <tr><td>Haemoglobin</td><td>9.1</td><td>L</td></tr>
          <tr><td>WBC</td><td>7.2</td><td>N</td></tr>
        </tbody>
      </table>`);
    expect(await ids()).toEqual([]);
  });

  it('can leave out a rule, with the reason', async () => {
    page('<input>');
    await expectNoAxeViolations(document.body, { skip: { label: 'a test of the option' } });
  });
});
