import {
  escapeHtml,
  formatDateTime,
  formatTime,
  humanize,
  letterheadHtml,
  printDocument,
  printHtml,
  templateFor,
  type Letterhead,
} from '@hc360/core';
import { WARD_TYPE_LABEL } from './api';
import type { DowntimePatient, DowntimeWard, DowntimeWardPack } from './wardCareApi';

const ALLERGY_TEXT: Record<DowntimePatient['allergyStatus'], string> = {
  Recorded: '',
  NoKnownAllergies: 'No known allergies',
  NotRecorded: 'ALLERGIES NOT RECORDED — ask before giving any medicine',
};

function allergies(p: DowntimePatient): string {
  if (p.allergyStatus !== 'Recorded') {
    return `<span class="${p.allergyStatus === 'NotRecorded' ? 'warn' : ''}">${escapeHtml(ALLERGY_TEXT[p.allergyStatus])}</span>`;
  }
  return `<strong class="allergy">ALLERGY:</strong> ${p.allergies
    .map(
      (a) =>
        `${escapeHtml(a.allergen)} (${escapeHtml(humanize(a.severity))}${a.reaction ? `, ${escapeHtml(a.reaction)}` : ''})`,
    )
    .join('; ')}`;
}

function medicines(p: DowntimePatient): string {
  if (p.medications.length === 0) {
    return '<p class="muted">No active medication orders.</p>';
  }
  const rows = p.medications
    .map((m) => {
      const flags = [
        m.isControlledDrug ? 'CD' : null,
        m.isHighAlert ? 'High alert' : null,
        m.dosesHeld ? 'Held: awaiting doctor' : null,
      ]
        .filter(Boolean)
        .join(' · ');
      const due = m.isAsNeeded
        ? 'As needed'
        : m.dueTimes.length === 0
          ? '—'
          : m.dueTimes.map((t) => formatTime(t)).join(', ');
      return `<tr><td>${escapeHtml(m.drugName)}${flags ? ` <span class="flag">${escapeHtml(flags)}</span>` : ''}</td><td>${escapeHtml(m.dose)}</td>
        <td>${escapeHtml(m.route ?? '')}</td><td>${escapeHtml(m.frequency)}</td><td>${escapeHtml(due)}</td><td class="sign"></td></tr>`;
    })
    .join('');
  return `<table class="meds"><thead><tr><th>Medicine</th><th>Dose</th><th>Route</th><th>Frequency</th><th>Due (next 8 h)</th><th>Given by / time</th></tr></thead><tbody>${rows}</tbody></table>`;
}

function patientBlock(p: DowntimePatient): string {
  const news2 = p.news2
    ? `NEWS2 ${p.news2.score}${p.news2.isComplete ? '' : ' (incomplete)'} at ${formatTime(p.news2.recordedAt)}${
        p.news2.response === 'Routine'
          ? ''
          : ` — ${p.news2.response === 'Emergency' ? 'EMERGENCY RESPONSE' : 'URGENT REVIEW'}`
      }`
    : 'NEWS2: no observations this stay';
  const tasks = p.pendingTasks.length
    ? `<ul>${p.pendingTasks.map((t) => `<li>${escapeHtml(t)}</li>`).join('')}</ul>`
    : '<p class="muted">None.</p>';
  const plan = p.carePlan.length
    ? `<ul>${p.carePlan
        .map(
          (c) =>
            `<li><strong>${escapeHtml(c.title)}</strong>: ${escapeHtml(c.goal)}. ${c.interventions
              .map((i) => `${escapeHtml(i.text)} (${escapeHtml(i.frequency)})`)
              .join('; ')}</li>`,
        )
        .join('')}</ul>`
    : '';
  return `<section class="patient">
    <div class="head"><span class="bed">${escapeHtml(p.bedLabel ?? '—')}</span>
      <span class="name">${escapeHtml(p.patientName)}</span>
      <span class="muted">${escapeHtml(p.uhid)} · ${escapeHtml(p.age ?? '—')}/${escapeHtml(p.gender.charAt(0))} · ${escapeHtml(p.admissionNumber)} · day ${p.dayOfStay}${
        p.consultantName ? ` · ${escapeHtml(p.consultantName)}` : ''
      }${p.isMlc ? ' · MLC' : ''}</span></div>
    ${p.provisionalDiagnosis ? `<div>Diagnosis: ${escapeHtml(p.provisionalDiagnosis)}</div>` : ''}
    <div>${allergies(p)}</div>
    ${p.isolation ? `<div class="warn">Isolation: ${escapeHtml(p.isolation)}</div>` : ''}
    <div class="${p.news2 && p.news2.response !== 'Routine' ? 'warn' : ''}">${escapeHtml(news2)}</div>
    ${medicines(p)}
    <div class="tasks"><strong>Pending tasks</strong>${tasks}</div>
    ${plan ? `<div class="tasks"><strong>Care plan</strong>${plan}</div>` : ''}
  </section>`;
}

function wardBlock(ward: DowntimeWard): string {
  const c = ward.counts;
  return `<h2>${escapeHtml(ward.name)} <span class="muted">(${escapeHtml(WARD_TYPE_LABEL[ward.type] ?? ward.type)})</span></h2>
    <p class="census">Census: ${c.occupied} occupied of ${c.total} beds · ${c.vacant} vacant · ${c.reserved} reserved · ${c.cleaning} cleaning · ${
      c.blocked + c.maintenance
    } out of service</p>
    ${ward.patients.length ? ward.patients.map(patientBlock).join('') : '<p class="muted">No patients on this ward.</p>'}`;
}

/**
 * The body of the pre-downtime ward print: per ward the census, and per patient the allergies, the active medicines with
 * the doses due in the next 8 hours (a column to sign as each is given on paper), NEWS2, pending tasks and the care plan.
 * Every value is escaped. Exported for the test.
 */
export function downtimeBody(pack: DowntimeWardPack): string {
  return `<h1>Downtime ward sheet</h1>
    <p class="muted">Printed ${escapeHtml(formatDateTime(pack.generatedAt))}${pack.printedByName ? ` by ${escapeHtml(pack.printedByName)}` : ''} ·
    medicines due until ${escapeHtml(formatDateTime(pack.windowEndsAt))}. Keep with the paper MAR; enter what is done here when HC360 is back.</p>
    ${pack.wards.map((w, i) => `<div class="${i > 0 ? 'ward page' : 'ward'}">${wardBlock(w)}</div>`).join('')}`;
}

/** Prints the ward sheet (or the whole hospital's pack) on A4 under the letterhead ("Other documents" template). */
export async function printDowntimePack(pack: DowntimeWardPack, letterhead: Letterhead): Promise<void> {
  const template = templateFor(letterhead, 'other');
  const styles = `
    h1 { font-size: 15pt; }
    h2 { font-size: 13pt; margin-top: 8px; }
    .page { page-break-before: always; }
    .census { margin: 2px 0 6px; }
    .patient { border: 1px solid #999; border-radius: 3px; padding: 5px 6px; margin-bottom: 6px; page-break-inside: avoid; }
    .head { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; }
    .bed { font-weight: 700; font-size: 12pt; min-width: 14mm; }
    .name { font-weight: 700; }
    .allergy { color: #000; }
    .warn { font-weight: 700; }
    .flag { font-size: 8pt; border: 1px solid #555; padding: 0 2px; }
    .meds { margin-top: 4px; font-size: 9pt; }
    .meds th, .meds td { border: 1px solid #bbb; }
    .meds .sign { width: 30mm; }
    .tasks ul { margin: 2px 0; padding-left: 16px; }`;
  const title = pack.wards.length === 1 ? `Downtime sheet — ${pack.wards[0]?.name ?? ''}` : 'Downtime pack — all wards';
  await printHtml(
    printDocument(`${letterheadHtml(letterhead, template)}${downtimeBody(pack)}`, {
      title,
      pageSize: 'A4',
      styles,
      template,
    }),
    {
      pageSize: 'A4',
    },
  );
}
