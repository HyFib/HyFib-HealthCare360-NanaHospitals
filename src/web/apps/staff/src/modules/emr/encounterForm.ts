import { z } from 'zod';

/**
 * The encounter note form (SOAP, follow-up and referral): validation mirroring the server, mapping to and from the
 * API, template application and follow-up date helpers. Pure and unit-tested.
 */

export interface EncounterContent {
  chiefComplaint: string | null;
  historyOfPresentIllness: string | null;
  examination: string | null;
  assessment: string | null;
  plan: string | null;
  followUpDate: string | null;
  followUpAdvice: string | null;
  referralDepartmentId: string | null;
  referralPractitionerId: string | null;
  referralExternal: string | null;
  referralReason: string | null;
}

/** Minimum length of an amendment reason (server rule). */
export const MIN_AMENDMENT_REASON = 10;

/** yyyy-MM-dd + days (calendar arithmetic, no time zone). */
export function addDaysIso(isoDate: string, days: number): string {
  const [y, m, d] = isoDate.split('-').map(Number);
  const date = new Date(Date.UTC(y ?? 1970, (m ?? 1) - 1, d ?? 1) + days * 86_400_000);
  return date.toISOString().slice(0, 10);
}

export function daysBetween(fromIso: string, toIso: string): number {
  const parse = (iso: string) => {
    const [y, m, d] = iso.split('-').map(Number);
    return Date.UTC(y ?? 1970, (m ?? 1) - 1, d ?? 1);
  };
  return Math.round((parse(toIso) - parse(fromIso)) / 86_400_000);
}

export const FOLLOW_UP_PRESETS = [
  { label: '3 days', days: 3 },
  { label: '1 week', days: 7 },
  { label: '2 weeks', days: 14 },
  { label: '1 month', days: 30 },
  { label: '3 months', days: 90 },
] as const;

/** Schema bound to "today" (IST) so the follow-up window matches the server: today up to two years ahead. */
export function encounterFormSchema(today: string) {
  const latest = addDaysIso(today, 730);
  return z
    .object({
      chiefComplaint: z.string().max(1000, 'Keep the chief complaint under 1,000 characters.'),
      historyOfPresentIllness: z.string().max(8000, 'Keep the history under 8,000 characters.'),
      examination: z.string().max(8000, 'Keep the examination under 8,000 characters.'),
      assessment: z.string().max(4000, 'Keep the assessment under 4,000 characters.'),
      plan: z.string().max(8000, 'Keep the plan under 8,000 characters.'),
      followUpDate: z.string().nullable(),
      followUpAdvice: z.string().max(1000, 'Keep the follow-up advice under 1,000 characters.'),
      referralDepartmentId: z.string().nullable(),
      referralPractitionerId: z.string().nullable(),
      referralExternal: z.string().max(300, 'Keep this under 300 characters.'),
      referralReason: z.string().max(1000, 'Keep the reason under 1,000 characters.'),
    })
    .superRefine((values, ctx) => {
      if (values.followUpDate && (values.followUpDate < today || values.followUpDate > latest)) {
        ctx.addIssue({ code: 'custom', path: ['followUpDate'], message: 'Choose a follow-up date between today and two years ahead.' });
      }
      if (hasReferral(values) && !values.referralReason.trim()) {
        ctx.addIssue({ code: 'custom', path: ['referralReason'], message: 'Give the reason for the referral.' });
      }
    });
}

export type EncounterFormValues = z.infer<ReturnType<typeof encounterFormSchema>>;

export const emptyEncounterForm: EncounterFormValues = {
  chiefComplaint: '',
  historyOfPresentIllness: '',
  examination: '',
  assessment: '',
  plan: '',
  followUpDate: null,
  followUpAdvice: '',
  referralDepartmentId: null,
  referralPractitionerId: null,
  referralExternal: '',
  referralReason: '',
};

export function hasReferral(values: Pick<EncounterFormValues, 'referralDepartmentId' | 'referralPractitionerId' | 'referralExternal'>): boolean {
  return Boolean(values.referralDepartmentId || values.referralPractitionerId || values.referralExternal.trim());
}

export function toFormValues(content: Partial<EncounterContent>): EncounterFormValues {
  return {
    chiefComplaint: content.chiefComplaint ?? '',
    historyOfPresentIllness: content.historyOfPresentIllness ?? '',
    examination: content.examination ?? '',
    assessment: content.assessment ?? '',
    plan: content.plan ?? '',
    followUpDate: content.followUpDate ?? null,
    followUpAdvice: content.followUpAdvice ?? '',
    referralDepartmentId: content.referralDepartmentId ?? null,
    referralPractitionerId: content.referralPractitionerId ?? null,
    referralExternal: content.referralExternal ?? '',
    referralReason: content.referralReason ?? '',
  };
}

function clean(value: string): string | null {
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
}

/** The request body fields (empty text → null; the referral reason is dropped when nothing is referred). */
export function toContent(values: EncounterFormValues): EncounterContent {
  const referral = hasReferral(values);
  return {
    chiefComplaint: clean(values.chiefComplaint),
    historyOfPresentIllness: clean(values.historyOfPresentIllness),
    examination: clean(values.examination),
    assessment: clean(values.assessment),
    plan: clean(values.plan),
    followUpDate: values.followUpDate || null,
    followUpAdvice: clean(values.followUpAdvice),
    referralDepartmentId: values.referralDepartmentId || null,
    referralPractitionerId: values.referralPractitionerId || null,
    referralExternal: clean(values.referralExternal),
    referralReason: referral ? clean(values.referralReason) : null,
  };
}

/** A signed encounter is a medico-legal record: it needs at least the chief complaint or the assessment. */
export function isSignable(values: Pick<EncounterFormValues, 'chiefComplaint' | 'assessment'>): boolean {
  return Boolean(values.chiefComplaint.trim() || values.assessment.trim());
}

export interface TemplateContent {
  chiefComplaint: string | null;
  historyOfPresentIllness: string | null;
  examination: string | null;
  assessment: string | null;
  plan: string | null;
  followUpAdvice: string | null;
  followUpDays: number | null;
}

const TEMPLATE_FIELDS = ['chiefComplaint', 'historyOfPresentIllness', 'examination', 'assessment', 'plan', 'followUpAdvice'] as const;

/** Fields the template would overwrite (non-empty in the note and different in the template). */
export function templateConflicts(values: EncounterFormValues, template: TemplateContent): string[] {
  return TEMPLATE_FIELDS.filter((field) => {
    const incoming = template[field]?.trim();
    const current = values[field].trim();
    return Boolean(incoming && current && current !== incoming);
  });
}

/**
 * Applies a speciality template. `fill-empty` keeps everything the doctor already typed; `replace` overwrites the
 * fields the template defines. The follow-up date is set from the template's interval only when none is chosen.
 */
export function applyTemplate(values: EncounterFormValues, template: TemplateContent, mode: 'fill-empty' | 'replace', today: string): EncounterFormValues {
  const next = { ...values };
  for (const field of TEMPLATE_FIELDS) {
    const incoming = template[field]?.trim();
    if (incoming && (mode === 'replace' || !values[field].trim())) {
      next[field] = incoming;
    }
  }
  if (!values.followUpDate && template.followUpDays && template.followUpDays > 0) {
    next.followUpDate = addDaysIso(today, template.followUpDays);
  }
  return next;
}

/** Adds a common complaint to the chief complaint ("Fever" + "Cough" → "Fever, cough"), ignoring repeats. */
export function appendComplaint(current: string, complaint: string): string {
  const addition = complaint.trim();
  if (!addition) {
    return current;
  }
  const existing = current
    .split(/[,;]/)
    .map((part) => part.trim().toLowerCase())
    .filter(Boolean);
  if (existing.includes(addition.toLowerCase())) {
    return current;
  }
  const base = current.trim().replace(/[,;]\s*$/, '');
  return base ? `${base}, ${addition.charAt(0).toLowerCase()}${addition.slice(1)}` : addition;
}

/** Appends dictated or picked text to a note, on a new line when the note already has text. */
export function appendText(current: string, addition: string): string {
  const base = current.replace(/\s+$/, '');
  return base ? `${base}\n${addition}` : addition;
}

/** Labels of the note fields, for conflict messages and printing. */
export const encounterFieldLabel: Record<(typeof TEMPLATE_FIELDS)[number], string> = {
  chiefComplaint: 'Chief complaint',
  historyOfPresentIllness: 'History of present illness',
  examination: 'Examination',
  assessment: 'Assessment',
  plan: 'Plan',
  followUpAdvice: 'Follow-up advice',
};
