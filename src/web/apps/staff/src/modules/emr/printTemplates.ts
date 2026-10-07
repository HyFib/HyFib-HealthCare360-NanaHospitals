import { escapeHtml, formatDateOnly, formatDateTime, letterheadHtml, templateFor, type Letterhead } from '@hc360/core';
import type { DiagnosisDto, EncounterDetail, VitalsDto } from './api';
import { directionsText } from './dosing';
import type { OrderDto } from './ordersApi';
import type { PrescriptionDto } from './prescriptionsApi';
import { formatBp, formatTemperature } from './vitals';

/**
 * HTML body of the consultation summary print-out (pure string builder; every value is escaped); print.ts sends it to
 * the printer with the "Clinical notes" print template. Prescriptions and discharge summaries are made by the server
 * instead (see `@/lib/documents`), so the printed copy matches their PDF.
 */

export const CLINICAL_PRINT_STYLES = `
  body { font-family: 'Segoe UI', 'Nirmala UI', 'Mangal', system-ui, sans-serif; }
  .small { font-size: 8.5pt; color: #444; }
  .doctor { text-align: right; }
  .doctor .name { font-weight: 700; font-size: 11pt; }
  .patient { display: grid; grid-template-columns: repeat(4, auto); gap: 2px 14px; padding: 6px 8px; background: #F2F7FA; border-radius: 4px; margin-bottom: 6px; }
  .label { color: #555; font-size: 8pt; text-transform: uppercase; letter-spacing: .4px; }
  .allergy { color: #B00020; font-weight: 700; }
  .section { margin-top: 8px; }
  .section h3 { font-size: 10pt; color: #045E80; margin: 0 0 2px; }
  .pre { white-space: pre-wrap; }
  .footer { display: flex; justify-content: space-between; align-items: flex-end; margin-top: 14px; gap: 12px; }
  .sign { text-align: right; min-width: 50mm; }
  .sign .line { border-top: 1px solid #333; margin-top: 18px; padding-top: 2px; }
  .draft { color: #B00020; font-weight: 700; border: 2px solid #B00020; padding: 2px 6px; display: inline-block; }
`;

function field(label: string, value: string | null | undefined): string {
  return `<div><div class="label">${escapeHtml(label)}</div><div>${escapeHtml(value || '—')}</div></div>`;
}

function allergyLine(allergies: readonly string[]): string {
  return allergies.length > 0
    ? `<div class="allergy">Known allergies: ${allergies.map(escapeHtml).join(', ')}</div>`
    : '<div class="small">Known allergies: none recorded</div>';
}

function genderShortOf(gender: string): string {
  return gender === 'Male' ? 'M' : gender === 'Female' ? 'F' : gender === 'Other' ? 'O' : '—';
}

export interface EncounterPrintData {
  letterhead: Letterhead;
  patient: { fullName: string; uhid: string; age: string | null; gender: string };
  encounter: EncounterDetail;
  diagnoses: readonly DiagnosisDto[];
  vitals: VitalsDto | null;
  orders: readonly OrderDto[];
  prescriptions: readonly PrescriptionDto[];
  allergies: readonly string[];
}

function textSection(title: string, value: string | null | undefined): string {
  return value && value.trim()
    ? `<div class="section"><h3>${escapeHtml(title)}</h3><div class="pre">${escapeHtml(value)}</div></div>`
    : '';
}

/** A4 consultation summary for the patient file or the patient. */
export function encounterSummaryHtml(data: EncounterPrintData): string {
  const e = data.encounter;
  const vitals = data.vitals
    ? [
        data.vitals.systolicBp || data.vitals.diastolicBp
          ? `BP ${formatBp(data.vitals.systolicBp, data.vitals.diastolicBp)}`
          : null,
        data.vitals.pulse ? `Pulse ${data.vitals.pulse}/min` : null,
        data.vitals.spO2 ? `SpO₂ ${data.vitals.spO2}%` : null,
        data.vitals.temperatureCelsius ? `Temp ${formatTemperature(data.vitals.temperatureCelsius)}` : null,
        data.vitals.respiratoryRate ? `RR ${data.vitals.respiratoryRate}/min` : null,
        data.vitals.weightKg
          ? `Weight ${data.vitals.weightKg} kg${data.vitals.bmi ? ` (BMI ${data.vitals.bmi})` : ''}`
          : null,
        data.vitals.bloodGlucose ? `Glucose ${data.vitals.bloodGlucose} mg/dL` : null,
      ].filter(Boolean)
    : [];
  const diagnoses = data.diagnoses
    .filter((d) => d.type !== 'Differential')
    .map(
      (d) =>
        `<li>${escapeHtml(d.title)}${d.icdCode ? ` (${escapeHtml(d.icdCode)})` : ''}${d.isPrimary ? ' — primary' : ''}${d.type === 'Provisional' ? ' — provisional' : ''}</li>`,
    )
    .join('');
  const orders = data.orders
    .flatMap((o) =>
      o.items
        .filter((i) => i.status !== 'Cancelled')
        .map(
          (i) =>
            `<li>${escapeHtml(i.serviceName)}${o.priority !== 'Routine' ? ` (${escapeHtml(o.priority)})` : ''}</li>`,
        ),
    )
    .join('');
  const medicines = data.prescriptions
    .filter((p) => p.status === 'Signed')
    .flatMap((p) =>
      p.items.map(
        (i) =>
          `<li><b>${escapeHtml(i.drugName)}</b>${i.strength ? ` ${escapeHtml(i.strength)}` : ''} — ${escapeHtml(
            directionsText({
              dose: i.dose,
              frequency: i.frequency,
              customFrequency: i.customFrequency,
              durationDays: i.durationDays,
              route: i.route,
              instructions: i.instructions,
            }),
          )}</li>`,
      ),
    )
    .join('');
  const referral =
    e.referralReason || e.referralExternal
      ? `<div class="section"><h3>Referral</h3><div>${escapeHtml([e.referralExternal, e.referralReason].filter(Boolean).join(' — '))}</div></div>`
      : '';
  const signature =
    e.status === 'InProgress'
      ? '<div class="draft">Unsigned draft</div>'
      : `<div class="small">Signed ${escapeHtml(formatDateTime(e.signedAt))}${e.amendedAt ? ` · amended ${escapeHtml(formatDateTime(e.amendedAt))}` : ''}</div>`;

  return `
    ${letterheadHtml(data.letterhead, templateFor(data.letterhead, 'clinical-note'), {
      aside: `<div class="doctor"><div class="name">${escapeHtml(e.practitionerName ?? '')}</div><div class="small">${escapeHtml(e.departmentName ?? '')}</div></div>`,
    })}
    <h2>Consultation summary</h2>
    <div class="patient">
      ${field('Patient', data.patient.fullName)}
      ${field('UHID', data.patient.uhid)}
      ${field('Age / sex', `${data.patient.age ?? '—'} / ${genderShortOf(data.patient.gender)}`)}
      ${field('Date', formatDateTime(e.startedAt))}
    </div>
    ${allergyLine(data.allergies)}
    ${textSection('Chief complaint', e.chiefComplaint)}
    ${textSection('History of present illness', e.historyOfPresentIllness)}
    ${vitals.length > 0 ? `<div class="section"><h3>Vital signs</h3><div>${vitals.map((v) => escapeHtml(v)).join(' · ')}</div></div>` : ''}
    ${textSection('Examination', e.examination)}
    ${diagnoses ? `<div class="section"><h3>Diagnosis</h3><ul>${diagnoses}</ul></div>` : ''}
    ${textSection('Assessment', e.assessment)}
    ${textSection('Plan', e.plan)}
    ${orders ? `<div class="section"><h3>Investigations ordered</h3><ul>${orders}</ul></div>` : ''}
    ${medicines ? `<div class="section"><h3>Medicines</h3><ul>${medicines}</ul></div>` : ''}
    ${
      e.followUpDate || e.followUpAdvice
        ? `<div class="section"><h3>Follow-up</h3><div>${e.followUpDate ? `Review on ${escapeHtml(formatDateOnly(e.followUpDate))}. ` : ''}${escapeHtml(e.followUpAdvice ?? '')}</div></div>`
        : ''
    }
    ${referral}
    <div class="footer"><div>${signature}</div><div class="sign"><div class="line">${escapeHtml(e.practitionerName ?? 'Doctor')}</div></div></div>`;
}
