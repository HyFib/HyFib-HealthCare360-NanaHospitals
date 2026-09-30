import { escapeHtml, formatDateOnly, formatDateTime } from '@hc360/core';
import type { CheckupReport } from './api';
import { bookingStatusLabel } from './itinerary';

/**
 * Consolidated health-checkup report (MHC-04) as print HTML (pure; every value escaped): letterhead, the day's
 * itinerary, the investigations done, and the reviewing doctor's summary and recommendations.
 */
export function checkupReportHtml(report: CheckupReport, facilityName: string): string {
  const { booking: b, patient: p, letterhead: l, reviewingDoctor: d } = report;
  const contact = [l?.phone, l?.email, l?.website].filter(Boolean).map(escapeHtml).join(' · ');
  const stops = b.itinerary
    .toSorted((x, y) => x.sequence - y.sequence)
    .map(
      (s) =>
        `<tr><td>${escapeHtml(s.departmentName)}</td><td>${escapeHtml(s.description)}</td><td>${escapeHtml(s.status === 'Done' ? 'Done' : s.status === 'Skipped' ? 'Not done' : 'Pending')}</td><td>${escapeHtml(s.notes ?? '')}</td></tr>`,
    )
    .join('');
  const tests = (report.order?.items ?? [])
    .filter((i) => i.status !== 'Cancelled')
    .map(
      (i) =>
        `<tr><td>${escapeHtml(i.serviceName)}</td><td>${escapeHtml(i.status === 'Completed' ? 'Reported' : i.status === 'InProgress' ? 'In progress' : 'Pending')}</td></tr>`,
    )
    .join('');
  const gender = p.gender === 'Male' ? 'M' : p.gender === 'Female' ? 'F' : p.gender === 'Other' ? 'O' : '—';
  const doctor = d
    ? `<div class="name">${escapeHtml(d.displayName)}</div>${d.qualification ? `<div class="small">${escapeHtml(d.qualification)}</div>` : ''}${
        d.registrationNumber
          ? `<div class="small">Reg. No. ${escapeHtml(d.registrationNumber)}${d.registrationCouncil ? ` (${escapeHtml(d.registrationCouncil)})` : ''}</div>`
          : ''
      }`
    : '<div class="small">Reviewing doctor</div>';

  return `
    <div class="letterhead">
      <div>
        <div class="facility">${escapeHtml(l?.name ?? facilityName)}</div>
        ${l?.address ? `<div class="small">${escapeHtml(l.address)}</div>` : ''}
        ${contact ? `<div class="small">${contact}</div>` : ''}
      </div>
      <div class="doctor"><div class="name">Health check-up report</div><div class="small">${escapeHtml(b.bookingNumber)}</div></div>
    </div>
    ${b.status === 'Completed' ? '' : `<div class="draft">${escapeHtml(bookingStatusLabel[b.status])} — not final</div>`}
    <div class="patient">
      <div><div class="label">Patient</div><div>${escapeHtml(p.fullName)}</div></div>
      <div><div class="label">UHID</div><div>${escapeHtml(p.uhid)}</div></div>
      <div><div class="label">Age / sex</div><div>${escapeHtml(`${p.age ?? '—'} / ${gender}`)}</div></div>
      <div><div class="label">Date</div><div>${escapeHtml(formatDateOnly(b.scheduledDate))}</div></div>
    </div>
    <div><span class="label">Package</span> ${escapeHtml(`${b.packageName} (${b.packageCode})`)}${b.corporateName ? ` · ${escapeHtml(b.corporateName)}` : ''}</div>
    <div class="section"><h3>Doctor's summary</h3><div class="pre">${escapeHtml(b.summary ?? 'Not yet written.')}</div></div>
    ${b.recommendations ? `<div class="section"><h3>Recommendations</h3><div class="pre">${escapeHtml(b.recommendations)}</div></div>` : ''}
    ${tests ? `<div class="section"><h3>Investigations</h3><table class="lines"><thead><tr><th>Test</th><th>Status</th></tr></thead><tbody>${tests}</tbody></table><div class="small">Detailed laboratory, imaging and cardiology reports are attached separately.</div></div>` : ''}
    ${stops ? `<div class="section"><h3>Check-up stations</h3><table class="lines"><thead><tr><th>Department</th><th>Station</th><th>Status</th><th>Notes</th></tr></thead><tbody>${stops}</tbody></table></div>` : ''}
    <div class="footer">
      <div class="small">${b.completedAt ? `Completed ${escapeHtml(formatDateTime(b.completedAt))}` : ''}</div>
      <div class="sign"><div class="line">${doctor}</div></div>
    </div>`;
}
