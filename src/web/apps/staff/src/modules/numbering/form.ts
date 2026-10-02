import { z } from 'zod';
import type { NumberSeriesItem, NumberSeriesList, SaveNumberSeriesRequest } from './api';
import { checkChange, otherFormats, type FormatChange, type OtherFormat } from './numberFormat';

/** The editor of one series: its format, when it starts again, and optionally the next number to issue. */
const formFields = z.object({
  format: z.string(),
  resetPolicy: z.enum(['Never', 'Yearly', 'Monthly', 'Daily']),
  startAt: z.number().int('Enter a whole number.').nullable(),
});

export type NumberSeriesForm = z.infer<typeof formFields>;

/** What the rules need to know about the hospital besides the series itself (from the list). */
export interface EditorContext {
  facilityCount: number;
  longestFacilityCode: number;
  otherFormats: OtherFormat[];
}

export function editorContext(list: NumberSeriesList, key: string): EditorContext {
  return {
    facilityCount: list.facilityCount,
    longestFacilityCode: list.longestFacilityCode,
    otherFormats: otherFormats(list.series, key),
  };
}

export function toForm(series: NumberSeriesItem): NumberSeriesForm {
  return { format: series.format, resetPolicy: series.resetPolicy, startAt: null };
}

/** The change the form describes, as the rules take it. The series is as it was when the editor opened. */
export function toChange(
  series: NumberSeriesItem,
  context: EditorContext,
  values: Pick<NumberSeriesForm, 'format' | 'resetPolicy' | 'startAt'>,
): FormatChange {
  return {
    name: series.name,
    perFacility: series.scope === 'facility',
    isLocked: series.isLocked,
    maxLength: series.maxLength,
    defaultFormat: series.defaultFormat,
    format: series.isLocked ? series.format : values.format.trim(),
    resetPolicy: series.isLocked ? series.resetPolicy : values.resetPolicy,
    startAt: values.startAt,
    currentFormat: series.format,
    currentResetPolicy: series.resetPolicy,
    lastNumber: series.lastNumber,
    facilityCount: context.facilityCount,
    longestFacilityCode: context.longestFacilityCode,
    otherFormats: context.otherFormats,
    facilityCode: series.facilityCode,
  };
}

/** The form's rules: the server's (see numberFormat.ts), on the fields the server names. */
export function numberSeriesFormSchema(series: NumberSeriesItem, context: EditorContext) {
  return formFields.superRefine((values, ctx) => {
    for (const [field, messages] of Object.entries(checkChange(toChange(series, context, values)))) {
      if (messages?.length) {
        ctx.addIssue({ code: 'custom', path: [field], message: messages[0] });
      }
    }
  });
}

/**
 * The save request. `expected…` are the values the editor started from, so a change someone else saved meanwhile is
 * not overwritten. A locked series sends its format and reset unchanged (only the next number can change).
 */
export function toSaveRequest(values: NumberSeriesForm, series: NumberSeriesItem): SaveNumberSeriesRequest {
  const change = toChange(series, { facilityCount: 1, longestFacilityCode: 0, otherFormats: [] }, values);
  return {
    format: change.format,
    resetPolicy: change.resetPolicy,
    startAt: values.startAt,
    expectedFormat: series.format,
    expectedResetPolicy: series.resetPolicy,
  };
}
