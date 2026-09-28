import { escapeHtml, formatDateOnly, formatDateTime, formatPhone, humanize, printDocument, printHtml } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { ADMISSION_SOURCE_LABEL, ADMISSION_TYPE_LABEL, PAYER_LABEL, type AdmissionDetail } from './api';

function row(label: string, value: string | null | undefined): string {
  return `<tr><th>${escapeHtml(label)}</th><td>${escapeHtml(value || '—')}</td></tr>`;
}

/**
 * Admission slip (A5) handed to the attendant and filed in the case sheet: identity, admission and visit numbers,
 * ward and bed, doctors, payer, attendant and a scannable UHID. Every value is escaped.
 */
export async function printAdmissionSlip(admission: AdmissionDetail, facilityName: string): Promise<void> {
  const barcode = await barcodeSvg(admission.uhid, 'code128', { height: 9 });
  const attendant = [admission.attendantName, admission.attendantRelation ? `(${admission.attendantRelation})` : null, admission.attendantPhone ? formatPhone(admission.attendantPhone) : null]
    .filter(Boolean)
    .join(' ');
  const body = `
    <div class="head">
      <div>
        <div class="hospital">${escapeHtml(facilityName)}</div>
        <h1>Admission slip</h1>
      </div>
      <div class="barcode">${barcode}<div class="mono center">${escapeHtml(admission.uhid)}</div></div>
    </div>
    <div class="rule"></div>
    <div class="patient">${escapeHtml(admission.patientName)} <span class="muted">· ${escapeHtml(admission.age ?? '—')} / ${escapeHtml(humanize(admission.gender))}</span></div>
    <table class="grid">
      ${row('Admission number', admission.admissionNumber)}
      ${row('IP visit number', admission.visitNumber)}
      ${row('Admitted on', formatDateTime(admission.admittedAt))}
      ${row('Ward and bed', [admission.wardName, admission.bedLabel].filter(Boolean).join(' · '))}
      ${row('Consultant in charge', admission.consultantName)}
      ${row('Admitting doctor', admission.admittingPractitionerName)}
      ${row('Department', admission.departmentName)}
      ${row('Admission type', ADMISSION_TYPE_LABEL[admission.type] ?? humanize(admission.type))}
      ${row('Source', ADMISSION_SOURCE_LABEL[admission.source] ?? humanize(admission.source))}
      ${row('Provisional diagnosis', admission.provisionalDiagnosis)}
      ${row('Payment by', PAYER_LABEL[admission.payerType] ?? humanize(admission.payerType))}
      ${admission.policyNumberMasked ? row('Policy / card', admission.policyNumberMasked) : ''}
      ${admission.isMlc ? row('Medico-legal case', admission.mlcNumber ?? 'Yes') : ''}
      ${admission.expectedDischargeDate ? row('Expected discharge', formatDateOnly(admission.expectedDischargeDate)) : ''}
      ${row('Attendant', attendant)}
      ${admission.notes ? row('Notes', admission.notes) : ''}
    </table>
    <div class="rule"></div>
    <p class="small">Please keep this slip safe. Show the UHID at the billing counter, pharmacy and laboratory. Visiting hours and the attendant pass
    rules are displayed at the nursing station.</p>
    <div class="signs"><span>Admitted by</span><span>Patient / attendant signature</span></div>`;
  const styles = `
    .head { display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; }
    .hospital { font-weight: 700; font-size: 12pt; color: #045E80; }
    h1 { font-size: 14pt; margin-top: 2px; }
    .barcode { width: 55mm; }
    .barcode svg { width: 100%; height: 12mm; }
    .mono { font-family: Consolas, monospace; letter-spacing: 1px; }
    .patient { font-size: 13pt; font-weight: 700; margin: 4px 0 6px; }
    .grid th { width: 38%; color: #444; font-weight: 600; }
    .grid td, .grid th { border-bottom: 1px solid #ddd; }
    .small { font-size: 8.5pt; color: #333; }
    .signs { display: flex; justify-content: space-between; margin-top: 28px; font-size: 9pt; }
    .signs span { border-top: 1px solid #555; padding-top: 3px; min-width: 40%; text-align: center; }`;
  await printHtml(printDocument(body, { title: `Admission ${admission.admissionNumber}`, pageSize: 'A5', styles }), { pageSize: 'A5' });
}

/**
 * Patient wristband on a 50 × 25 mm label: name, age/sex, admission number and a Code 128 of the UHID, which bedside
 * scanners read before medicines, samples and blood. Printed silently on the label printer inside HC360 Desktop.
 */
export async function printWristband(admission: Pick<AdmissionDetail, 'uhid' | 'patientName' | 'age' | 'gender' | 'admissionNumber' | 'wardName' | 'bedLabel'>): Promise<void> {
  const barcode = await barcodeSvg(admission.uhid, 'code128', { height: 7, scale: 2 });
  const place = [admission.wardName, admission.bedLabel].filter(Boolean).join(' ');
  const body = `
    <div class="band">
      <div class="name">${escapeHtml(admission.patientName)}</div>
      <div class="meta">${escapeHtml(admission.age ?? '—')}/${escapeHtml(admission.gender.charAt(0))} · ${escapeHtml(admission.admissionNumber)}${place ? ` · ${escapeHtml(place)}` : ''}</div>
      <div class="code">${barcode}</div>
      <div class="uhid">${escapeHtml(admission.uhid)}</div>
    </div>`;
  const styles = `
    body { font-size: 7pt; }
    .band { width: 46mm; height: 21mm; overflow: hidden; display: flex; flex-direction: column; }
    .name { font-weight: 700; font-size: 8pt; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .meta { font-size: 6pt; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .code svg { width: 100%; height: 9mm; display: block; }
    .uhid { font-family: Consolas, monospace; font-size: 7pt; letter-spacing: 1px; text-align: center; }`;
  await printHtml(printDocument(body, { title: `Wristband ${admission.uhid}`, pageSize: 'Label50x25', styles }), { pageSize: 'Label50x25', silent: true });
}
