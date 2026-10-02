/**
 * Cardiology report content (pure; unit-tested): the measurement rows of the study type, as the report screen shows
 * them. Empty measurements are left out rather than shown as blanks. The printed report and its PDF are made by the
 * server, which lists the same rows.
 */
import type { AbpmFindings, CardiologyStudyType, EcgFindings, EchoFindings, HolterFindings, TmtFindings } from './api';
import { tmtResultLabel } from './labels';
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
            [
              'Heart rate (min / mean / max)',
              h.minHeartRate !== null || h.meanHeartRate !== null || h.maxHeartRate !== null
                ? `${h.minHeartRate ?? '—'} / ${h.meanHeartRate ?? '—'} / ${h.maxHeartRate ?? '—'} /min`
                : null,
            ],
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
