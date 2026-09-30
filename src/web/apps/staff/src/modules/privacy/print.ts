import {
  escapeHtml,
  formatDate,
  formatDateOnly,
  formatDateTime,
  formatPhone,
  humanize,
  printDocument,
  printHtml,
} from '@hc360/core';
import type { BreachReport, DataSummary, PrivacyContact } from './api';
import {
  breachDataCategoryLabel,
  breachNatureLabel,
  breachNatureTerm,
  breachStatusLabel,
  consentTypeLabel,
  genderLabel,
} from './labels';
import { itemDatesText } from './rules';

/** Free text with its line breaks kept; every value is escaped before it reaches the page. */
function paragraph(text: string | null | undefined): string {
  return text && text.trim()
    ? `<p>${escapeHtml(text.trim()).replace(/\r?\n/g, '<br>')}</p>`
    : '<p class="empty">Not recorded yet.</p>';
}

function row(label: string, value: string | number | null | undefined): string {
  const shown = value === null || value === undefined || value === '' ? '—' : value;
  return `<tr><th scope="row">${escapeHtml(label)}</th><td>${escapeHtml(shown)}</td></tr>`;
}

function contactRows(contact: PrivacyContact): string {
  return [
    row('Contact', contact.name ? `${contact.name}, ${contact.title}` : contact.title),
    row('E-mail', contact.email),
    row('Phone', contact.phone ? formatPhone(contact.phone) : null),
    row('Address', contact.address),
  ].join('');
}

const SHARED_STYLES = `
  .head { border-bottom: 2px solid #045E80; padding-bottom: 6px; margin-bottom: 10px; }
  .hospital { font-size: 15px; font-weight: 700; color: #045E80; }
  h1 { font-size: 17px; margin: 4px 0 2px; }
  h2 { font-size: 13px; margin: 14px 0 4px; color: #045E80; border-bottom: 1px solid #cfdfe8; padding-bottom: 2px; }
  h3 { font-size: 12px; margin: 10px 0 2px; }
  p { margin: 2px 0 6px; line-height: 1.45; }
  .empty { color: #666; font-style: italic; }
  .rule-ref { color: #555; font-weight: 400; font-size: 10px; }
  table { border: 1px solid #ccc; margin: 4px 0 8px; }
  table.facts th { width: 32%; background: #f3f7fa; font-weight: 600; }
  thead th { background: #eef5f9; border-bottom: 1px solid #bbb; }
  tbody tr { border-bottom: 1px solid #e5e5e5; }
  td.num { text-align: right; white-space: nowrap; }
  .keep { break-inside: avoid; }
  .foot { margin-top: 16px; color: #555; font-size: 9.5px; border-top: 1px solid #ccc; padding-top: 4px; }
  .sign { margin-top: 24px; display: flex; gap: 32px; }
  .sign div { flex: 1; border-top: 1px solid #333; padding-top: 4px; }`;

// ------------------------------------------------------------------ data summary

/**
 * The data summary as handed to the patient (DPDP Act s11): what we hold, why, on what basis, how long we keep it,
 * who received it and whom to contact. Every value is escaped.
 */
export function dataSummaryHtml(
  summary: DataSummary,
  requestNumber: string | null,
  printedAt = new Date().toISOString(),
): string {
  const p = summary.person;
  const person = [
    row('Name', p.fullName),
    row('UHID (hospital number)', p.uhid),
    row('Gender', genderLabel[p.gender] ?? humanize(p.gender)),
    row('Date of birth', p.dateOfBirth ? formatDateOnly(p.dateOfBirth) : null),
    row('Mobile number', p.phone ? formatPhone(p.phone) : null),
    row('Other phone number', p.alternatePhone ? formatPhone(p.alternatePhone) : null),
    row('E-mail address', p.email),
    row('Address', p.address),
    row('Emergency contact', p.emergencyContact),
    row('ABHA number', p.abhaNumber),
    row('ABHA address', p.abhaAddress),
    row('Aadhaar', p.aadhaarLast4),
    row('Photograph on record', p.hasPhoto ? 'Yes' : 'No'),
    row(
      'Registered',
      `${formatDateTime(p.registeredAt)}${p.registeredAtFacility ? ` at ${p.registeredAtFacility}` : ''}`,
    ),
  ].join('');

  const categories = summary.categories
    .map((c) => {
      const items =
        c.items.length === 0
          ? '<p class="empty">No records of this kind apart from the details above.</p>'
          : `<table><thead><tr><th>Records</th><th class="right">How many</th><th>Dates</th></tr></thead><tbody>${c.items
              .map(
                (i) =>
                  `<tr><td>${escapeHtml(i.label)}</td><td class="num">${escapeHtml(i.count.toLocaleString('en-IN'))}</td><td>${escapeHtml(itemDatesText(i.first, i.last) || '—')}</td></tr>`,
              )
              .join('')}</tbody></table>`;
      return `<div class="keep"><h3>${escapeHtml(c.title)}</h3>
        <p>${escapeHtml(c.description)}</p>
        <table class="facts"><tbody>${row('Why we hold it', c.purpose)}${row('Legal basis', c.basis)}${row('How long we keep it', c.retention)}</tbody></table>
        ${items}</div>`;
    })
    .join('');

  const consents =
    summary.consents.length === 0
      ? '<p class="empty">No consents are recorded.</p>'
      : `<table><thead><tr><th>Consent</th><th>Answer</th><th>Recorded</th><th>Withdrawn</th></tr></thead><tbody>${summary.consents
          .map(
            (c) =>
              `<tr><td>${escapeHtml(consentTypeLabel(c.type, humanize))}</td><td>${c.granted ? 'Given' : 'Refused'}</td><td>${escapeHtml(formatDate(c.capturedAt))}</td><td>${escapeHtml(c.revokedAt ? formatDate(c.revokedAt) : '—')}</td></tr>`,
          )
          .join('')}</tbody></table>`;

  const recipients = (list: DataSummary['sharedWith'], none: string) =>
    list.length === 0
      ? `<p class="empty">${escapeHtml(none)}</p>`
      : `<table><thead><tr><th>Organisation</th><th>Role</th><th>What they received</th></tr></thead><tbody>${list
          .map(
            (r) =>
              `<tr><td>${escapeHtml(r.name)}</td><td>${escapeHtml(r.role)}</td><td>${escapeHtml(r.data)}</td></tr>`,
          )
          .join('')}</tbody></table>`;

  return `
  <div class="head">
    <div class="hospital">${escapeHtml(summary.hospital)}</div>
    <h1>Summary of your personal data</h1>
    <div>${escapeHtml(p.fullName)} · UHID ${escapeHtml(p.uhid)} · prepared ${escapeHtml(formatDateTime(summary.generatedAt))}${
      requestNumber ? ` · request ${escapeHtml(requestNumber)}` : ''
    }</div>
  </div>
  <p>Under the Digital Personal Data Protection Act 2023 you may ask what personal data the hospital holds about you.
  This summary lists it, why we hold it, how long we keep it and who we have shared it with.</p>
  <h2>Your registration details</h2>
  <table class="facts"><tbody>${person}</tbody></table>
  <h2>What we hold about you</h2>
  ${categories}
  <h2>Consents</h2>
  ${consents}
  <h2>Who we shared it with</h2>
  ${recipients(summary.sharedWith, 'We have not shared your data with any other organisation.')}
  <h2>Service providers who process it for us</h2>
  ${recipients(summary.processors, 'No outside service provider processes your data for us.')}
  <h2>Your nominee</h2>
  ${
    summary.nominee
      ? `<p>${escapeHtml(summary.nominee.name)} (${escapeHtml(summary.nominee.relationship)}) can act for you after your death or if you cannot act.</p>`
      : '<p class="empty">You have not named a nominee.</p>'
  }
  <h2>Whom to contact</h2>
  <p>If anything here is wrong or missing, you can ask us to correct it. You can also ask us to erase data we no longer
  need, or complain about how we handle your data.</p>
  <table class="facts"><tbody>${contactRows(summary.contact)}</tbody></table>
  <div class="foot">Printed ${escapeHtml(formatDateTime(printedAt))} from HealthCare360.</div>`;
}

export async function printDataSummary(summary: DataSummary, requestNumber: string | null): Promise<void> {
  await printHtml(
    printDocument(dataSummaryHtml(summary, requestNumber), {
      title: `Personal data summary ${summary.person.uhid}`,
      pageSize: 'A4',
      styles: SHARED_STYLES,
    }),
    { pageSize: 'A4' },
  );
}

// ------------------------------------------------------------------ the Board's report

function section(title: string, rule: string, body: string): string {
  const ref = rule ? ` <span class="rule-ref">${escapeHtml(rule)}</span>` : '';
  return `<div class="keep"><h2>${escapeHtml(title)}${ref}</h2>${body}</div>`;
}

/**
 * The detailed report to the Data Protection Board (DPDP Act s8(6); DPDP Rules 2025, rule 7(2)(b)): who the hospital
 * is, and every part the rule asks for. Every value is escaped; parts not filled in say so.
 */
export function breachReportHtml(report: BreachReport, printedAt = new Date().toISOString()): string {
  const b = report.breach;
  const occurred = b.occurredFrom
    ? b.occurredTo && b.occurredTo !== b.occurredFrom
      ? `${formatDateTime(b.occurredFrom)} to ${formatDateTime(b.occurredTo)}`
      : formatDateTime(b.occurredFrom)
    : 'Not yet established';
  const nature =
    b.nature.length === 0
      ? 'Being established'
      : b.nature.map((n) => `${breachNatureTerm[n]} (${breachNatureLabel[n].toLowerCase()})`).join('; ');
  const data =
    b.dataCategories.length === 0
      ? 'Being established'
      : b.dataCategories.map((c) => breachDataCategoryLabel[c]).join('; ');
  const due = b.extendedUntil
    ? `${formatDateTime(b.reportDueAt)} (extended by the Board)`
    : `${formatDateTime(b.reportDueAt)} (72 hours after the hospital became aware)`;

  const fiduciary = `<table class="facts"><tbody>
    ${row('Name', report.hospital)}
    ${row('Legal name', report.legalName)}
    ${row('Address', report.address)}
    ${row('Registration number', report.registrationNumber)}
    ${contactRows(report.contact)}
  </tbody></table>`;

  const summary = `<table class="facts"><tbody>
    ${row('Reference', b.breachNumber)}
    ${row('Title', b.title)}
    ${row('The hospital became aware', formatDateTime(b.awareAt))}
    ${row('The Board was first told', b.boardInformedAt ? formatDateTime(b.boardInformedAt) : 'Not yet')}
    ${row('Board reference', b.boardReference)}
    ${row('Detailed report due', due)}
    ${row('Status', breachStatusLabel[b.status])}
  </tbody></table>`;

  const extent = `<table class="facts"><tbody>
    ${row('Nature', nature)}
    ${row('Personal data affected', data)}
    ${row('Estimated number of people affected', b.estimatedAffected !== null ? b.estimatedAffected.toLocaleString('en-IN') : 'Being established')}
    ${row('People identified so far', b.affectedListed.toLocaleString('en-IN'))}
    ${row('When it happened', occurred)}
    ${row('Where (systems, places or suppliers)', b.location)}
  </tbody></table>`;

  const intimation = `<table class="facts"><tbody>
    ${row('Notices first sent', b.notifiedAt ? formatDateTime(b.notifiedAt) : 'Not yet sent')}
    ${row('People identified as affected', b.affectedListed.toLocaleString('en-IN'))}
    ${row('People sent the notice', b.notified.toLocaleString('en-IN'))}
    ${row('People with no mobile number or e-mail address', b.withoutContact.toLocaleString('en-IN'))}
  </tbody></table>
  <p>Each notice gives a description of the breach, its likely consequences, what the hospital is doing to reduce the
  risk, what the person can do to protect themselves, and whom to contact. It is sent by SMS (a short notice with a link
  to the full notice in the patient portal) and by e-mail where the person has an address.</p>
  <h3>What people were told they can do</h3>${paragraph(b.principalSteps)}
  <h3>The short notice sent by SMS</h3>${paragraph(b.principalNotice)}`;

  return `
  <div class="head">
    <div class="hospital">${escapeHtml(report.hospital)}</div>
    <h1>Report of a personal data breach to the Data Protection Board of India</h1>
    <div>Detailed report under section 8(6) of the Digital Personal Data Protection Act 2023 and rule 7(2)(b) of the
    Digital Personal Data Protection Rules 2025 · reference ${escapeHtml(b.breachNumber)}</div>
  </div>
  ${section('The Data Fiduciary', '', fiduciary)}
  ${section('The breach', '', summary)}
  ${section('Description of the breach', 'Rule 7(2)(b)', paragraph(b.description))}
  ${section('Nature, extent, timing and location', 'Rule 7(2)(b)', extent)}
  ${section('Likely impact on the people affected', 'Rule 7(2)(b)', paragraph(b.consequences))}
  ${section('Events, circumstances and reasons that led to the breach', 'Rule 7(2)(b)(i)', paragraph(b.cause))}
  ${section('Measures taken or proposed to reduce the risk', 'Rule 7(2)(b)(ii)', paragraph(b.mitigation))}
  ${section(
    'Findings about the person who caused the breach',
    'Rule 7(2)(b)(iii)',
    b.findings && b.findings.trim()
      ? paragraph(b.findings)
      : '<p class="empty">No findings about who caused it have been recorded.</p>',
  )}
  ${section('Remedial measures to stop it happening again', 'Rule 7(2)(b)(iv)', paragraph(b.remediation))}
  ${section('Report on the notices to the people affected', 'Rule 7(2)(b)(v)', intimation)}
  <div class="sign keep">
    <div>Name and designation of the person submitting</div>
    <div>Signature</div>
    <div>Date</div>
  </div>
  <div class="foot">Prepared ${escapeHtml(formatDateTime(report.generatedAt))} from HealthCare360 · printed ${escapeHtml(formatDateTime(printedAt))}.</div>`;
}

export async function printBreachReport(report: BreachReport): Promise<void> {
  await printHtml(
    printDocument(breachReportHtml(report), {
      title: `Breach report ${report.breach.breachNumber}`,
      pageSize: 'A4',
      styles: SHARED_STYLES,
    }),
    { pageSize: 'A4' },
  );
}
