import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useEffect } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { describe, expect, it, vi } from 'vitest';
import { TemplateFields } from './NotificationTemplateEditor';
import {
  EMPTY_TEMPLATE_FORM,
  toTemplateForm,
  type NotificationTemplateDto,
  type TemplateForm,
} from './notificationTemplatesApi';

// The drawer around the fields is Fluent's; the fields themselves are what is tested (drawers flake in jsdom).
function Harness({
  initial,
  template = null,
  onChange,
}: {
  initial: TemplateForm;
  template?: NotificationTemplateDto | null;
  onChange?: (values: Partial<TemplateForm>) => void;
}) {
  const form = useForm<TemplateForm>({ defaultValues: initial });
  const values = useWatch({ control: form.control });
  useEffect(() => onChange?.(values), [values, onChange]);
  return (
    <FluentProvider theme={webLightTheme}>
      <TemplateFields control={form.control} template={template} facilityId="f1" facilityName="Nana Hospitals, Pune" />
    </FluentProvider>
  );
}

const whatsAppTemplate: NotificationTemplateDto = {
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
};

describe('TemplateFields', () => {
  it('offers the document header only for WhatsApp, with what it means', async () => {
    const onChange = vi.fn();
    render(<Harness initial={EMPTY_TEMPLATE_FORM} onChange={onChange} />);

    expect(screen.queryByRole('checkbox', { name: 'Document header' })).not.toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: /DLT template id/ })).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByRole('combobox', { name: /Channel/ }), 'WhatsApp');

    const header = screen.getByRole('checkbox', { name: 'Document header' });
    expect(header).not.toBeChecked();
    expect(screen.getByText(/approved with a document header; the report's PDF is sent in it/)).toBeInTheDocument();
    expect(screen.getByText(/must match the template registered with Meta/)).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: /DLT template id/ })).not.toBeInTheDocument();

    await userEvent.click(header);

    expect(header).toBeChecked();
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ whatsAppDocumentHeader: true }));
  });

  it('shows the saved document header of a WhatsApp template and keeps its identity fixed', () => {
    render(<Harness initial={toTemplateForm(whatsAppTemplate)} template={whatsAppTemplate} />);

    expect(screen.getByRole('checkbox', { name: 'Document header' })).toBeChecked();
    expect(screen.getByRole('textbox', { name: /Message key/ })).toBeDisabled();
    expect(screen.getByRole('combobox', { name: /Channel/ })).toBeDisabled();
    expect(screen.getByRole('combobox', { name: /Language/ })).toBeDisabled();
    expect(screen.getByText('All facilities')).toBeInTheDocument();
  });

  it('asks for the subject of e-mail templates only', async () => {
    render(<Harness initial={EMPTY_TEMPLATE_FORM} />);

    expect(screen.queryByRole('option', { name: 'In-app' })).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Subject' })).not.toBeInTheDocument();
    await userEvent.selectOptions(screen.getByRole('combobox', { name: /Channel/ }), 'Email');
    expect(screen.getByRole('textbox', { name: 'Subject' })).toBeInTheDocument();
    expect(screen.queryByRole('checkbox', { name: 'Document header' })).not.toBeInTheDocument();
  });
});
