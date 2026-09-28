import { escapeHtml, formatDate, formatDateOnly, formatDateTime } from '@hc360/core';
import type { DiagnosisDto, DischargeSummaryPrint, EncounterDetail, FacilityLetterhead, PrescriberInfo, VitalsDto } from './api';
import { directionsText, durationText } from './dosing';
import type { OrderDto } from './ordersApi';
import type { PrescriptionDto, PrescriptionPrintModel } from './prescriptionsApi';
import { formatBp, formatTemperature } from './vitals';

/**
 * HTML bodies of the clinical print-outs (pure string builders; every value is escaped). The print functions in
 * print.ts add the QR code and send them to the printer.
 */

export const CLINICAL_PRINT_STYLES = `
  body { font-family: 'Segoe UI', 'Nirmala UI', 'Mangal', system-ui, sans-serif; }
  .letterhead { display: flex; justify-content: space-between; gap: 12px; border-bottom: 2px solid #045E80; padding-bottom: 6px; margin-bottom: 8px; }
  .facility { font-size: 14pt; font-weight: 700; color: #045E80; }
  .small { font-size: 8.5pt; color: #444; }
  .doctor { text-align: right; }
  .doctor .name { font-weight: 700; font-size: 11pt; }
  .patient { display: grid; grid-template-columns: repeat(4, auto); gap: 2px 14px; padding: 6px 8px; background: #F2F7FA; border-radius: 4px; margin-bottom: 6px; }
  .label { color: #555; font-size: 8pt; text-transform: uppercase; letter-spacing: .4px; }
  .allergy { color: #B00020; font-weight: 700; }
  .rx { font-size: 20pt; font-weight: 700; font-family: 'Times New Roman', serif; margin: 4px 0; }
  table.lines th { border-bottom: 1px solid #999; font-size: 8.5pt; color: #333; }
  table.lines td { border-bottom: 1px solid #E3E3E3; }
  .drug { font-weight: 600; }
  .hindi { color: #333; font-size: 9pt; }
  .controlled { display: inline-block; border: 1px solid #B00020; color: #B00020; font-size: 7.5pt; padding: 0 4px; border-radius: 3px; margin-left: 4px; }
  .section { margin-top: 8px; }
  .section h3 { font-size: 10pt; color: #045E80; margin: 0 0 2px; }
  .pre { white-space: pre-wrap; }
  .footer { display: flex; justify-content: space-between; align-items: flex-end; margin-top: 14px; gap: 12px; }
  .qr svg { width: 26mm; height: 26mm; }
  .sign { text-align: right; min-width: 50mm; }
  .sign .line { border-top: 1px solid #333; margin-top: 18px; padding-top: 2px; }
  .draft { color: #B00020; font-weight: 700; border: 2px solid #B00020; padding: 2px 6px; display: inline-block; }
  .grid2 { display: grid; grid-template-columns: 1fr 1fr; gap: 4px 16px; }
`;

function letterheadHtml(letterhead: FacilityLetterhead | null, fallbackName: string, right: string): string {
  const contact = [letterhead?.phone, letterhead?.email, letterhead?.website].filter(Boolean).map(escapeHtml).join(' · ');
  const registration = [letterhead?.registrationNumber && `Reg. ${letterhead.registrationNumber}`, letterhead?.nabhNumber && `NABH ${letterhead.nabhNumber}`]
    .filter(Boolean)
    .map((part) => escapeHtml(part as string))
    .join(' · ');
  return `
    <div class="letterhead">
      <div>
        <div class="facility">${escapeHtml(letterhead?.name ?? fallbackName)}</div>
        ${letterhead?.address ? `<div class="small">${escapeHtml(letterhead.address)}</div>` : ''}
        ${contact ? `<div class="small">${contact}</div>` : ''}
        ${registration ? `<div class="small">${registration}</div>` : ''}
      </div>
      ${right}
    </div>`;
}

function prescriberHtml(prescriber: PrescriberInfo | null): string {
  if (!prescriber) {
    return '';
  }
  const registration = prescriber.registrationNumber
    ? `Reg. No. ${escapeHtml(prescriber.registrationNumber)}${prescriber.registrationCouncil ? ` (${escapeHtml(prescriber.registrationCouncil)})` : ''}`
    : '';
  return `
    <div class="doctor">
      <div class="name">${escapeHtml(prescriber.displayName)}</div>
      ${prescriber.qualification ? `<div class="small">${escapeHtml(prescriber.qualification)}</div>` : ''}
      ${[prescriber.specialty, prescriber.department].filter(Boolean).length > 0 ? `<div class="small">${escapeHtml([prescriber.specialty, prescriber.department].filter(Boolean).join(', '))}</div>` : ''}
      ${registration ? `<div class="small">${registration}</div>` : ''}
    </div>`;
}

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

/** A5 e-prescription (ERX-04): letterhead, prescriber registration, bilingual directions and the QR verification code. */
export function prescriptionHtml(model: PrescriptionPrintModel, options: { facilityName: string; qrSvg: string | null; verifyUrl: string | null }): string {
  const lines = model.lines
    .map(
      (line) => `
      <tr>
        <td>${line.sequence}.</td>
        <td>
          <div class="drug">${escapeHtml(line.drugName)}${line.strength ? ` ${escapeHtml(line.strength)}` : ''}${line.isControlledDrug ? '<span class="controlled">Schedule H1/X</span>' : ''}</div>
          ${line.genericName ? `<div class="small">${escapeHtml(line.genericName)}${line.dosageForm ? ` · ${escapeHtml(line.dosageForm)}` : ''}</div>` : ''}
        </td>
        <td>
          <div>${escapeHtml(line.dose)} · ${escapeHtml(line.frequencyEnglish)}${line.route && line.route !== 'Oral' ? ` · ${escapeHtml(line.route)}` : ''}</div>
          <div class="hindi" lang="hi">${escapeHtml(line.dose)} · ${escapeHtml(line.frequencyHindi)}</div>
          ${line.instructions ? `<div class="small">${escapeHtml(line.instructions)}</div>` : ''}
        </td>
        <td>${escapeHtml(line.frequency === 'STAT' ? 'Once' : durationText(line.durationDays))}</td>
        <td class="right">${escapeHtml(line.quantity)}</td>
      </tr>`,
    )
    .join('');

  const status =
    model.status === 'Signed'
      ? ''
      : `<div class="draft">${model.status === 'Cancelled' ? 'CANCELLED — do not dispense' : 'DRAFT — not valid for dispensing'}</div>`;

  return `
    ${letterheadHtml(model.letterhead, options.facilityName, prescriberHtml(model.prescriber))}
    ${status}
    <div class="patient">
      ${field('Patient', model.patient.name)}
      ${field('UHID', model.patient.uhid)}
      ${field('Age / sex', `${model.patient.age ?? '—'} / ${genderShortOf(model.patient.gender)}`)}
      ${field('Date', model.signedAt ? formatDateTime(model.signedAt) : '')}
    </div>
    ${allergyLine(model.knownAllergies)}
    ${model.diagnoses.length > 0 ? `<div><span class="label">Diagnosis</span> ${model.diagnoses.map(escapeHtml).join('; ')}</div>` : ''}
    <div class="rx">℞</div>
    <table class="lines">
      <thead><tr><th></th><th>Medicine</th><th>Dose and directions</th><th>Duration</th><th class="right">Qty</th></tr></thead>
      <tbody>${lines}</tbody>
    </table>
    ${model.advice ? `<div class="section"><h3>Advice</h3><div class="pre">${escapeHtml(model.advice)}</div></div>` : ''}
    ${
      model.followUpDate || model.followUpAdvice
        ? `<div class="section"><h3>Follow-up</h3><div>${model.followUpDate ? `Review on ${escapeHtml(formatDateOnly(model.followUpDate))}. ` : ''}${escapeHtml(model.followUpAdvice ?? '')}</div></div>`
        : ''
    }
    ${model.refillsAllowed > 0 ? `<div class="small">Refills allowed: ${model.refillsAllowed}</div>` : ''}
    <div class="footer">
      <div class="qr">
        ${options.qrSvg ?? ''}
        <div class="small">${escapeHtml(model.prescriptionNumber)}</div>
        ${options.verifyUrl ? '<div class="small">Scan to verify this prescription</div>' : ''}
      </div>
      <div class="sign">
        <div class="small">${model.signedAt ? `Digitally signed on ${escapeHtml(formatDateTime(model.signedAt))}` : ''}</div>
        <div class="line">${escapeHtml(model.prescriber?.displayName ?? 'Doctor')}</div>
      </div>
    </div>`;
}

export interface EncounterPrintData {
  facilityName: string;
  patient: { fullName: string; uhid: string; age: string | null; gender: string };
  encounter: EncounterDetail;
  diagnoses: readonly DiagnosisDto[];
  vitals: VitalsDto | null;
  orders: readonly OrderDto[];
  prescriptions: readonly PrescriptionDto[];
  allergies: readonly string[];
}

function textSection(title: string, value: string | null | undefined): string {
  return value && value.trim() ? `<div class="section"><h3>${escapeHtml(title)}</h3><div class="pre">${escapeHtml(value)}</div></div>` : '';
}

/** A4 consultation summary for the patient file or the patient. */
export function encounterSummaryHtml(data: EncounterPrintData): string {
  const e = data.encounter;
  const vitals = data.vitals
    ? [
        data.vitals.systolicBp || data.vitals.diastolicBp ? `BP ${formatBp(data.vitals.systolicBp, data.vitals.diastolicBp)}` : null,
        data.vitals.pulse ? `Pulse ${data.vitals.pulse}/min` : null,
        data.vitals.spO2 ? `SpO₂ ${data.vitals.spO2}%` : null,
        data.vitals.temperatureCelsius ? `Temp ${formatTemperature(data.vitals.temperatureCelsius)}` : null,
        data.vitals.respiratoryRate ? `RR ${data.vitals.respiratoryRate}/min` : null,
        data.vitals.weightKg ? `Weight ${data.vitals.weightKg} kg${data.vitals.bmi ? ` (BMI ${data.vitals.bmi})` : ''}` : null,
        data.vitals.bloodGlucose ? `Glucose ${data.vitals.bloodGlucose} mg/dL` : null,
      ].filter(Boolean)
    : [];
  const diagnoses = data.diagnoses
    .filter((d) => d.type !== 'Differential')
    .map((d) => `<li>${escapeHtml(d.title)}${d.icdCode ? ` (${escapeHtml(d.icdCode)})` : ''}${d.isPrimary ? ' — primary' : ''}${d.type === 'Provisional' ? ' — provisional' : ''}</li>`)
    .join('');
  const orders = data.orders
    .flatMap((o) => o.items.filter((i) => i.status !== 'Cancelled').map((i) => `<li>${escapeHtml(i.serviceName)}${o.priority !== 'Routine' ? ` (${escapeHtml(o.priority)})` : ''}</li>`))
    .join('');
  const medicines = data.prescriptions
    .filter((p) => p.status === 'Signed')
    .flatMap((p) =>
      p.items.map(
        (i) =>
          `<li><b>${escapeHtml(i.drugName)}</b>${i.strength ? ` ${escapeHtml(i.strength)}` : ''} — ${escapeHtml(
            directionsText({ dose: i.dose, frequency: i.frequency, customFrequency: i.customFrequency, durationDays: i.durationDays, route: i.route, instructions: i.instructions }),
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
    ${letterheadHtml(null, data.facilityName, `<div class="doctor"><div class="name">${escapeHtml(e.practitionerName ?? '')}</div><div class="small">${escapeHtml(e.departmentName ?? '')}</div></div>`)}
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

/** A4 discharge summary with letterhead and consultant registration. */
export function dischargeSummaryHtml(print: DischargeSummaryPrint, facilityName: string): string {
  const s = print.summary;
  const p = print.patient;
  const status =
    s.status === 'Draft'
      ? '<div class="draft">DRAFT — not signed</div>'
      : `<div class="small">Signed ${escapeHtml(formatDateTime(s.signedAt))}${s.amendedAt ? ` · amended ${escapeHtml(formatDateTime(s.amendedAt))} (${escapeHtml(s.amendmentReason ?? '')})` : ''}</div>`;
  return `
    ${letterheadHtml(print.letterhead, facilityName, prescriberHtml(print.consultant))}
    <h2>Discharge summary</h2>
    ${status}
    <div class="patient">
      ${field('Patient', p.fullName)}
      ${field('UHID', p.uhid)}
      ${field('Age / sex', `${p.age ?? '—'} / ${genderShortOf(p.gender)}`)}
      ${field('Admitted / discharged', `${s.admittedAt ? formatDate(s.admittedAt) : '—'} → ${s.dischargedAt ? formatDate(s.dischargedAt) : '—'}`)}
    </div>
    ${textSection('Diagnosis', [s.diagnoses, s.diagnosisCodes.length > 0 ? `ICD-10: ${s.diagnosisCodes.join(', ')}` : null].filter(Boolean).join('\n'))}
    ${textSection('Presenting complaints', s.presentingComplaints)}
    ${textSection('Course in hospital', s.courseInHospital)}
    ${textSection('Procedures', s.procedures)}
    ${textSection('Investigations', s.investigationsSummary)}
    ${textSection('Condition at discharge', s.conditionAtDischarge)}
    ${textSection('Medicines on discharge', s.medications)}
    ${textSection('Advice', s.advice)}
    ${
      s.followUpDate || s.followUpInstructions
        ? `<div class="section"><h3>Follow-up</h3><div>${s.followUpDate ? `Review on ${escapeHtml(formatDateOnly(s.followUpDate))}. ` : ''}${escapeHtml(s.followUpInstructions ?? '')}</div></div>`
        : ''
    }
    <div class="footer"><div class="small">In an emergency, come to the hospital casualty or call the hospital.</div><div class="sign"><div class="line">${escapeHtml(print.consultant?.displayName ?? s.practitionerName ?? 'Consultant')}</div></div></div>`;
}
