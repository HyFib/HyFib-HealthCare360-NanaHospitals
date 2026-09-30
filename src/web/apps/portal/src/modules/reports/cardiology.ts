import type { MessageKey, Translate } from '@/i18n';
import type { CardiologyReportView } from '@/lib/types';

export interface Measurement {
  label: string;
  value: string;
}

/** Measurements of a cardiology study as label/value text in the patient's language (empty ones left out). */
export function cardiologyMeasurements(report: CardiologyReportView, t: Translate): Measurement[] {
  const n = (value: number | null | undefined, unit: MessageKey) =>
    value === null || value === undefined ? null : t(unit, { value });
  const items: [MessageKey, string | number | null | undefined][] = [];
  if (report.ecg) {
    const e = report.ecg;
    items.push(
      ['card.ecg.heartRate', n(e.heartRate, 'unit.bpm')],
      ['card.ecg.rhythm', e.rhythm],
      ['card.ecg.pr', n(e.prInterval, 'unit.ms')],
      ['card.ecg.qrs', n(e.qrsDuration, 'unit.ms')],
      ['card.ecg.qt', n(e.qtInterval, 'unit.ms')],
      ['card.ecg.qtc', n(e.qtcInterval, 'unit.ms')],
      ['card.ecg.axis', e.axis],
      ['card.ecg.st', e.stTChanges],
      ['card.ecg.interpretation', e.interpretation],
    );
  }
  if (report.echo) {
    const e = report.echo;
    items.push(
      ['card.echo.lvef', n(e.lvef, 'unit.percent')],
      ['card.echo.lv', e.leftVentricle],
      ['card.echo.rv', e.rightVentricle],
      ['card.echo.la', e.leftAtrium],
      ['card.echo.ra', e.rightAtrium],
      ['card.echo.valves', e.valves],
      ['card.echo.rwma', e.rwma],
      ['card.echo.diastolic', e.diastolicFunction],
      ['card.echo.pericardium', e.pericardium],
      ['card.echo.conclusion', e.conclusion],
    );
  }
  if (report.tmt) {
    const e = report.tmt;
    items.push(
      ['card.tmt.protocol', e.protocol],
      [
        'card.tmt.duration',
        e.durationSeconds === null
          ? null
          : t('unit.minutes', { value: Math.round((e.durationSeconds / 60) * 10) / 10 }),
      ],
      ['card.tmt.mets', e.mets],
      ['card.tmt.maxHr', n(e.maxHeartRate, 'unit.bpm')],
      ['card.tmt.target', n(e.targetHeartRatePercent, 'unit.percent')],
      ['card.tmt.bp', e.bpResponse],
      ['card.tmt.symptoms', e.symptoms],
      ['card.tmt.st', e.stChanges],
      ['card.tmt.stopReason', e.reasonForTermination],
      ['card.tmt.result', e.result ? t(TMT_RESULT[e.result]) : null],
    );
  }
  if (report.holter) {
    const e = report.holter;
    items.push(
      ['card.holter.duration', n(e.durationHours, 'unit.hours')],
      ['card.holter.minHr', n(e.minHeartRate, 'unit.bpm')],
      ['card.holter.meanHr', n(e.meanHeartRate, 'unit.bpm')],
      ['card.holter.maxHr', n(e.maxHeartRate, 'unit.bpm')],
      ['card.holter.ve', e.ventricularEctopics],
      ['card.holter.sve', e.supraventricularEctopics],
      ['card.holter.pauses', e.pauses],
      ['card.holter.longestPause', e.longestPause],
      ['card.holter.arrhythmias', e.arrhythmias],
      ['card.holter.st', e.stChanges],
    );
  }
  if (report.abpm) {
    const e = report.abpm;
    const bp = (systolic: number | null, diastolic: number | null) =>
      systolic === null || diastolic === null ? null : t('unit.mmHg', { value: `${systolic}/${diastolic}` });
    items.push(
      ['card.abpm.day', bp(e.daytimeMeanSystolic, e.daytimeMeanDiastolic)],
      ['card.abpm.night', bp(e.nighttimeMeanSystolic, e.nighttimeMeanDiastolic)],
      ['card.abpm.dipping', e.dippingPattern],
    );
  }
  return items
    .filter(([, value]) => value !== null && value !== undefined && value !== '')
    .map(([key, value]) => ({ label: t(key), value: String(value) }));
}

const TMT_RESULT: Record<'Negative' | 'Positive' | 'Inconclusive', MessageKey> = {
  Negative: 'card.tmt.result.Negative',
  Positive: 'card.tmt.result.Positive',
  Inconclusive: 'card.tmt.result.Inconclusive',
};
