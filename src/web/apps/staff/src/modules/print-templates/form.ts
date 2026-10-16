import { MAX_CLOSING_NOTE_LENGTH, MAX_PRINT_SPACE_MM, type PrintTemplate } from '@hc360/core';
import { z } from 'zod';
import type { PrintTemplateItem, PrintTemplateList, SavePrintTemplateRequest } from './api';

/** Whose template the editor changes: every facility's, or only the signed-in user's facility's. */
export type TemplateScope = 'hospital' | 'facility';

const spaceMessage = `Enter between 0 and ${MAX_PRINT_SPACE_MM} mm.`;
const space = z
  .number()
  .int('Enter whole millimetres.')
  .min(0, spaceMessage)
  .max(MAX_PRINT_SPACE_MM, spaceMessage)
  .nullable();

/** The server's rules (`PrintTemplateRules`), on the fields the server names. */
export const printTemplateFormSchema = z.object({
  scope: z.enum(['hospital', 'facility']),
  letterhead: z.enum(['Printed', 'Preprinted']),
  topSpaceMm: space,
  bottomSpaceMm: space,
  showLogo: z.boolean(),
  closingNote: z
    .string()
    .refine(
      (note) => note.trim().length <= MAX_CLOSING_NOTE_LENGTH,
      `Keep the closing note to ${MAX_CLOSING_NOTE_LENGTH} characters.`,
    )
    .refine((note) => !hasControlCharacters(note), 'The closing note can hold text and line breaks only.'),
  paper: z.enum(['A4', 'A5']).nullable(),
});

export type PrintTemplateForm = z.infer<typeof printTemplateFormSchema>;

/** Characters that are not text (a tab, a bell), as .NET's `char.IsControl` sees them; line breaks are allowed. */
function hasControlCharacters(text: string): boolean {
  for (let index = 0; index < text.length; index++) {
    const code = text.charCodeAt(index);
    if ((code < 0x20 && code !== 0x0a && code !== 0x0d) || (code >= 0x7f && code <= 0x9f)) {
      return true;
    }
  }
  return false;
}

/** Whether the editor offers the choice between hospital-wide and this facility's own template. */
export function offersScope(item: PrintTemplateItem, list: PrintTemplateList): boolean {
  return list.facilityId !== null && (list.facilityCount > 1 || item.facility !== null);
}

/** The editor opens on the facility's own template when it has one, else on the hospital-wide one. */
export function initialScope(item: PrintTemplateItem, list: PrintTemplateList): TemplateScope {
  return item.facility !== null && list.facilityId !== null ? 'facility' : 'hospital';
}

/** The template the scope prints with now: a facility without its own prints with the hospital-wide one. */
export function toForm(item: PrintTemplateItem, scope: TemplateScope): PrintTemplateForm {
  const template = scope === 'facility' ? (item.facility ?? item.hospitalWide) : item.hospitalWide;
  return {
    scope,
    letterhead: item.allowsPreprinted ? template.letterhead : 'Printed',
    topSpaceMm: template.topSpaceMm,
    bottomSpaceMm: template.bottomSpaceMm,
    showLogo: template.showLogo,
    closingNote: template.closingNote ?? '',
    paper: item.papers.length > 0 ? (template.paper ?? item.papers[0]!) : null,
  };
}

/** The template the form describes, as documents use it: blank space only on pre-printed paper, the note trimmed. */
export function toTemplate(values: PrintTemplateForm, item: PrintTemplateItem): PrintTemplate {
  const preprinted = item.allowsPreprinted && values.letterhead === 'Preprinted';
  return {
    letterhead: preprinted ? 'Preprinted' : 'Printed',
    topSpaceMm: preprinted ? (values.topSpaceMm ?? 0) : 0,
    bottomSpaceMm: preprinted ? (values.bottomSpaceMm ?? 0) : 0,
    showLogo: values.showLogo,
    closingNote: values.closingNote.trim() || null,
    paper: values.paper !== null && item.papers.includes(values.paper) ? values.paper : null,
  };
}

export function toSaveRequest(values: PrintTemplateForm, item: PrintTemplateItem): SavePrintTemplateRequest {
  const forThisFacility = values.scope === 'facility';
  // The version of the template this one replaces, so a save over someone else's newer one is refused (REL-04).
  const version = forThisFacility ? item.facilityVersion : item.hospitalWideVersion;
  return { template: toTemplate(values, item), forThisFacility, ...(version === null ? {} : { version }) };
}
