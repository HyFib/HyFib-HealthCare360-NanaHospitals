import { isApiError, type ApiError } from '@hc360/core';
import type { OptionItem } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { z } from 'zod';
import { lookupQueries } from '@/lib/lookups';
import type { SaveTemplateRequest, TemplateDto, TemplateServiceDto } from './api';
import type { DoseFrequency } from './dosing';
import type { SaveOrderSetRequest } from './ordersApi';

// The template and order-set catalogues (EMR-02, EMR-05): what their forms accept, mirroring the server's
// SaveTemplateRequestValidator and SaveOrderSetRequestValidator, and the requests they send.

/** A template's or order set's code, as the server stores it (capitals, digits and hyphens). */
export const CATALOGUE_CODE = /^[A-Z0-9][A-Z0-9-]*$/;
export const CODE_HINT = 'Capital letters, digits and hyphens, e.g. GM-DM.';

/** The code is another template's or order set's (409 `template.code_in_use` / `order_set.code_in_use`). */
export function isCodeInUse(error: unknown): error is ApiError {
  return isApiError(error) && (error.code === 'template.code_in_use' || error.code === 'order_set.code_in_use');
}

/** Someone else saved the record after it was opened (REL-04): the save was refused, nothing was overwritten. */
export function isStale(error: unknown): boolean {
  return isApiError(error) && error.isConflict && !isCodeInUse(error);
}

/**
 * The specialities of the masters (their codes are what templates and order sets store), as select options. A code no
 * longer in the masters is still offered, so that an edit does not drop it silently.
 */
export function useSpecialtyOptions(current?: string | null): {
  options: OptionItem[];
  nameOf: (code: string) => string;
} {
  const specialties = useQuery(lookupQueries.specialties());
  const items = specialties.data ?? [];
  const options = items.map((s) => ({ value: s.code, label: `${s.name} (${s.code})` }));
  if (current && !items.some((s) => s.code === current)) {
    options.push({ value: current, label: current });
  }
  return { options, nameOf: (code) => items.find((s) => s.code === code)?.name ?? code };
}

const code = z
  .string()
  .trim()
  .min(1, 'Enter a short code.')
  .max(32, 'At most 32 characters.')
  .refine((v) => CATALOGUE_CODE.test(v.toUpperCase()), 'Use letters, digits and hyphens only, without spaces.');

const service = z.object({
  serviceItemId: z.string(),
  code: z.string(),
  name: z.string(),
  category: z.custom<TemplateServiceDto['category']>(),
});

const text = (v: string): string | null => v.trim() || null;

// ---------------------------------------------------------------------------------------------------- order sets

export const orderSetSchema = z.object({
  code,
  name: z.string().trim().min(1, 'Enter the name doctors pick it by.').max(200, 'At most 200 characters.'),
  specialtyCode: z.string().max(16).nullable(),
  description: z.string().max(500, 'At most 500 characters.'),
  services: z.array(service).min(1, 'Add at least one service.').max(40, 'An order set has at most 40 services.'),
  isActive: z.boolean(),
});

export type OrderSetValues = z.infer<typeof orderSetSchema>;

/** The request a save sends: trimmed, the code in capitals, blanks as null and the version the set was opened at. */
export function toOrderSetRequest(values: OrderSetValues, version: number | null): SaveOrderSetRequest {
  return {
    code: values.code.trim().toUpperCase(),
    name: values.name.trim(),
    specialtyCode: values.specialtyCode || null,
    description: text(values.description),
    serviceItemIds: values.services.map((s) => s.serviceItemId),
    isActive: values.isActive,
    version,
  };
}

// ---------------------------------------------------------------------------------------------------- templates

const FREQUENCIES = [
  'OD',
  'BD',
  'TDS',
  'QID',
  'HS',
  'SOS',
  'STAT',
  'Weekly',
  'Custom',
] as const satisfies readonly DoseFrequency[];

const medicineSchema = z
  .object({
    drugItemId: z.string().nullable(),
    drugName: z.string().trim().min(1, 'Enter the medicine.').max(200, 'At most 200 characters.'),
    genericName: z.string().max(300),
    strength: z.string().max(64, 'At most 64 characters.'),
    dosageForm: z.string().max(64),
    route: z.string().max(32),
    dose: z.string().trim().min(1, 'Enter the dose, e.g. 1 tablet.').max(64, 'At most 64 characters.'),
    frequency: z.enum(FREQUENCIES),
    customFrequency: z.string().max(64, 'At most 64 characters.'),
    durationDays: z
      .number()
      .int('Whole days only.')
      .min(1, 'From 1 to 365 days.')
      .max(365, 'From 1 to 365 days.')
      .nullable(),
    instructions: z.string().max(300, 'At most 300 characters.'),
  })
  .superRefine((m, ctx) => {
    if (m.frequency === 'Custom' && !m.customFrequency.trim()) {
      ctx.addIssue({ code: 'custom', path: ['customFrequency'], message: 'Describe the frequency, e.g. 1-0-1.' });
    }
    if (m.durationDays === null) {
      ctx.addIssue({ code: 'custom', path: ['durationDays'], message: 'Enter the number of days.' });
    }
  });

export type TemplateMedicineValues = z.infer<typeof medicineSchema>;

/** Common complaints are typed one per line. */
export const complaintLines = (value: string): string[] => [
  ...new Set(
    value
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean),
  ),
];

export const templateSchema = z.object({
  code,
  name: z.string().trim().min(1, 'Enter the name doctors pick it by.').max(200, 'At most 200 characters.'),
  specialtyCode: z.string().max(16).nullable(),
  personal: z.boolean(),
  chiefComplaint: z.string().max(1000, 'At most 1000 characters.'),
  historyOfPresentIllness: z.string().max(4000, 'At most 4000 characters.'),
  examination: z.string().max(4000, 'At most 4000 characters.'),
  assessment: z.string().max(4000, 'At most 4000 characters.'),
  plan: z.string().max(4000, 'At most 4000 characters.'),
  followUpAdvice: z.string().max(1000, 'At most 1000 characters.'),
  followUpDays: z
    .number()
    .int('Whole days only.')
    .min(1, 'From 1 to 365 days.')
    .max(365, 'From 1 to 365 days.')
    .nullable(),
  commonComplaints: z
    .string()
    .refine((v) => complaintLines(v).length <= 30, 'At most 30 complaints.')
    .refine((v) => complaintLines(v).every((c) => c.length <= 100), 'Each complaint has at most 100 characters.'),
  services: z.array(service).max(30, 'At most 30 default orders.'),
  medications: z.array(medicineSchema).max(20, 'At most 20 default medicines.'),
  displayOrder: z.number({ error: 'Enter a number.' }).int('Whole numbers only.'),
  isActive: z.boolean(),
});

export type TemplateValues = z.infer<typeof templateSchema>;

export const emptyMedicine = (): TemplateMedicineValues => ({
  drugItemId: null,
  drugName: '',
  genericName: '',
  strength: '',
  dosageForm: '',
  route: '',
  dose: '',
  frequency: 'OD',
  customFrequency: '',
  durationDays: 5,
  instructions: '',
});

/** The form's values for a template being edited, or a new one. */
export function templateValues(template: TemplateDto | null): TemplateValues {
  return {
    code: template?.code ?? '',
    name: template?.name ?? '',
    specialtyCode: template?.specialtyCode ?? null,
    personal: template ? template.ownerPractitionerId !== null : false,
    chiefComplaint: template?.chiefComplaint ?? '',
    historyOfPresentIllness: template?.historyOfPresentIllness ?? '',
    examination: template?.examination ?? '',
    assessment: template?.assessment ?? '',
    plan: template?.plan ?? '',
    followUpAdvice: template?.followUpAdvice ?? '',
    followUpDays: template?.followUpDays ?? null,
    commonComplaints: (template?.commonComplaints ?? []).join('\n'),
    services: template?.defaultOrders ?? [],
    medications: (template?.medications ?? []).map((m) => ({
      drugItemId: m.drugItemId,
      drugName: m.drugName,
      genericName: m.genericName ?? '',
      strength: m.strength ?? '',
      dosageForm: m.dosageForm ?? '',
      route: m.route ?? '',
      dose: m.dose,
      frequency: m.frequency,
      customFrequency: m.customFrequency ?? '',
      durationDays: m.durationDays,
      instructions: m.instructions ?? '',
    })),
    displayOrder: template?.displayOrder ?? 0,
    isActive: template?.isActive ?? true,
  };
}

/** The request a save sends: trimmed, the code in capitals, blanks as null and the version the template was loaded at. */
export function toTemplateRequest(values: TemplateValues, version: number | null): SaveTemplateRequest {
  return {
    code: values.code.trim().toUpperCase(),
    name: values.name.trim(),
    specialtyCode: values.specialtyCode || null,
    personal: values.personal,
    chiefComplaint: text(values.chiefComplaint),
    historyOfPresentIllness: text(values.historyOfPresentIllness),
    examination: text(values.examination),
    assessment: text(values.assessment),
    plan: text(values.plan),
    followUpAdvice: text(values.followUpAdvice),
    followUpDays: values.followUpDays,
    commonComplaints: complaintLines(values.commonComplaints),
    defaultServiceItemIds: values.services.map((s) => s.serviceItemId),
    medications: values.medications.map((m) => ({
      drugItemId: m.drugItemId,
      drugName: m.drugName.trim(),
      genericName: text(m.genericName),
      strength: text(m.strength),
      dosageForm: text(m.dosageForm),
      route: text(m.route),
      dose: m.dose.trim(),
      frequency: m.frequency,
      customFrequency: m.frequency === 'Custom' ? text(m.customFrequency) : null,
      durationDays: m.durationDays ?? 1,
      instructions: text(m.instructions),
    })),
    displayOrder: values.displayOrder,
    isActive: values.isActive,
    version,
  };
}
