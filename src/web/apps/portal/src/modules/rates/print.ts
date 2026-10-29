import { escapeHtml } from '@hc360/core';
import type { Translate } from '@/i18n';
import type { Formatters } from '@/i18n/useFormat';
import { safeFileName, type PrintableDocument } from '@/lib/print';
import type { PublicRateList, RateLine } from '@/lib/types';
import { categoryKey, payableText, rateNotes } from './model';

export interface RatePrintContext {
  t: Translate;
  f: Formatters;
  printedAt: string;
}

const STYLES = `
.head { border-bottom: 2px solid #0A7CA7; padding-bottom: 6px; margin-bottom: 8px; }
.facility { font-size: 14pt; font-weight: 700; color: #045E80; }
.local { display: block; }
.amount { text-align: right; white-space: nowrap; }
.notes { margin-top: 12px; font-size: 9pt; padding-left: 16px; }
.footer { margin-top: 16px; font-size: 8.5pt; color: #555; border-top: 1px solid #ccc; padding-top: 4px; }
`;

/** A percentage as the list writes it: "18%", "12.5%". */
export function percentText(value: number, f: Formatters): string {
  return `${f.number(value)}%`;
}

/** The English name, the local name under it (marked with its language) and the code. */
function name(line: RateLine, lang: string | undefined, f: Formatters, t: Translate): string {
  const local = line.localName
    ? `<span class="local"${lang ? ` lang="${escapeHtml(lang)}"` : ''}>${escapeHtml(line.localName)}</span>`
    : '';
  return `${escapeHtml(line.name)}${line.mayVary ? ' *' : ''}${local}<span class="small muted">${escapeHtml(
    t('rates.code'),
  )} ${escapeHtml(line.code)}${line.gstRate > 0 ? ` · ${escapeHtml(t('rates.withGst', { percent: percentText(line.gstRate, f) }))}` : ''}</span>`;
}

function row(label: string, amount: string): string {
  return `<tr class="avoid-break"><td>${label}</td><td class="amount">${escapeHtml(amount)}</td></tr>`;
}

function table(title: string, rows: string[]): string {
  return rows.length ? `<h2>${escapeHtml(title)}</h2><table><tbody>${rows.join('')}</tbody></table>` : '';
}

/**
 * A copy of the rate list as the page shows it, in the reader's language, to print. The hospital's own copy, in English
 * and the local language, is the PDF.
 */
export function rateListDocument(list: PublicRateList, ctx: RatePrintContext): PrintableDocument {
  const { t, f } = ctx;
  const { rates } = list;
  const lang = list.localLanguage?.code;
  const money = (line: RateLine) => payableText(line, f, t);
  const doctors = rates.doctors.map((d) =>
    row(
      `<strong>${escapeHtml(d.name)}</strong>${d.department ? `<span class="small muted"> · ${escapeHtml(d.department)}</span>` : ''}<span class="small muted local">${escapeHtml(
        t('rates.followUp'),
      )}: ${escapeHtml(
        d.freeFollowUpDays > 0 ? t('rates.followUpFree', { days: d.freeFollowUpDays }) : t('rates.followUpCharged'),
      )}${d.video ? ` · ${escapeHtml(t('rates.byVideo'))} ${escapeHtml(money(d.video))}` : ''}</span>`,
      `${t('rates.inPerson')} ${money(d.fee)}`,
    ),
  );
  const rooms = rates.rooms.map((r) =>
    row(
      `${name(r.day, lang, f, t)}<span class="small muted local">${[
        r.intensiveCare ? t('rates.intensiveCare') : '',
        r.wards.length ? t('rates.wards', { wards: r.wards.join(', ') }) : '',
        r.nursing ? `${t('rates.nursing')} ${money(r.nursing)}` : '',
        r.visit ? `${t('rates.visit')} ${money(r.visit)}` : '',
        t('rates.multiplier', { multiplier: f.number(r.multiplier) }),
      ]
        .filter(Boolean)
        .map((part) => escapeHtml(part))
        .join(' · ')}</span>`,
      `${t('rates.bed')} ${money(r.day)}`,
    ),
  );
  const packages = rates.packages.map((p) =>
    row(
      `${name(p.price, lang, f, t)}<span class="small muted local">${escapeHtml(
        [
          p.lengthOfStayDays ? t('rates.packageStay', { days: p.lengthOfStayDays }) : '',
          t(`rates.packageFor.${p.usedFor}`),
        ]
          .filter(Boolean)
          .join(' · '),
      )}</span>`,
      money(p.price),
    ),
  );
  const notes = rateNotes(rates, (value) => percentText(value, f))
    .map((note) => `<li>${escapeHtml(t(note.key, note.vars))}</li>`)
    .join('');
  const body = `<div class="head"><div class="facility">${escapeHtml(list.facility.name)}</div><h1>${escapeHtml(
    t('rates.title'),
  )}</h1><div class="small">${escapeHtml(
    t('rates.subtitle', { facility: list.facility.name, date: f.calendarDate(rates.asOn) }),
  )}</div></div>
${rates.registration ? table(t('rates.registration'), [row(name(rates.registration, lang, f, t), money(rates.registration))]) : ''}
${table(t('rates.consultations'), doctors)}
${table(t('rates.rooms'), rooms)}${rooms.length ? `<p class="small">${escapeHtml(t('rates.note.multiplier'))}</p>` : ''}
${rates.sections
  .map((section) =>
    table(
      t(categoryKey(section.category)),
      section.lines.map((line) => row(name(line, lang, f, t), money(line))),
    ),
  )
  .join('\n')}
${table(t('rates.packages'), packages)}${packages.length ? `<p class="small">${escapeHtml(t('rates.note.packages'))}</p>` : ''}
<ul class="notes">${notes}</ul>
<div class="footer">${escapeHtml(t('print.printedOn', { date: f.dateTime(ctx.printedAt) }))}</div>`;
  return {
    title: `${t('rates.title')} · ${list.facility.name}`,
    body,
    styles: STYLES,
    fileName: safeFileName(`rate-list-${rates.asOn}`),
  };
}
