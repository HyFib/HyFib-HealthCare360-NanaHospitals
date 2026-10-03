import {
  escapeHtml,
  formatDateOnly,
  formatDateTime,
  formatPhone,
  humanize,
  printDocument,
  printHtml,
  type WristbandLabel,
} from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { printLabelJob } from '@/lib/labelPrinting';
import {
  ADMISSION_SOURCE_LABEL,
  ADMISSION_TYPE_LABEL,
  admissionApi,
  PAYER_LABEL,
  type AdmissionDetail,
  type WristbandDto,
} from './api';

function row(label: string, value: string | null | undefined): string {
  return `<tr><th>${escapeHtml(label)}</th><td>${escapeHtml(value || '—')}</td></tr>`;
}

/**
 * Admission slip (A5) handed to the attendant and filed in the case sheet: identity, admission and visit numbers,
 * ward and bed, doctors, payer, attendant and a scannable UHID. Every value is escaped.
 */
export async function printAdmissionSlip(admission: AdmissionDetail, facilityName: string): Promise<void> {
  const barcode = await barcodeSvg(admission.uhid, 'code128', { height: 9 });
  const attendant = [
    admission.attendantName,
    admission.attendantRelation ? `(${admission.attendantRelation})` : null,
    admission.attendantPhone ? formatPhone(admission.attendantPhone) : null,
  ]
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
  await printHtml(printDocument(body, { title: `Admission ${admission.admissionNumber}`, pageSize: 'A5', styles }), {
    pageSize: 'A5',
  });
}

/** Age and sex as the band shows them: "46Y/M"; only the sex when the age is not known. */
export function bandAgeSex(band: Pick<WristbandDto, 'age' | 'gender'>): string {
  return [band.age, band.gender.charAt(0)].filter(Boolean).join('/');
}

/** The wristband as the server writes it for a ZPL or TSPL band printer: the same facts as the band page. */
export function wristbandData(band: WristbandDto): WristbandLabel {
  return {
    uhid: band.uhid,
    patientName: band.patientName,
    ageSex: bandAgeSex(band),
    admissionNumber: band.admissionNumber,
    ward: band.wardName,
    bed: band.bedLabel,
    allergies: band.allergies,
  };
}

/** The band's allergy mark: black for allergies on the chart, outlined when nobody has recorded them, none for NKA. */
function allergyMark(allergies: WristbandDto['allergies']): string {
  switch (allergies) {
    case 'Recorded':
      return '<div class="allergy">ALLERGY</div>';
    case 'NotRecorded':
      return '<div class="allergy-unknown">ALLERGIES<br>NOT RECORDED</div>';
    default:
      return '';
  }
}

/**
 * The wristband as a page on an adult band (1 × 11 inches), printed along its length: name, UHID, age/sex, admission
 * number, ward and bed, a black ALLERGY mark when allergies are on the chart (an outlined ALLERGIES NOT RECORDED mark
 * when nobody has recorded them, none for no known allergies), and a Code 128 of the UHID. For band printers on their
 * Windows driver, and for browsers.
 */
export async function wristbandHtml(band: WristbandDto): Promise<string> {
  const barcode = await barcodeSvg(band.uhid, 'code128', { height: 9, scale: 2 });
  const place = [band.wardName, band.bedLabel].filter(Boolean).join(' / ');
  const body = `
    <div class="band">
      <div class="text">
        <div class="name">${escapeHtml(band.patientName)}</div>
        <div class="meta">UHID ${escapeHtml(band.uhid)} · ${escapeHtml(bandAgeSex(band))}</div>
        <div class="meta">${escapeHtml(band.admissionNumber)}${place ? ` · ${escapeHtml(place)}` : ''}</div>
      </div>
      ${allergyMark(band.allergies)}
      <div class="code">${barcode}</div>
    </div>`;
  // The band is laid out 250 × 20 mm and turned a quarter turn clockwise, so it reads along the wrist (letter tops
  // towards the band's right-hand edge as printed, as on raw ZPL and TSPL bands).
  const styles = `
    .band { position: absolute; top: 0; left: 22mm; width: 250mm; height: 20mm; transform: rotate(90deg); transform-origin: top left;
            display: flex; align-items: center; gap: 4mm; padding: 0 4mm; font-family: 'Segoe UI', Arial, sans-serif; }
    .text { flex: 0 1 110mm; min-width: 0; }
    .name { font-weight: 800; font-size: 11pt; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .meta { font-size: 8pt; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .allergy { background: #000; color: #fff; font-weight: 800; font-size: 10pt; letter-spacing: 1px; padding: 3mm 4mm;
               -webkit-print-color-adjust: exact; print-color-adjust: exact; }
    .allergy-unknown { border: 0.6mm solid #000; font-weight: 800; font-size: 7pt; line-height: 1.15; text-align: center;
                       white-space: nowrap; padding: 1.5mm 2mm; }
    .code svg { height: 15mm; width: auto; display: block; }`;
  return printDocument(body, { title: `Wristband ${band.uhid}`, pageSize: 'Wristband', styles });
}

/**
 * The patient's wristband on this computer's wristband printer, without a dialog (FO-03): name, age/sex, admission
 * number, ward and bed, the UHID that bedside scanners read before medicines, samples and blood, and the ALLERGY mark
 * from the chart. The band's data is read fresh, so a band printed after an allergy is recorded carries the mark.
 */
export async function printWristband(admission: Pick<AdmissionDetail, 'id'>): Promise<void> {
  const band = await admissionApi.wristband(admission.id);
  await printLabelJob({ kind: 'wristbands', labels: [wristbandData(band)], page: () => wristbandHtml(band) });
}
