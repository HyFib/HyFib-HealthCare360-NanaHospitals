import { HOSPITAL_TIME_ZONE } from '@hc360/core';
import type { MessageKey } from '@/i18n';

const hourOf = new Intl.DateTimeFormat('en-GB', { timeZone: HOSPITAL_TIME_ZONE, hour: '2-digit', hourCycle: 'h23' });

/** "Good morning / afternoon / evening" by the hospital's clock (IST), whatever the phone's time zone. */
export function greetingKey(nowMs: number): MessageKey {
  const hour = Number(hourOf.format(nowMs));
  if (hour >= 4 && hour < 12) {
    return 'home.greeting.morning';
  }
  return hour >= 12 && hour < 17 ? 'home.greeting.afternoon' : 'home.greeting.evening';
}
