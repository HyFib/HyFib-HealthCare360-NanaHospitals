import {
  escapeHtml,
  formatDateTime,
  letterheadHtml,
  printDocument,
  printHtml,
  templateFor,
  type Letterhead,
} from '@hc360/core';
import { api } from '@/lib/api';
import { formatQty } from '@/modules/inventory/format';

export type RecallTraceSource = 'OpDispense' | 'CounterSale' | 'WardIssue' | 'Implant' | 'OtConsumable';

export const SOURCE_LABEL: Record<RecallTraceSource, string> = {
  OpDispense: 'OP dispense',
  CounterSale: 'Counter sale',
  WardIssue: 'Ward issue',
  Implant: 'Implanted in surgery',
  OtConsumable: 'Used in surgery',
};

/** What an operation's row adds: the surgery, the implant's serial number and the surgeon, as one line. */
export function operationDetail(row: Pick<RecallTraceRow, 'procedure' | 'serialNumber' | 'clinician'>): string {
  return [
    row.procedure,
    row.serialNumber ? `serial ${row.serialNumber}` : null,
    row.clinician ? `surgeon ${row.clinician}` : null,
  ]
    .filter((part): part is string => Boolean(part))
    .join('; ');
}

/** One hand-over of the batch to a patient: kept is what was given less what came back. */
export interface RecallTraceRow {
  source: RecallTraceSource;
  at: string;
  document: string;
  storeName: string;
  patientId: string | null;
  uhid: string | null;
  patientName: string;
  phone: string | null;
  quantity: number;
  returned: number;
  kept: number;
  /** For an operation: the surgery as booked (the document is its booking number, the store the theatre). */
  procedure: string | null;
  /** For an implant: its serial number as recorded. */
  serialNumber: string | null;
  /** For an operation: the surgeon, who reviews the patient. */
  clinician: string | null;
}

export interface RecallStockRow {
  storeName: string;
  onHand: number;
  isBlocked: boolean;
  isRecalled: boolean;
  blockReason: string | null;
}

export interface RecallTrace {
  itemId: string;
  itemCode: string;
  itemName: string;
  batchNumber: string;
  generatedAt: string;
  generatedBy: string;
  patientCount: number;
  quantityWithPatients: number;
  patients: RecallTraceRow[];
  stock: RecallStockRow[];
}

/** Every run is audited (it lists patients' names and phone numbers), so it runs only when asked, never as one types. */
export const recallTraceApi = {
  trace: (itemId: string, batchNumber: string) =>
    api.get<RecallTrace>('/api/v1/pharmacy/recalls/trace', { query: { itemId, batchNumber } }),
};

/** The call-back list: every patient who had the batch, with what they kept, and the stores still holding it. */
export function buildRecallTraceHtml(trace: RecallTrace, letterhead: Letterhead): string {
  const template = templateFor(letterhead, 'other');
  const patients = trace.patients
    .map(
      (
        p,
        i,
      ) => `<tr><td>${i + 1}</td><td>${escapeHtml(formatDateTime(p.at))}</td><td>${escapeHtml(SOURCE_LABEL[p.source])}</td>
        <td>${escapeHtml(p.document)}${operationDetail(p) ? `<br/><span class="small">${escapeHtml(operationDetail(p))}</span>` : ''}</td><td>${escapeHtml(p.patientName)}${p.uhid ? `<br/><span class="small">${escapeHtml(p.uhid)}</span>` : ''}</td>
        <td>${escapeHtml(p.phone ?? '')}</td><td class="right">${escapeHtml(formatQty(p.quantity))}</td><td class="right">${escapeHtml(formatQty(p.returned))}</td>
        <td class="right"><b>${escapeHtml(formatQty(p.kept))}</b></td><td class="blank"></td></tr>`,
    )
    .join('');
  const stock = trace.stock
    .map(
      (s) =>
        `<tr><td>${escapeHtml(s.storeName)}</td><td class="right">${escapeHtml(formatQty(s.onHand))}</td><td>${s.isRecalled ? 'Held on recall' : s.isBlocked ? 'On hold' : '<b>Not held</b>'}</td></tr>`,
    )
    .join('');
  const body = `
    ${letterheadHtml(letterhead, template, { taxIds: false })}
    <h2 class="center" style="margin-top:8px">Recall trace: patients to contact</h2>
    <table class="meta">
      <tr><td>Item</td><td><b>${escapeHtml(`${trace.itemCode} ${trace.itemName}`)}</b></td><td>Batch</td><td><b>${escapeHtml(trace.batchNumber)}</b></td></tr>
      <tr><td>Patients</td><td>${trace.patientCount}</td><td>Still with patients</td><td>${escapeHtml(formatQty(trace.quantityWithPatients))}</td></tr>
      <tr><td>Run by</td><td>${escapeHtml(trace.generatedBy)}</td><td>At</td><td>${escapeHtml(formatDateTime(trace.generatedAt))}</td></tr>
    </table>
    <div class="rule"></div>
    <table class="lines"><tr><th>#</th><th>When</th><th>How</th><th>Document</th><th>Patient</th><th>Phone</th><th class="right">Given</th><th class="right">Back</th><th class="right">Kept</th><th>Contacted / outcome</th></tr>${patients}</table>
    <h3>Stores still holding the batch</h3>
    <table class="lines"><tr><th>Store</th><th class="right">On hand</th><th>Held</th></tr>${stock || '<tr><td colspan="3">None</td></tr>'}</table>
    <p class="small">Confidential: patients' names and phone numbers. This list was produced from HC360 and its running is recorded.</p>
    <div class="sign"><div>Pharmacist: ____________________</div><div>Checked by: ____________________</div></div>`;
  const styles = `
    .small { font-size: 10px; color: #444; }
    .meta td { padding: 2px 10px 2px 0; }
    .lines th { border-bottom: 1px solid #888; font-size: 11px; text-align: left; }
    .lines td { border-bottom: 1px solid #e5e5e5; font-size: 11px; vertical-align: top; }
    .blank { width: 18%; }
    .sign { display: flex; justify-content: space-between; margin-top: 36px; }`;
  return printDocument(body, { title: `Recall trace ${trace.batchNumber}`, pageSize: 'A4', styles, template });
}

export async function printRecallTrace(trace: RecallTrace, letterhead: Letterhead): Promise<void> {
  await printHtml(buildRecallTraceHtml(trace, letterhead), { pageSize: 'A4' });
}
