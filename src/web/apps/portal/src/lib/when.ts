import type { Translate } from '@/i18n';
import { daysBetween, istDateOf } from '@/i18n/format';
import type { Formatters } from '@/i18n/useFormat';

/** "Today", "Tomorrow" or "Sat, 27 Sept" for a calendar date, relative to `now` (IST). */
export function describeDay(day: string, f: Formatters, t: Translate, now: number): string {
  const diff = daysBetween(istDateOf(now), day);
  if (diff === 0) {
    return t('common.today');
  }
  if (diff === 1) {
    return t('common.tomorrow');
  }
  return `${f.weekday(day)}, ${f.dayMonth(day)}`;
}

/** "Today, 10:30 am" / "Tomorrow, 9:00 am" / "Sat, 27 Sept, 10:30 am" for an instant (IST). */
export function describeWhen(iso: string, f: Formatters, t: Translate, now: number): string {
  const day = istDateOf(iso);
  const diff = daysBetween(istDateOf(now), day);
  if (diff < 0 || diff > 6) {
    return f.dateTime(iso);
  }
  return `${describeDay(day, f, t, now)}, ${f.time(iso)}`;
}
