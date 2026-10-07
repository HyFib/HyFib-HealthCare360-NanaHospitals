import {
  escapeHtml,
  formatDateTime,
  formatTime,
  letterheadHtml,
  printDocument,
  printHtml,
  templateFor,
  type Letterhead,
} from '@hc360/core';
import type { OperationNoteDto, OtCalendar, SurgeryDetail } from './api';
import { anaesthesiaLabel, lateralityLabel, specimenLabel, surgeryStatusLabel } from './labels';
import { procedureWithSide } from './logic';

const block = (label: string, value: string | null | undefined) =>
  value
    ? `<div class="block"><div class="label">${escapeHtml(label)}</div><div class="text">${escapeHtml(value)}</div></div>`
    : '';

/** Operation note and post-operative orders on A4, for the case file and the ward ("Clinical notes" template). */
export async function printOperationNote(
  letterhead: Letterhead,
  booking: SurgeryDetail,
  note: OperationNoteDto,
): Promise<void> {
  const template = templateFor(letterhead, 'clinical-note');
  const patient = booking.patient;
  const specimens = note.specimens.length
    ? `<table class="grid"><thead><tr><th>Specimen</th><th>Sent to</th><th>Label</th></tr></thead><tbody>${note.specimens
        .map(
          (s) =>
            `<tr><td>${escapeHtml(s.description)}</td><td>${escapeHtml(specimenLabel[s.sentTo])}</td><td>${escapeHtml(s.label ?? '')}</td></tr>`,
        )
        .join('')}</tbody></table>`
    : '<div class="text">None</div>';

  const body = `
    ${letterheadHtml(letterhead, template)}
    <h1>Operation note</h1>
    <table class="meta">
      <tr><th>Patient</th><td>${escapeHtml(patient?.fullName ?? '')}</td><th>UHID</th><td>${escapeHtml(patient?.uhid ?? '')}</td></tr>
      <tr><th>Age / sex</th><td>${escapeHtml(`${patient?.age ?? '—'} / ${patient?.gender ?? ''}`)}</td><th>Booking</th><td>${escapeHtml(booking.bookingNumber)}</td></tr>
      <tr><th>Theatre</th><td>${escapeHtml(booking.theatreName ?? '')}</td><th>Surgery</th><td>${escapeHtml(formatDateTime(booking.startedAt))} to ${escapeHtml(formatTime(booking.completedAt))}</td></tr>
      <tr><th>Surgeon</th><td>${escapeHtml(booking.surgeonName ?? '')}</td><th>Anaesthetist</th><td>${escapeHtml(booking.anaesthetistName ?? '')} (${escapeHtml(anaesthesiaLabel[booking.anaesthesiaType])})</td></tr>
      <tr><th>Side</th><td>${escapeHtml(lateralityLabel[booking.laterality])}</td><th>Status</th><td>${escapeHtml(surgeryStatusLabel[booking.status])}</td></tr>
    </table>
    ${block('Pre-operative diagnosis', note.preOperativeDiagnosis)}
    ${block('Post-operative diagnosis', note.postOperativeDiagnosis)}
    ${block('Procedure performed', note.procedurePerformed)}
    ${block('Findings', note.findings)}
    ${block('Procedure details', note.procedureDetails)}
    <div class="block"><div class="label">Specimens</div>${specimens}</div>
    ${block('Drains', note.drains)}
    ${block('Estimated blood loss', note.estimatedBloodLossMl !== null ? `${note.estimatedBloodLossMl} ml` : null)}
    ${block('Complications', note.complications)}
    <div class="orders">${block('Post-operative orders', note.postOpOrders ?? 'None written')}</div>
    <div class="sign">${
      note.signedAt
        ? `Signed by ${escapeHtml(note.signedByName ?? '')} on ${escapeHtml(formatDateTime(note.signedAt))}`
        : '<strong>DRAFT — NOT SIGNED</strong>'
    }</div>`;

  const styles = `
    h1 { font-size: 15pt; margin: 2px 0 6px; }
    table.meta th { width: 16%; color: #555; font-weight: 600; }
    table.meta td { width: 34%; }
    .block { margin-top: 8px; page-break-inside: avoid; }
    .label { font-weight: 700; font-size: 10pt; text-transform: uppercase; letter-spacing: 0.5px; color: #333; }
    .text { white-space: pre-wrap; }
    table.grid th, table.grid td { border: 1px solid #aaa; }
    .orders { border: 1px solid #333; padding: 4px 8px; margin-top: 10px; }
    .sign { margin-top: 24px; text-align: right; }`;

  await printHtml(
    printDocument(body, { title: `Operation note ${booking.bookingNumber}`, pageSize: 'A4', styles, template }),
    { pageSize: 'A4' },
  );
}

/** The day's OT list by theatre (A4), for the notice board and the theatre sister. */
export async function printOtList(letterhead: Letterhead, calendar: OtCalendar, dateLabel: string): Promise<void> {
  const template = templateFor(letterhead, 'other');
  const sections = calendar.theatres
    .map((theatre) => {
      const rows = theatre.bookings
        .map(
          (b) =>
            `<tr><td>${escapeHtml(formatTime(b.start))}–${escapeHtml(formatTime(b.end))}</td><td>${escapeHtml(b.patientName)}<br/><span class="muted">${escapeHtml(
              `${b.uhid} · ${b.age ?? '—'}/${b.gender.charAt(0)}`,
            )}</span></td><td>${escapeHtml(procedureWithSide(b.procedureName, b.laterality))}</td><td>${escapeHtml(b.surgeonName ?? '')}</td><td>${escapeHtml(
              b.anaesthetistName ?? '',
            )} (${escapeHtml(b.anaesthesiaType)})</td><td>${escapeHtml(b.priority === 'Elective' ? '' : b.priority.toUpperCase())}</td></tr>`,
        )
        .join('');
      return `<h2>${escapeHtml(`${theatre.code} · ${theatre.name}`)}</h2>${
        rows
          ? `<table class="grid"><thead><tr><th>Time</th><th>Patient</th><th>Procedure</th><th>Surgeon</th><th>Anaesthetist</th><th>Priority</th></tr></thead><tbody>${rows}</tbody></table>`
          : '<div class="muted">No cases</div>'
      }`;
    })
    .join('');
  const body = `${letterheadHtml(letterhead, template)}<h1>OT list · ${escapeHtml(dateLabel)}</h1>${sections}<div class="muted" style="margin-top:12px">Printed ${escapeHtml(
    formatDateTime(new Date().toISOString()),
  )}. Confirm the side and site at the WHO Time-out for every case.</div>`;
  const styles = `
    h1 { font-size: 15pt; } h2 { font-size: 12pt; margin-top: 12px; border-bottom: 1px solid #999; }
    table.grid th, table.grid td { border: 1px solid #aaa; }`;
  await printHtml(printDocument(body, { title: `OT list ${dateLabel}`, pageSize: 'A4', styles, template }), {
    pageSize: 'A4',
  });
}
