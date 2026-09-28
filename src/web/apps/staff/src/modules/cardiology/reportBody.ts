/**
 * Cardiology report content (pure; unit-tested): the measurement rows of the study type (shared by the on-screen view
 * and the print) and the printed body. Empty measurements are left out rather than printed as blanks.
 */
import { escapeHtml, formatDateTime, genderShort } from '@hc360/core';
import { paragraphs } from '@/modules/lab/common/reportHtml';
import type { AbpmFindings, CardiologyReportView, CardiologyStudyType, EcgFindings, EchoFindings, HolterFindings, TmtFindings } from './api';
import { studyTypeLongLabel, tmtResultLabel } from './labels';
import { formatDuration } from './measurements';

export interface MeasurementSet {
  ecg?: EcgFindings | null;
  echo?: EchoFindings | null;
  tmt?: TmtFindings | null;
  holter?: HolterFindings | null;
  abpm?: AbpmFindings | null;
}

type Row = [label: string, value: string];

function unit(value: number | null | undefined, suffix: string): string | null {
  return value === null || value === undefined ? null : `${value.toLocaleString('en-IN')} ${suffix}`.trim();
}

function pair(a: number | null | undefined, b: number | null | undefined, suffix: string): string | null {
  return a === null || a === undefined || b === null || b === undefined ? null : `${a}/${b} ${suffix}`;
}

function compact(rows: [string, string | null | undefined][]): Row[] {
  return rows.filter((row): row is Row => typeof row[1] === 'string' && row[1].trim().length > 0);
}

/** Label/value rows of the measurements that belong to the study type. */
export function measurementRows(type: CardiologyStudyType, set: MeasurementSet): Row[] {
  switch (type) {
    case 'Ecg': {
      const e = set.ecg;
      return e
        ? compact([
            ['Heart rate', unit(e.heartRate, '/min')],
            ['Rhythm', e.rhythm],
            ['PR interval', unit(e.prInterval, 'ms')],
            ['QRS duration', unit(e.qrsDuration, 'ms')],
            ['QT interval', unit(e.qtInterval, 'ms')],
            ['QTc', unit(e.qtcInterval, 'ms')],
            ['Axis', e.axis],
            ['ST-T changes', e.stTChanges],
            ['Interpretation', e.interpretation],
          ])
        : [];
    }
    case 'Echo': {
      const e = set.echo;
      return e
        ? compact([
            ['LV ejection fraction', unit(e.lvef, '%')],
            ['Left ventricle', e.leftVentricle],
            ['Right ventricle', e.rightVentricle],
            ['Left atrium', e.leftAtrium],
            ['Right atrium', e.rightAtrium],
            ['Valves', e.valves],
            ['Regional wall motion', e.rwma],
            ['Diastolic function', e.diastolicFunction],
            ['Pericardium', e.pericardium],
            ['Echo conclusion', e.conclusion],
          ])
        : [];
    }
    case 'Tmt': {
      const t = set.tmt;
      return t
        ? compact([
            ['Protocol', t.protocol],
            ['Exercise duration', t.durationSeconds === null ? null : `${formatDuration(t.durationSeconds)} min`],
            ['Workload', unit(t.mets, 'METs')],
            ['Maximum heart rate', unit(t.maxHeartRate, '/min')],
            ['Target heart rate reached', unit(t.targetHeartRatePercent, '%')],
            ['BP response', t.bpResponse],
            ['Symptoms', t.symptoms],
            ['ST changes', t.stChanges],
            ['Reason for stopping', t.reasonForTermination],
            ['Result', t.result ? tmtResultLabel[t.result] : null],
          ])
        : [];
    }
    case 'Holter': {
      const h = set.holter;
      return h
        ? compact([
            ['Recording', unit(h.durationHours, 'hours')],
            ['Heart rate (min / mean / max)', h.minHeartRate !== null || h.meanHeartRate !== null || h.maxHeartRate !== null ? `${h.minHeartRate ?? '—'} / ${h.meanHeartRate ?? '—'} / ${h.maxHeartRate ?? '—'} /min` : null],
            ['Ventricular ectopics', unit(h.ventricularEctopics, '')],
            ['Supraventricular ectopics', unit(h.supraventricularEctopics, '')],
            ['Pauses', unit(h.pauses, '')],
            ['Longest pause', h.longestPause],
            ['Arrhythmias', h.arrhythmias],
            ['ST changes', h.stChanges],
          ])
        : [];
    }
    case 'Abpm': {
      const a = set.abpm;
      return a
        ? compact([
            ['Daytime mean BP', pair(a.daytimeMeanSystolic, a.daytimeMeanDiastolic, 'mmHg')],
            ['Night-time mean BP', pair(a.nighttimeMeanSystolic, a.nighttimeMeanDiastolic, 'mmHg')],
            ['Dipping pattern', a.dippingPattern],
          ])
        : [];
    }
    default:
      return [];
  }
}

export function cardiologyReportBodyHtml(view: CardiologyReportView): string {
  const rows = measurementRows(view.studyType, view);
  const table = rows.length
    ? `<table><tbody>${rows.map(([label, value]) => `<tr><th style="width:35%">${escapeHtml(label)}</th><td>${escapeHtml(value)}</td></tr>`).join('')}</tbody></table>`
    : '';
  const conclusion = paragraphs(view.conclusion);
  const recommendations = paragraphs(view.recommendations);
  return `
    <h2>${escapeHtml(view.studyName)}</h2>
    <div class="small">${escapeHtml(studyTypeLongLabel[view.studyType])}</div>
    ${view.isCriticalFinding ? '<p class="crit">Critical finding: communicated to the treating doctor.</p>' : ''}
    ${table}
    ${conclusion ? `<h3>Conclusion</h3><div class="impression">${conclusion}</div>` : ''}
    ${recommendations ? `<h3>Recommendations</h3>${recommendations}` : ''}`;
}

export function cardiologyPatientRows(view: CardiologyReportView): [string, string][] {
  return [
    ['Patient', view.patient.name],
    ['Age / sex', `${view.patient.age ?? '—'} / ${genderShort[view.patient.gender]}`],
    ['UHID', view.patient.uhid],
    ['Accession no.', view.accessionNumber],
    ['Referred by', view.referredBy ?? 'Self'],
    ['Ordered', formatDateTime(view.orderedAt)],
    ['Recorded', view.acquiredAt ? formatDateTime(view.acquiredAt) : '—'],
    ['Reported', view.releasedAt ? formatDateTime(view.releasedAt) : view.verifiedAt ? formatDateTime(view.verifiedAt) : 'Not signed'],
  ];
}

export const CARDIOLOGY_PRINT_STYLES = `
  .impression { font-weight: 600; }
  .body table th { border-bottom: 1px solid #eee; font-weight: 600; color: #333; }
`;
