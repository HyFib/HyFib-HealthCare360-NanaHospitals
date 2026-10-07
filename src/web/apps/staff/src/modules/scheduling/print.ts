import {
  escapeHtml,
  formatInr,
  formatTime,
  letterheadHtml,
  printDocument,
  printHtml,
  templateFor,
  type Letterhead,
} from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import type { AppointmentRef } from './api';
import { formatDayMedium, istDate } from './dates';
import { paymentLabel, visitTypeLabel } from './labels';

/**
 * 80 mm appointment slip handed to the patient (or read out on the phone). The QR code carries only the appointment
 * number, which the desk can type or scan into the appointment search on the day of the visit. It has the facility's
 * letterhead, and the closing note of the "Other documents" print template.
 */
export async function printAppointmentSlip(letterhead: Letterhead, appointment: AppointmentRef): Promise<void> {
  const template = templateFor(letterhead, 'other');
  const qr = await barcodeSvg(appointment.number, 'qrcode', { scale: 2 });
  const where = [appointment.departmentName, appointment.room ? `Room ${appointment.room}` : null]
    .filter(Boolean)
    .join(' · ');
  const body = `
    <div class="slip">
      ${letterheadHtml(letterhead, template, { compact: true })}
      <div class="label">Appointment</div>
      <div class="number">${escapeHtml(appointment.number)}</div>
      <div class="date">${escapeHtml(formatDayMedium(istDate(appointment.startsAt)))}</div>
      <div class="time">${escapeHtml(formatTime(appointment.startsAt))}</div>
      <div class="doctor">${escapeHtml(appointment.practitionerName)}</div>
      ${where ? `<div class="small">${escapeHtml(where)}</div>` : ''}
      <hr/>
      <div>${escapeHtml(appointment.patientName)}</div>
      <div class="small">UHID ${escapeHtml(appointment.uhid)}</div>
      <div class="small">${escapeHtml(visitTypeLabel[appointment.visitType])} · ${appointment.isTelehealth ? 'Video consultation' : 'Clinic visit'}</div>
      ${
        appointment.feeAmount > 0
          ? `<div class="small">Consultation fee ${escapeHtml(formatInr(appointment.feeAmount))} · ${escapeHtml(paymentLabel[appointment.paymentStatus])}</div>`
          : ''
      }
      <div class="qr">${qr}</div>
      <div class="note">${
        appointment.isTelehealth
          ? 'The video consultation link is sent to your mobile by SMS/WhatsApp. Open it a few minutes before your time.'
          : 'Please come 15 minutes early and show this slip at the front desk.'
      }</div>
    </div>`;
  const styles = `
    .slip { width: 72mm; font-family: 'Segoe UI', sans-serif; text-align: center; font-size: 10pt; }
    .label { margin-top: 6px; font-size: 9pt; text-transform: uppercase; letter-spacing: 1px; }
    .number { font-family: Consolas, monospace; font-size: 12pt; font-weight: 700; letter-spacing: 1px; }
    .date { margin-top: 4px; font-weight: 600; }
    .time { font-size: 22pt; font-weight: 800; line-height: 1.15; }
    .doctor { font-weight: 600; }
    .small { font-size: 8pt; color: #333; }
    .qr svg { width: 24mm; height: 24mm; margin-top: 6px; }
    .note { margin-top: 6px; font-size: 8pt; }
    hr { border: none; border-top: 1px dashed #666; margin: 6px 0; }`;
  await printHtml(
    printDocument(body, { title: `Appointment ${appointment.number}`, pageSize: 'Receipt80', styles, template }),
    { pageSize: 'Receipt80', silent: true },
  );
}
