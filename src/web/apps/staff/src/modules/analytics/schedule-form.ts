import { z } from 'zod';
import type {
  ReportCatalogueItem,
  ReportInputs,
  SaveScheduleRequest,
  ScheduleDestination,
  ScheduleDto,
  ScheduleFrequency,
} from './api';
import { EXTRACT_DEFAULT_PERIOD, MAX_EXTRACT_NAME_LENGTH } from './labels';
import { defaultPeriodFor } from './params';

/**
 * The schedule form of scheduled e-mails and BI extracts, with the server's rules (SaveScheduleRequestValidator): an
 * e-mail goes daily, weekly or monthly to at least one address; an extract can also refresh hourly, is always a CSV
 * file and needs a file name instead of addresses.
 */

export const SCHEDULE_TIME = /^([01]\d|2[0-3]):[0-5]\d$/;
export const MAX_RECIPIENTS = 20;

const EXTRACT_NAME = /^[a-z0-9][a-z0-9-]*$/;
const WINDOWS_DEVICE = /^(con|prn|aux|nul|com[0-9]|lpt[0-9])$/;

/** A file name an extract may use (without ".csv"): lower-case letters, digits and dashes. */
export function isExtractName(value: string): boolean {
  return value.length <= MAX_EXTRACT_NAME_LENGTH && EXTRACT_NAME.test(value);
}

/** `con`, `nul`, `com1` and the like, which Windows opens as a device, not a file; the server refuses them too. */
export function isWindowsDeviceName(value: string): boolean {
  return WINDOWS_DEVICE.test(value);
}

/** What is typed into the file name, as the server keeps it: lower case, with spaces and underscores as dashes. */
export function normalizeExtractName(value: string): string {
  return value.toLowerCase().replace(/[\s_]+/g, '-');
}

export const scheduleFormSchema = z
  .object({
    destination: z.enum(['Email', 'ExtractFolder']),
    name: z.string().trim().min(1, 'Give the schedule a name.').max(100, 'Use at most 100 characters.'),
    reportId: z.string().min(1, 'Choose a report.'),
    format: z.enum(['Xlsx', 'Csv', 'Pdf']),
    frequency: z.enum(['Hourly', 'Daily', 'Weekly', 'Monthly']),
    time: z.string().regex(SCHEDULE_TIME, 'Enter the time as HH:mm (24-hour clock, IST).'),
    dayOfWeek: z.enum(['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']).nullable(),
    dayOfMonth: z.string().nullable(),
    recipients: z.array(z.string()).max(MAX_RECIPIENTS, `Send to at most ${MAX_RECIPIENTS} people.`),
    extractName: z.string(),
    isActive: z.boolean(),
  })
  .superRefine((value, ctx) => {
    if (value.frequency === 'Weekly' && !value.dayOfWeek) {
      ctx.addIssue({ code: 'custom', path: ['dayOfWeek'], message: 'Choose the day of the week.' });
    }
    if (value.frequency === 'Monthly' && !value.dayOfMonth) {
      ctx.addIssue({ code: 'custom', path: ['dayOfMonth'], message: 'Choose the day of the month.' });
    }
    if (value.destination === 'Email') {
      if (value.recipients.length === 0) {
        ctx.addIssue({ code: 'custom', path: ['recipients'], message: 'Add at least one e-mail address.' });
      }
      if (value.frequency === 'Hourly') {
        ctx.addIssue({ code: 'custom', path: ['frequency'], message: 'E-mails go daily, weekly or monthly.' });
      }
    } else if (!isExtractName(value.extractName.trim())) {
      ctx.addIssue({
        code: 'custom',
        path: ['extractName'],
        message: `Name the file with lower-case letters, digits and dashes (at most ${MAX_EXTRACT_NAME_LENGTH}), for example daily-revenue.`,
      });
    } else if (isWindowsDeviceName(value.extractName.trim())) {
      ctx.addIssue({
        code: 'custom',
        path: ['extractName'],
        message: 'Windows keeps this name for a device. Choose another, for example daily-revenue.',
      });
    }
  });

export type ScheduleForm = z.infer<typeof scheduleFormSchema>;

/** The form of a new schedule, or of `schedule` to change it. A new extract is named after its report. */
export function scheduleFormDefaults(
  schedule: ScheduleDto | null,
  destination: ScheduleDestination,
  report: ReportCatalogueItem | undefined,
): ScheduleForm {
  const extract = (schedule?.destination ?? destination) === 'ExtractFolder';
  return {
    destination: schedule?.destination ?? destination,
    name: schedule?.name ?? report?.title ?? '',
    reportId: report?.id ?? '',
    format: extract ? 'Csv' : (schedule?.format ?? 'Xlsx'),
    frequency: schedule?.frequency ?? 'Daily',
    time: schedule?.time ?? (extract ? '06:00' : '07:00'),
    dayOfWeek: schedule?.dayOfWeek ?? null,
    dayOfMonth: schedule?.dayOfMonth ? String(schedule.dayOfMonth) : null,
    recipients: schedule?.recipients ?? [],
    extractName: schedule?.extractName ?? (extract ? (report?.id ?? '') : ''),
    isActive: schedule?.isActive ?? true,
  };
}

/** The period a new schedule covers until one is chosen: the period that just ended for an e-mail, the year for an extract. */
export function defaultSchedulePeriod(destination: ScheduleDestination, frequency: ScheduleFrequency): string {
  return destination === 'ExtractFolder' ? EXTRACT_DEFAULT_PERIOD : defaultPeriodFor(frequency);
}

export function toSaveRequest(
  value: ScheduleForm,
  parameters: ReportInputs,
  version: number | null,
): SaveScheduleRequest {
  const extract = value.destination === 'ExtractFolder';
  return {
    name: value.name.trim(),
    reportId: value.reportId,
    parameters,
    format: extract ? 'Csv' : value.format,
    frequency: value.frequency,
    time: value.time,
    dayOfWeek: value.frequency === 'Weekly' ? value.dayOfWeek : null,
    dayOfMonth: value.frequency === 'Monthly' && value.dayOfMonth ? Number(value.dayOfMonth) : null,
    recipients: extract ? [] : value.recipients,
    isActive: value.isActive,
    version,
    destination: value.destination,
    extractName: extract ? value.extractName.trim() : null,
  };
}

/** A saved schedule as it is, paused or resumed. */
export function scheduleToRequest(schedule: ScheduleDto, isActive = schedule.isActive): SaveScheduleRequest {
  return {
    name: schedule.name,
    reportId: schedule.reportId,
    parameters: schedule.parameters,
    format: schedule.format,
    frequency: schedule.frequency,
    time: schedule.time,
    dayOfWeek: schedule.dayOfWeek,
    dayOfMonth: schedule.dayOfMonth,
    recipients: schedule.recipients,
    isActive,
    version: schedule.version,
    destination: schedule.destination,
    extractName: schedule.extractName,
  };
}
