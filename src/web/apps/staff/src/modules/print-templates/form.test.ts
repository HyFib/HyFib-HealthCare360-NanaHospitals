import { DEFAULT_PRINT_TEMPLATE, type PrintTemplate } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import { templateSummary, type PrintTemplateItem, type PrintTemplateList } from './api';
import {
  initialScope,
  offersScope,
  printTemplateFormSchema,
  toForm,
  toSaveRequest,
  toTemplate,
  type PrintTemplateForm,
} from './form';

const stationery: PrintTemplate = {
  letterhead: 'Preprinted',
  topSpaceMm: 38,
  bottomSpaceMm: 12,
  showLogo: false,
  closingNote: 'Subject to Pune jurisdiction.',
  paper: null,
};

function item(overrides: Partial<PrintTemplateItem> = {}): PrintTemplateItem {
  return {
    key: 'prescription',
    name: 'Prescriptions',
    area: 'Clinical',
    covers: 'Prescriptions, on A5 or A4.',
    papers: ['A5', 'A4'],
    allowsPreprinted: true,
    hospitalWide: DEFAULT_PRINT_TEMPLATE,
    facility: null,
    hospitalWideVersion: null,
    facilityVersion: null,
    ...overrides,
  };
}

function list(overrides: Partial<PrintTemplateList> = {}): PrintTemplateList {
  return { facilityId: 'f1', facilityName: 'Nana Hospitals, Baner', facilityCount: 2, templates: [], ...overrides };
}

const form = (overrides: Partial<PrintTemplateForm> = {}): PrintTemplateForm => ({
  ...toForm(item(), 'hospital'),
  ...overrides,
});

const problems = (values: PrintTemplateForm) => {
  const result = printTemplateFormSchema.safeParse(values);
  return result.success ? {} : Object.fromEntries(result.error.issues.map((i) => [i.path.join('.'), i.message]));
};

describe('the print template editor', () => {
  it('opens on the template the facility prints with: its own when it has one', () => {
    const own = item({ facility: stationery });

    expect(initialScope(own, list())).toBe('facility');
    expect(toForm(own, 'facility')).toMatchObject({ letterhead: 'Preprinted', topSpaceMm: 38, paper: 'A5' });
    expect(toForm(own, 'hospital')).toMatchObject({ letterhead: 'Printed', closingNote: '', showLogo: true });
    expect(initialScope(item(), list())).toBe('hospital');
    expect(toForm(item(), 'facility')).toEqual({ ...toForm(item(), 'hospital'), scope: 'facility' });
  });

  it('offers the choice of hospital-wide or this facility only where there is one', () => {
    expect(offersScope(item(), list())).toBe(true);
    expect(offersScope(item(), list({ facilityCount: 1 }))).toBe(false);
    expect(offersScope(item({ facility: stationery }), list({ facilityCount: 1 }))).toBe(true);
    expect(offersScope(item(), list({ facilityId: null }))).toBe(false);
  });

  it('keeps to the rules the server keeps', () => {
    expect(problems(form())).toEqual({});
    expect(problems(form({ topSpaceMm: 81, bottomSpaceMm: -1 }))).toEqual({
      topSpaceMm: 'Enter between 0 and 80 mm.',
      bottomSpaceMm: 'Enter between 0 and 80 mm.',
    });
    expect(problems(form({ topSpaceMm: 12.5 }))).toEqual({ topSpaceMm: 'Enter whole millimetres.' });
    expect(problems(form({ closingNote: `  ${'x'.repeat(500)}  ` }))).toEqual({});
    expect(problems(form({ closingNote: 'x'.repeat(501) }))).toEqual({
      closingNote: 'Keep the closing note to 500 characters.',
    });
    expect(problems(form({ closingNote: 'Line one\r\nLine two' }))).toEqual({});
    expect(problems(form({ closingNote: 'Tab\there' }))).toEqual({
      closingNote: 'The closing note can hold text and line breaks only.',
    });
  });

  it('saves the template as documents use it', () => {
    expect(
      toSaveRequest(
        form({
          scope: 'facility',
          letterhead: 'Preprinted',
          topSpaceMm: 40,
          bottomSpaceMm: null,
          closingNote: '  Thank you.\n',
          paper: 'A4',
        }),
        item(),
      ),
    ).toEqual({
      template: {
        letterhead: 'Preprinted',
        topSpaceMm: 40,
        bottomSpaceMm: 0,
        showLogo: true,
        closingNote: 'Thank you.',
        paper: 'A4',
      },
      forThisFacility: true,
    });
    expect(toTemplate(form({ letterhead: 'Printed', topSpaceMm: 40, closingNote: '   ' }), item())).toMatchObject({
      topSpaceMm: 0,
      closingNote: null,
    });
    const roll = item({ key: 'sales-invoice', papers: [], allowsPreprinted: false });
    expect(toTemplate(form({ letterhead: 'Preprinted', topSpaceMm: 40, paper: 'A4' }), roll)).toMatchObject({
      letterhead: 'Printed',
      topSpaceMm: 0,
      paper: null,
    });
  });

  it('sends back the version of the template it replaces, so a newer save is not overwritten', () => {
    const saved = item({ facility: stationery, hospitalWideVersion: 41, facilityVersion: 57 });

    expect(toSaveRequest(form({ scope: 'facility' }), saved).version).toBe(57);
    expect(toSaveRequest(form({ scope: 'hospital' }), saved).version).toBe(41);
    expect(toSaveRequest(form({ scope: 'facility' }), item())).not.toHaveProperty('version');
  });

  it('says in a few words how each kind of document prints', () => {
    expect(templateSummary(DEFAULT_PRINT_TEMPLATE, item())).toBe('Letterhead with the logo · A5');
    expect(templateSummary(stationery, item({ papers: [] }))).toBe(
      'Pre-printed stationery (38 mm at the top, 12 mm at the foot) · closing note',
    );
    expect(templateSummary({ ...DEFAULT_PRINT_TEMPLATE, showLogo: false, paper: 'A4' }, item())).toBe(
      'Letterhead without the logo · A4',
    );
  });
});
