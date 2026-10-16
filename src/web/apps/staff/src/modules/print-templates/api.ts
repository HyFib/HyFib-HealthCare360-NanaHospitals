import type { PrintTemplate, PrintTemplateKey } from '@hc360/core';
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Print templates (Hospital setup, SRS HMS-04): how each kind of document prints. The letterhead or the hospital's own
 * pre-printed stationery, the logo, a closing note, and the paper where there is a choice, hospital-wide or for one
 * facility. Mirrors `PrintTemplateEndpoints` (`/api/v1/admin/print-templates`, permission `admin.print.manage`). The
 * layout of each document stays as it is: a template cannot add markup or styles.
 */

export const PRINT_TEMPLATES_PERMISSION = 'admin.print.manage';

export type Paper = 'A4' | 'A5';

/** One kind of document, with the template it prints with hospital-wide and at the signed-in user's facility. */
export interface PrintTemplateItem {
  key: PrintTemplateKey;
  /** What staff call these documents: "Bills", "Reports". */
  name: string;
  /** The part of HC360 that prints them, as its menu names it. */
  area: string;
  /** Which documents follow the template, in a sentence. */
  covers: string;
  /** The papers to choose from, the usual one first; empty when the paper is fixed. */
  papers: Paper[];
  /** False for documents printed only on 80 mm rolls, which are never pre-printed. */
  allowsPreprinted: boolean;
  /** What every facility prints with unless it has its own: the defaults until one is saved. */
  hospitalWide: PrintTemplate;
  /** The facility's own template; null when it prints with the hospital-wide one. */
  facility: PrintTemplate | null;
  /** The saved hospital-wide template's version; null while it is the defaults. */
  hospitalWideVersion: number | null;
  /** The facility's own template's version; null when it has none. */
  facilityVersion: number | null;
}

export interface PrintTemplateList {
  facilityId: string | null;
  facilityName: string | null;
  /** With one facility there is no choice between hospital-wide and the facility's own. */
  facilityCount: number;
  templates: PrintTemplateItem[];
}

export interface SavePrintTemplateRequest {
  template: PrintTemplate;
  /** True: the signed-in user's facility's own template. False: the hospital-wide one. */
  forThisFacility: boolean;
  /** The version of the template being replaced, when one was saved: 409 if someone saved it since (REL-04). */
  version?: number;
}

const BASE = '/api/v1/admin/print-templates';

export const printTemplateKeys = { all: ['admin', 'print-templates'] as const };

export const printTemplateQueries = {
  list: () =>
    queryOptions({
      queryKey: printTemplateKeys.all,
      queryFn: ({ signal }) => api.get<PrintTemplateList>(BASE, { signal }),
    }),
};

export const printTemplatesApi = {
  save: (key: string, request: SavePrintTemplateRequest) =>
    api.put<void>(`${BASE}/${encodeURIComponent(key)}`, request),
  /** Removes the facility's own template: it prints with the hospital-wide one again. */
  removeFacilityTemplate: (key: string) => api.delete<void>(`${BASE}/${encodeURIComponent(key)}/facility`),
};

/** The template the item prints with at the signed-in user's facility. */
export function effectiveTemplate(item: PrintTemplateItem): PrintTemplate {
  return item.facility ?? item.hospitalWide;
}

/** How a template prints, in a few words: "Letterhead with the logo · A5 · closing note". */
export function templateSummary(template: PrintTemplate, item: Pick<PrintTemplateItem, 'papers'>): string {
  const parts = [
    template.letterhead === 'Preprinted'
      ? `Pre-printed stationery (${spaces(template)})`
      : template.showLogo
        ? 'Letterhead with the logo'
        : 'Letterhead without the logo',
  ];
  if (item.papers.length > 0) {
    parts.push(template.paper ?? item.papers[0]!);
  }
  if (template.closingNote) {
    parts.push('closing note');
  }
  return parts.join(' · ');
}

function spaces(template: PrintTemplate): string {
  const top = `${template.topSpaceMm} mm at the top`;
  return template.bottomSpaceMm > 0 ? `${top}, ${template.bottomSpaceMm} mm at the foot` : top;
}
