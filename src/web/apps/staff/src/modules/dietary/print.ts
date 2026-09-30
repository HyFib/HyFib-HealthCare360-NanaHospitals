import { escapeHtml, formatDateOnly, printDocument, printHtml } from '@hc360/core';
import type { ProductionSummary, WardMealPlan } from './api';
import { MEAL_SLOT_LABEL, MEAL_SLOTS, PREFERENCE_SHORT } from './rules';
import { formatTimeOfDay } from '@/modules/inpatient/time';

const TABLE_STYLES = `
  h1 { font-size: 14pt; }
  h2 { font-size: 11pt; margin-top: 10px; }
  th { border-bottom: 1px solid #888; font-size: 9pt; }
  td { border-bottom: 1px solid #ddd; font-size: 9.5pt; }
  .num { text-align: right; font-variant-numeric: tabular-nums; }
  .allergy { color: #b10e1c; font-weight: 700; }
  .hospital { font-weight: 700; color: #045E80; }`;

/** A4 cooking list for the kitchen: per round and diet, how many veg, non-veg, egg and Jain meals, with special notes. */
export async function printProductionSheet(summary: ProductionSummary, facilityName: string): Promise<void> {
  const sections = MEAL_SLOTS.map((slot) => {
    const lines = summary.lines.filter((l) => l.slot === slot);
    if (lines.length === 0) {
      return '';
    }
    const rows = lines
      .map(
        (l) => `<tr>
          <td>${escapeHtml(l.dietName ?? l.dietCode)}</td>
          <td class="num">${l.vegetarian || ''}</td><td class="num">${l.nonVegetarian || ''}</td><td class="num">${l.eggetarian || ''}</td><td class="num">${l.jain || ''}</td>
          <td class="num"><strong>${l.total}</strong></td>
          <td>${l.specialNotes.map((n) => escapeHtml(n)).join('<br/>')}</td>
        </tr>`,
      )
      .join('');
    const total = lines.reduce((sum, l) => sum + l.total, 0);
    return `<h2>${escapeHtml(MEAL_SLOT_LABEL[slot])} · ${escapeHtml(formatTimeOfDay(lines[0]?.serviceTime))} · ${total} meals</h2>
      <table><thead><tr><th>Diet</th><th class="num">Veg</th><th class="num">Non-veg</th><th class="num">Egg</th><th class="num">Jain</th><th class="num">Total</th><th>Special notes</th></tr></thead><tbody>${rows}</tbody></table>`;
  }).join('');
  const body = `<div class="hospital">${escapeHtml(facilityName)} · Kitchen</div>
    <h1>Production list for ${escapeHtml(formatDateOnly(summary.date))} (${summary.totalMeals} meals)</h1>${sections || '<p>No meals planned.</p>'}`;
  await printHtml(printDocument(body, { title: `Kitchen ${summary.date}`, pageSize: 'A4', styles: TABLE_STYLES }), {
    pageSize: 'A4',
  });
}

/** A4 delivery list for one ward and round: bed, patient, diet, restrictions and allergies, with a tick column. */
export async function printWardMealList(plan: WardMealPlan, date: string, facilityName: string): Promise<void> {
  const rows = plan.items
    .filter((m) => m.status !== 'Cancelled')
    .map(
      (m) => `<tr>
        <td>${escapeHtml(MEAL_SLOT_LABEL[m.slot])}</td>
        <td><strong>${escapeHtml(m.bedLabel ?? '')}</strong></td>
        <td>${escapeHtml(m.patientName ?? '')}</td>
        <td>${escapeHtml(m.dietName ?? m.dietCode)} (${escapeHtml(PREFERENCE_SHORT[m.preference])})</td>
        <td>${escapeHtml(m.restrictions.join(', '))}${m.instructions ? `<br/>${escapeHtml(m.instructions)}` : ''}</td>
        <td class="allergy">${escapeHtml(m.allergyNotes ?? '')}</td>
        <td style="width:14mm"></td>
      </tr>`,
    )
    .join('');
  const body = `<div class="hospital">${escapeHtml(facilityName)} · Dietary</div>
    <h1>${escapeHtml(plan.wardName ?? 'Ward')} · meals for ${escapeHtml(formatDateOnly(date))}</h1>
    <table><thead><tr><th>Round</th><th>Bed</th><th>Patient</th><th>Diet</th><th>Restrictions</th><th>Allergies</th><th>Served</th></tr></thead><tbody>${rows}</tbody></table>`;
  await printHtml(
    printDocument(body, { title: `Meals ${plan.wardName ?? ''} ${date}`, pageSize: 'A4', styles: TABLE_STYLES }),
    { pageSize: 'A4' },
  );
}
