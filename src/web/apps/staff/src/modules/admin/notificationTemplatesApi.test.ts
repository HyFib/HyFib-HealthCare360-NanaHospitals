import { describe, expect, it } from 'vitest';
import {
  EMPTY_TEMPLATE_FORM,
  filterTemplates,
  templateFormSchema,
  templateScope,
  toSaveRequest,
  toTemplateForm,
  type NotificationTemplateDto,
  type TemplateForm,
} from './notificationTemplatesApi';

function template(overrides: Partial<NotificationTemplateDto> = {}): NotificationTemplateDto {
  return {
    id: 't1',
    facilityId: null,
    key: 'lab.report_ready',
    channel: 'WhatsApp',
    language: 'en',
    subject: null,
    body: 'Dear {{name}}, your report {{reference}} is ready.',
    dltTemplateId: null,
    whatsAppTemplateName: 'lab_report_ready',
    description: 'Report ready (WhatsApp)',
    isActive: true,
    version: 7,
    whatsAppDocumentHeader: true,
    ...overrides,
  };
}

const form = (overrides: Partial<TemplateForm> = {}): TemplateForm => ({
  ...EMPTY_TEMPLATE_FORM,
  key: 'lab.report_ready',
  body: 'Dear {{name}}, your report {{reference}} is ready.',
  ...overrides,
});

describe('notification template form', () => {
  it('loads the document header of a WhatsApp template and sends it back', () => {
    const existing = template();
    const values = toTemplateForm(existing);
    expect(values.whatsAppDocumentHeader).toBe(true);

    const request = toSaveRequest({ ...values, body: `${values.body} ` }, existing, 'f1');
    expect(request).toEqual({
      key: 'lab.report_ready',
      channel: 'WhatsApp',
      language: 'en',
      facilityId: null,
      subject: null,
      body: 'Dear {{name}}, your report {{reference}} is ready.',
      dltTemplateId: null,
      whatsAppTemplateName: 'lab_report_ready',
      description: 'Report ready (WhatsApp)',
      isActive: true,
      version: 7,
      whatsAppDocumentHeader: true,
    });
  });

  it('never flags a document header on a template that is not for WhatsApp', () => {
    const request = toSaveRequest(
      form({
        channel: 'Sms',
        whatsAppDocumentHeader: true,
        whatsAppTemplateName: 'lab_report_ready',
        dltTemplateId: ' 1107160000000000001 ',
      }),
      null,
      'f1',
    );
    expect(request.whatsAppDocumentHeader).toBe(false);
    expect(request.whatsAppTemplateName).toBeNull();
    expect(request.dltTemplateId).toBe('1107160000000000001');
  });

  it('sends only the fields of the chosen channel', () => {
    const email = toSaveRequest(
      form({ channel: 'Email', subject: ' Your report ', dltTemplateId: '1107160000000000001' }),
      null,
      null,
    );
    expect(email.subject).toBe('Your report');
    expect(email.dltTemplateId).toBeNull();
    expect(email.whatsAppDocumentHeader).toBe(false);
    expect(email.version).toBeNull();
  });

  it('makes a new template for every facility or for this facility only', () => {
    expect(toSaveRequest(form({ appliesTo: 'all' }), null, 'f1').facilityId).toBeNull();
    expect(toSaveRequest(form({ appliesTo: 'facility' }), null, 'f1').facilityId).toBe('f1');
  });

  it('keeps the key, channel, language and facility of an existing template', () => {
    const existing = template({ facilityId: 'f2', language: 'hi' });
    const request = toSaveRequest(
      form({ key: 'something.else', channel: 'WhatsApp', language: 'en', appliesTo: 'all' }),
      existing,
      'f1',
    );
    expect(request).toMatchObject({ key: 'lab.report_ready', channel: 'WhatsApp', language: 'hi', facilityId: 'f2' });
  });

  it('checks the key of a new template, but not of an existing one', () => {
    expect(templateFormSchema(true).safeParse(form({ key: 'Lab Report' })).success).toBe(false);
    expect(templateFormSchema(true).safeParse(form({ key: 'lab.report_ready' })).success).toBe(true);
    expect(templateFormSchema(false).safeParse(form({ key: 'Legacy Key' })).success).toBe(true);
  });

  it('limits SMS text and checks the DLT and WhatsApp template names', () => {
    const schema = templateFormSchema(true);
    expect(schema.safeParse(form({ channel: 'Sms', body: 'x'.repeat(1001) })).success).toBe(false);
    expect(schema.safeParse(form({ channel: 'WhatsApp', body: 'x'.repeat(1001) })).success).toBe(true);
    expect(schema.safeParse(form({ dltTemplateId: '12345' })).success).toBe(false);
    expect(schema.safeParse(form({ whatsAppTemplateName: 'Lab Report' })).success).toBe(false);
    expect(schema.safeParse(form({ body: '   ' })).success).toBe(false);
  });
});

describe('notification template list', () => {
  const templates = [
    template(),
    template({ id: 't2', channel: 'Sms', description: 'Report ready', whatsAppDocumentHeader: false }),
    template({ id: 't3', key: 'appointment.reminder', channel: 'Sms', description: 'Appointment reminder' }),
  ];

  it('filters by channel and by key or description', () => {
    expect(filterTemplates(templates, 'Sms', undefined).map((t) => t.id)).toEqual(['t2', 't3']);
    expect(filterTemplates(templates, undefined, 'REMINDER').map((t) => t.id)).toEqual(['t3']);
    expect(filterTemplates(templates, 'WhatsApp', 'report').map((t) => t.id)).toEqual(['t1']);
    expect(filterTemplates(templates, undefined, ' ')).toHaveLength(3);
  });

  it('says who a template is for', () => {
    expect(templateScope(template(), 'f1')).toBe('All facilities');
    expect(templateScope(template({ facilityId: 'f1' }), 'f1')).toBe('This facility');
    expect(templateScope(template({ facilityId: 'f2' }), 'f1')).toBe('Another facility');
  });
});
