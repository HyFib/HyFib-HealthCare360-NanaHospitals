import { queryOptions } from '@tanstack/react-query';
import { z } from 'zod';
import { api } from '@/lib/api';

/**
 * Notification templates (HMS-04): the SMS, WhatsApp, e-mail and in-app messages HC360 sends, keyed like
 * `lab.report_ready`. Mirrors `NotificationEndpoints` (`/api/v1/admin/notification-templates`, permission
 * `admin.notifications.manage`). Placeholders such as `{{name}}` are filled in when a message is sent. A template of
 * this facility wins over the all-facility one, and the patient's language over English.
 */

export type NotificationChannel = 'Sms' | 'WhatsApp' | 'Email' | 'InApp';

export const NOTIFICATION_CHANNELS: readonly NotificationChannel[] = ['Sms', 'WhatsApp', 'Email', 'InApp'];

/** The channels messages are sent on; in-app notifications do not use templates (old ones stay listed). */
export const SENDING_CHANNELS: readonly NotificationChannel[] = ['Sms', 'WhatsApp', 'Email'];

export const CHANNEL_LABELS: Record<NotificationChannel, string> = {
  Sms: 'SMS',
  WhatsApp: 'WhatsApp',
  Email: 'E-mail',
  InApp: 'In-app',
};

/** The languages messages are sent in (the patient's preferred language, English otherwise). */
export const TEMPLATE_LANGUAGES: Record<string, string> = { en: 'English', hi: 'Hindi', ta: 'Tamil' };

export function languageLabel(code: string): string {
  return TEMPLATE_LANGUAGES[code] ?? code;
}

export interface NotificationTemplateDto {
  id: string;
  /** Null: used by every facility unless a facility has its own version. */
  facilityId: string | null;
  key: string;
  channel: NotificationChannel;
  language: string;
  subject: string | null;
  body: string;
  dltTemplateId: string | null;
  whatsAppTemplateName: string | null;
  description: string | null;
  isActive: boolean;
  version: number;
  /** The WhatsApp template was approved with a document header: messages that carry a document (a report's PDF) send it there. */
  whatsAppDocumentHeader: boolean;
}

/** Key, channel, language and facility are fixed once the template exists; the server ignores them on update. */
export interface SaveNotificationTemplateRequest {
  key: string;
  channel: NotificationChannel;
  language: string;
  facilityId: string | null;
  subject: string | null;
  body: string;
  dltTemplateId: string | null;
  whatsAppTemplateName: string | null;
  description: string | null;
  isActive: boolean;
  version: number | null;
  whatsAppDocumentHeader: boolean;
}

const BASE = '/api/v1/admin/notification-templates';

export const notificationTemplateKeys = { all: ['admin', 'notification-templates'] as const };

export const notificationTemplateQueries = {
  list: () =>
    queryOptions({
      queryKey: notificationTemplateKeys.all,
      queryFn: ({ signal }) => api.get<NotificationTemplateDto[]>(BASE, { signal }),
    }),
};

export const notificationTemplatesApi = {
  create: (request: SaveNotificationTemplateRequest) => api.post<{ id: string }>(BASE, request),
  update: (id: string, request: SaveNotificationTemplateRequest) => api.put<void>(`${BASE}/${id}`, request),
};

// ------------------------------------------------------------------------------------------------ form

export const SMS_BODY_LIMIT = 1000;
const KEY = /^[a-z0-9]+([._-][a-z0-9]+)*$/;
const DLT_ID = /^[0-9]{10,25}$/;
const WHATSAPP_NAME = /^[a-z0-9_]+$/;

const templateFields = z.object({
  key: z
    .string()
    .trim()
    .min(1, 'Enter the message key, for example lab.report_ready.')
    .max(64, 'Use at most 64 characters.'),
  channel: z.enum(['Sms', 'WhatsApp', 'Email', 'InApp']),
  language: z.string().trim().min(1, 'Choose the language.'),
  /** A new template is for every facility, or for the signed-in user's facility only. */
  appliesTo: z.enum(['all', 'facility']),
  subject: z.string().trim().max(200, 'Use at most 200 characters.'),
  body: z.string().trim().min(1, 'Enter the message text.').max(4000, 'Use at most 4000 characters.'),
  dltTemplateId: z
    .string()
    .trim()
    .refine((value) => !value || DLT_ID.test(value), 'A DLT template id is a 10–25 digit number.'),
  whatsAppTemplateName: z
    .string()
    .trim()
    .max(128, 'Use at most 128 characters.')
    .refine(
      (value) => !value || WHATSAPP_NAME.test(value),
      'Template names use lower-case letters, digits and underscores.',
    ),
  whatsAppDocumentHeader: z.boolean(),
  description: z.string().trim().max(300, 'Use at most 300 characters.'),
  isActive: z.boolean(),
});

export type TemplateForm = z.infer<typeof templateFields>;

/** The editor's rules. The key of an existing template cannot change, so its format is checked for new ones only. */
export function templateFormSchema(isNew: boolean) {
  return templateFields.superRefine((value, ctx) => {
    if (isNew && !KEY.test(value.key)) {
      ctx.addIssue({
        code: 'custom',
        path: ['key'],
        message: 'Use lower-case letters and digits, separated by dots, dashes or underscores.',
      });
    }
    if (value.channel === 'Sms' && value.body.length > SMS_BODY_LIMIT) {
      ctx.addIssue({
        code: 'custom',
        path: ['body'],
        message: `SMS templates must be at most ${SMS_BODY_LIMIT} characters.`,
      });
    }
  });
}

export const EMPTY_TEMPLATE_FORM: TemplateForm = {
  key: '',
  channel: 'Sms',
  language: 'en',
  appliesTo: 'all',
  subject: '',
  body: '',
  dltTemplateId: '',
  whatsAppTemplateName: '',
  whatsAppDocumentHeader: false,
  description: '',
  isActive: true,
};

export function toTemplateForm(template: NotificationTemplateDto): TemplateForm {
  return {
    key: template.key,
    channel: template.channel,
    language: template.language,
    appliesTo: template.facilityId === null ? 'all' : 'facility',
    subject: template.subject ?? '',
    body: template.body,
    dltTemplateId: template.dltTemplateId ?? '',
    whatsAppTemplateName: template.whatsAppTemplateName ?? '',
    whatsAppDocumentHeader: template.whatsAppDocumentHeader,
    description: template.description ?? '',
    isActive: template.isActive,
  };
}

/**
 * The save request: only the fields of the chosen channel are sent (a subject for e-mail, the DLT id for SMS, the Meta
 * template name and its document header for WhatsApp). A new template applies to every facility or to `facilityId`.
 */
export function toSaveRequest(
  values: TemplateForm,
  existing: NotificationTemplateDto | null,
  facilityId: string | null,
): SaveNotificationTemplateRequest {
  const text = (value: string) => value.trim() || null;
  const whatsApp = values.channel === 'WhatsApp';
  return {
    key: existing?.key ?? values.key.trim(),
    channel: existing?.channel ?? values.channel,
    language: existing?.language ?? values.language.trim(),
    facilityId: existing ? existing.facilityId : values.appliesTo === 'all' ? null : facilityId,
    subject: values.channel === 'Email' ? text(values.subject) : null,
    body: values.body.trim(),
    dltTemplateId: values.channel === 'Sms' ? text(values.dltTemplateId) : null,
    whatsAppTemplateName: whatsApp ? text(values.whatsAppTemplateName) : null,
    description: text(values.description),
    isActive: values.isActive,
    version: existing?.version ?? null,
    whatsAppDocumentHeader: whatsApp && values.whatsAppDocumentHeader,
  };
}

/** Who a template is for, in words. */
export function templateScope(template: NotificationTemplateDto, facilityId: string | null): string {
  if (template.facilityId === null) {
    return 'All facilities';
  }
  return template.facilityId === facilityId ? 'This facility' : 'Another facility';
}

/** Templates matching the screen's filters (channel, and a key or description containing the search text). */
export function filterTemplates(
  templates: readonly NotificationTemplateDto[],
  channel: NotificationChannel | undefined,
  search: string | undefined,
): NotificationTemplateDto[] {
  const term = search?.trim().toLowerCase() ?? '';
  return templates.filter(
    (t) =>
      (!channel || t.channel === channel) &&
      (!term || t.key.toLowerCase().includes(term) || (t.description ?? '').toLowerCase().includes(term)),
  );
}
