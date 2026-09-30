/**
 * The breach record form: its schema mirrors SaveBreachRequestValidator (and the "not in the future" rule of
 * BreachService), and maps to and from the server's record. Pure so it is unit-tested.
 */
import { z } from 'zod';
import { fromIstInput, nowIstInput, toIstInput } from '@/modules/hr/shared/dates';
import type { BreachDetail, SaveBreachRequest } from './api';
import { BREACH_DATA_CATEGORIES, BREACH_NATURES, NOTICE_MAX_LENGTH } from './labels';

/** A few minutes of slack for workstation clocks that run fast. */
const CLOCK_SLACK_MS = 5 * 60_000;

function instant(value: string): number | null {
  const iso = fromIstInput(value);
  return iso ? Date.parse(iso) : null;
}

export const breachFormSchema = z
  .object({
    title: z.string().trim().min(1, 'Give the breach a short title.').max(200),
    description: z.string().trim().min(1, 'Describe what happened.').max(4000),
    awareAt: z.string().min(1, 'Enter when the hospital became aware of the breach.'),
    occurredFrom: z.string(),
    occurredTo: z.string(),
    nature: z.array(z.enum(BREACH_NATURES)),
    location: z.string().trim().max(500),
    dataCategories: z.array(z.enum(BREACH_DATA_CATEGORIES)),
    estimatedAffected: z
      .number({ error: 'Enter a number, or leave it empty.' })
      .int('Enter a whole number.')
      .min(0, 'The number cannot be negative.')
      .nullable(),
    consequences: z.string().trim().max(4000),
    mitigation: z.string().trim().max(4000),
    principalSteps: z.string().trim().max(4000),
    cause: z.string().trim().max(4000),
    /** Optional (rule 7(2)(b)(iii) asks for findings "if any"); older local drafts do not have it. */
    findings: z.string().trim().max(4000).optional(),
    remediation: z.string().trim().max(4000),
    principalNotice: z
      .string()
      .trim()
      .max(NOTICE_MAX_LENGTH, `Keep the SMS notice to ${NOTICE_MAX_LENGTH} characters.`),
  })
  .superRefine((v, ctx) => {
    const now = Date.now() + CLOCK_SLACK_MS;
    const aware = instant(v.awareAt);
    if (v.awareAt && aware === null) {
      ctx.addIssue({ code: 'custom', path: ['awareAt'], message: 'Enter a date and time.' });
    } else if (aware !== null && aware > now) {
      ctx.addIssue({
        code: 'custom',
        path: ['awareAt'],
        message: 'The time the hospital became aware cannot be in the future.',
      });
    }
    const from = v.occurredFrom ? instant(v.occurredFrom) : null;
    const to = v.occurredTo ? instant(v.occurredTo) : null;
    if (from !== null && to !== null && to < from) {
      ctx.addIssue({ code: 'custom', path: ['occurredTo'], message: 'The end must be after the start.' });
    }
    if (from !== null && aware !== null && from > aware) {
      ctx.addIssue({
        code: 'custom',
        path: ['occurredFrom'],
        message: 'It cannot have started after the hospital became aware of it.',
      });
    }
  });

export type BreachFormValues = z.infer<typeof breachFormSchema>;

export function newBreachDefaults(): BreachFormValues {
  return {
    title: '',
    description: '',
    awareAt: nowIstInput(),
    occurredFrom: '',
    occurredTo: '',
    nature: [],
    location: '',
    dataCategories: [],
    estimatedAffected: null,
    consequences: '',
    mitigation: '',
    principalSteps: '',
    cause: '',
    findings: '',
    remediation: '',
    principalNotice: '',
  };
}

export function fromBreachDetail(b: BreachDetail): BreachFormValues {
  return {
    title: b.title,
    description: b.description,
    awareAt: toIstInput(b.awareAt),
    occurredFrom: toIstInput(b.occurredFrom),
    occurredTo: toIstInput(b.occurredTo),
    nature: [...b.nature],
    location: b.location ?? '',
    dataCategories: [...b.dataCategories],
    estimatedAffected: b.estimatedAffected,
    consequences: b.consequences ?? '',
    mitigation: b.mitigation ?? '',
    principalSteps: b.principalSteps ?? '',
    cause: b.cause ?? '',
    findings: b.findings ?? '',
    remediation: b.remediation ?? '',
    principalNotice: b.principalNotice ?? '',
  };
}

const clean = (value: string): string | null => {
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
};

/** The request for the server; `version` is required for changes to an existing breach. */
export function toSaveBreachRequest(v: BreachFormValues, version: number | null): SaveBreachRequest {
  const awareAt = fromIstInput(v.awareAt);
  if (!awareAt) {
    throw new Error('The time the hospital became aware is required.');
  }
  return {
    title: v.title.trim(),
    description: v.description.trim(),
    awareAt,
    occurredFrom: v.occurredFrom ? fromIstInput(v.occurredFrom) : null,
    occurredTo: v.occurredTo ? fromIstInput(v.occurredTo) : null,
    nature: [...new Set(v.nature)],
    location: clean(v.location),
    dataCategories: [...new Set(v.dataCategories)],
    estimatedAffected: v.estimatedAffected,
    consequences: clean(v.consequences),
    mitigation: clean(v.mitigation),
    principalSteps: clean(v.principalSteps),
    cause: clean(v.cause),
    findings: clean(v.findings ?? ''),
    remediation: clean(v.remediation),
    principalNotice: clean(v.principalNotice),
    version,
  };
}

export type GapFields = Pick<
  BreachFormValues,
  'description' | 'consequences' | 'mitigation' | 'cause' | 'remediation' | 'principalSteps' | 'principalNotice'
>;

/** Which parts of the form the Board's report and the notice to people still need (Breach.MissingFor…). */
export function formGaps(v: GapFields): { report: string[]; notice: string[] } {
  const empty = (value: string) => !value.trim();
  const report: string[] = [];
  const notice: string[] = [];
  if (empty(v.description)) {
    report.push('what happened');
    notice.push('what happened');
  }
  if (empty(v.consequences)) {
    report.push('the likely consequences');
    notice.push('the likely consequences');
  }
  if (empty(v.mitigation)) {
    report.push('what is being done to reduce the risk');
    notice.push('what is being done to reduce the risk');
  }
  if (empty(v.cause)) {
    report.push('the cause');
  }
  if (empty(v.remediation)) {
    report.push('what stops it happening again');
  }
  if (empty(v.principalSteps)) {
    notice.push('what people can do to protect themselves');
  }
  if (empty(v.principalNotice)) {
    notice.push('the short notice for SMS');
  }
  return { report, notice };
}
