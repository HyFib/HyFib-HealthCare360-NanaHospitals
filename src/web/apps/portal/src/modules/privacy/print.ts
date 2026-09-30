import { escapeHtml } from '@hc360/core';
import type { Translate } from '@/i18n';
import { istDateOf } from '@/i18n/format';
import type { Formatters } from '@/i18n/useFormat';
import { safeFileName, type PrintableDocument } from '@/lib/print';
import type { DataSummary, SummaryRecipient } from '@/lib/types';
import {
  categoryView,
  consentView,
  contactTitle,
  formatPhone,
  personRows,
  splitCategories,
  type CategoryView,
} from './model';

export interface SummaryPrintContext {
  t: Translate;
  f: Formatters;
  printedAt: string;
}

const STYLES = `
.head { display: flex; justify-content: space-between; gap: 12px; border-bottom: 2px solid #0A7CA7; padding-bottom: 6px; margin-bottom: 8px; }
.facility { font-size: 14pt; font-weight: 700; color: #045E80; }
.patient { display: flex; flex-wrap: wrap; gap: 4px 18px; margin: 6px 0 10px; }
.section { break-inside: avoid; margin-bottom: 6px; }
.section p { margin: 2px 0 6px; }
.facts th { width: 34%; }
.footer { margin-top: 16px; font-size: 8.5pt; color: #555; border-top: 1px solid #ccc; padding-top: 4px; }
`;

type Row = readonly (string | null | undefined)[];

/**
 * The patient's copy of the summary of their personal data (DPDP Act s11), in their language. Every value, the
 * server's included, goes through `escapeHtml`.
 */
export function dataSummaryDocument(summary: DataSummary, ctx: SummaryPrintContext): PrintableDocument {
  const { t, f } = ctx;
  const e = (value: string | null | undefined) => escapeHtml(value ?? '');
  const row = (cells: Row, labelled: boolean) =>
    `<tr>${cells.map((cell, index) => (labelled && index === 0 ? `<th>${e(cell)}</th>` : `<td>${e(cell)}</td>`)).join('')}</tr>`;
  /** Label and value rows: "Why we keep it | To book and run your visits." */
  const labelled = (rows: readonly Row[]) =>
    rows.length ? `<table class="facts">${rows.map((cells) => row(cells, true)).join('')}</table>` : '';
  /** One record, consent or organisation per row. */
  const listed = (rows: readonly Row[]) =>
    rows.length ? `<table>${rows.map((cells) => row(cells, false)).join('')}</table>` : '';
  const paragraph = (text: string) => `<p>${e(text)}</p>`;
  const section = (title: string, content: string) => `<div class="section"><h2>${e(title)}</h2>${content}</div>`;
  const facts = (view: CategoryView) =>
    labelled([
      [t('myPrivacy.data.purpose'), view.purpose],
      [t('myPrivacy.data.basis'), view.basis],
      [t('myPrivacy.data.retention'), view.retention],
    ]);
  const recipients = (
    list: readonly SummaryRecipient[],
    dataKey: 'myPrivacy.data.recipient.data' | 'myPrivacy.data.processor.data',
    none: string,
  ) =>
    list.length
      ? listed(list.map((recipient) => [recipient.name, recipient.role, t(dataKey, { data: recipient.data })]))
      : paragraph(none);

  const { registration, others } = splitCategories(summary.categories);
  const registrationView = registration ? categoryView(registration, summary.periods, t, f) : null;
  const person = section(
    registrationView?.title ?? t('myPrivacy.category.registration.title'),
    labelled(personRows(summary.person, t, f).map((detail) => [detail.label, detail.value])) +
      (registrationView ? facts(registrationView) : ''),
  );

  const categories = others
    .map((category) => {
      const view = categoryView(category, summary.periods, t, f);
      const items = listed(view.items.map((item) => [item.label, item.count, item.dates]));
      return section(view.title, paragraph(view.description) + items + facts(view));
    })
    .join('\n');

  const consents = section(
    t('myPrivacy.data.consents'),
    summary.consents.length
      ? listed(
          summary.consents.map((consent) => {
            const view = consentView(consent, t, f);
            return [view.label, t(view.state), view.detail];
          }),
        )
      : paragraph(t('myPrivacy.data.consents.none')),
  );

  const nominee = section(
    t('myPrivacy.hub.nominee'),
    paragraph(
      summary.nominee
        ? t('myPrivacy.hub.nominee.named', { name: summary.nominee.name, relationship: summary.nominee.relationship })
        : t('myPrivacy.hub.nominee.none'),
    ),
  );

  const contact = summary.contact;
  const contactSection = section(
    t('myPrivacy.contact.title'),
    labelled(
      [
        [contactTitle(contact.title, t), contact.name],
        [t('myPrivacy.contact.email'), contact.email],
        [t('myPrivacy.contact.phone'), formatPhone(contact.phone)],
        [t('myPrivacy.contact.address'), contact.address],
      ].filter((cells) => cells[1]?.trim()),
    ) + paragraph(t('myPrivacy.contact.board')),
  );
  const day = Number.isNaN(Date.parse(summary.generatedAt)) ? '' : istDateOf(summary.generatedAt);

  const body = `<div class="head"><div class="facility">${e(summary.hospital)}</div><div class="right"><h1>${e(
    t('myPrivacy.data.printTitle'),
  )}</h1><div class="small">${e(t('myPrivacy.data.subtitle', { date: f.dateTime(summary.generatedAt) }))}</div></div></div>
<div class="patient"><span><strong>${e(summary.person.fullName)}</strong></span><span>${e(t('common.uhid'))} ${e(summary.person.uhid)}</span></div>
${person}
${categories}
${consents}
${section(t('myPrivacy.data.shared'), recipients(summary.sharedWith, 'myPrivacy.data.recipient.data', t('myPrivacy.data.shared.none')))}
${section(t('myPrivacy.data.processors'), recipients(summary.processors, 'myPrivacy.data.processor.data', t('myPrivacy.data.processors.none')))}
${nominee}
${contactSection}
<div class="footer">${e(t('myPrivacy.data.free'))}<br/>${e(t('print.printedOn', { date: f.dateTime(ctx.printedAt) }))}</div>`;

  return {
    title: t('myPrivacy.data.printTitle'),
    body,
    styles: STYLES,
    fileName: safeFileName(`privacy-summary-${day}`),
  };
}
