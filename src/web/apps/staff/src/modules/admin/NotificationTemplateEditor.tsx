import {
  Button,
  Checkbox,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  OverlayDrawer,
  Text,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, useSession } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import {
  CHANNEL_LABELS,
  EMPTY_TEMPLATE_FORM,
  languageLabel,
  NOTIFICATION_CHANNELS,
  notificationTemplateKeys,
  notificationTemplatesApi,
  SENDING_CHANNELS,
  SMS_BODY_LIMIT,
  TEMPLATE_LANGUAGES,
  templateFormSchema,
  templateScope,
  toSaveRequest,
  toTemplateForm,
  type NotificationTemplateDto,
  type TemplateForm,
} from './notificationTemplatesApi';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** Create or change one notification template. Its key, channel, language and facility are fixed once it exists. */
export function NotificationTemplateDrawer({
  template,
  onClose,
}: {
  template: NotificationTemplateDto | null;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId ?? null);
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'this facility';
  const form = useForm<TemplateForm>({
    resolver: zodResolver(templateFormSchema(template === null)),
    defaultValues: template ? toTemplateForm(template) : EMPTY_TEMPLATE_FORM,
  });

  const save = useMutation({
    mutationFn: async (values: TemplateForm) => {
      const request = toSaveRequest(values, template, facilityId);
      if (template) {
        await notificationTemplatesApi.update(template.id, request);
      } else {
        await notificationTemplatesApi.create(request);
      }
    },
    onSuccess: () => {
      notify.success('Template saved', 'Messages sent from now on use it.');
      void queryClient.invalidateQueries({ queryKey: notificationTemplateKeys.all });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        notify.warning(
          'Someone else changed this template',
          'Close it and open it again to see their version, then make your change.',
        );
        void queryClient.invalidateQueries({ queryKey: notificationTemplateKeys.all });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the template');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {template ? template.key : 'New template'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <TemplateFields
            control={form.control}
            template={template}
            facilityId={facilityId}
            facilityName={facilityName}
          />
        </DrawerBody>
        <DrawerFooter>
          <Button type="submit" appearance="primary" disabled={save.isPending}>
            {template ? 'Save' : 'Create template'}
          </Button>
          <Button onClick={guard.requestClose}>Cancel</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}

/** The editor's fields. Fields that belong to one channel (subject, DLT id, WhatsApp template) show for it only. */
export function TemplateFields({
  control,
  template,
  facilityId,
  facilityName,
}: {
  control: Control<TemplateForm>;
  template: NotificationTemplateDto | null;
  facilityId: string | null;
  facilityName: string;
}) {
  const styles = useStyles();
  const channel = useWatch({ control, name: 'channel' });
  const editing = template !== null;
  const languages = {
    ...TEMPLATE_LANGUAGES,
    ...(template ? { [template.language]: languageLabel(template.language) } : {}),
  };

  return (
    <div className={styles.form}>
      <TextField
        control={control}
        name="key"
        label="Message key"
        required
        disabled={editing}
        autoFocus={!editing}
        maxLength={64}
        hint={
          editing
            ? 'The key, channel, language and facility of a template cannot be changed.'
            : 'Which message this is, for example lab.report_ready. HC360 finds templates by this key.'
        }
      />
      <SelectField
        control={control}
        name="channel"
        label="Channel"
        required
        disabled={editing}
        options={(editing ? NOTIFICATION_CHANNELS : SENDING_CHANNELS).map((value) => ({
          value,
          label: CHANNEL_LABELS[value],
        }))}
      />
      <SelectField
        control={control}
        name="language"
        label="Language"
        required
        disabled={editing}
        options={Object.entries(languages).map(([value, label]) => ({ value, label }))}
        hint="Patients get the version in their language when there is one, English otherwise."
      />
      {editing ? (
        <Field label="Used by">
          <Text>{templateScope(template, facilityId)}</Text>
        </Field>
      ) : (
        <ChoiceField
          control={control}
          name="appliesTo"
          label="Used by"
          options={[
            { value: 'all', label: 'All facilities' },
            { value: 'facility', label: `Only ${facilityName}` },
          ]}
          hint="A facility's own version is used instead of the one for all facilities."
        />
      )}
      {channel === 'Email' && <TextField control={control} name="subject" label="Subject" maxLength={200} />}
      <TextAreaField
        control={control}
        name="body"
        label="Message"
        required
        rows={5}
        maxLength={channel === 'Sms' ? SMS_BODY_LIMIT : 4000}
        hint="Placeholders in double braces, such as {{name}} or {{hospital}}, are filled in when the message is sent."
      />
      {channel === 'Sms' && (
        <TextField
          control={control}
          name="dltTemplateId"
          label="DLT template id"
          maxLength={25}
          hint="The TRAI DLT id of the approved template; the message must match its approved text."
        />
      )}
      {channel === 'WhatsApp' && (
        <>
          <TextField
            control={control}
            name="whatsAppTemplateName"
            label="WhatsApp template name"
            maxLength={128}
            hint="The approved template's name at Meta. Empty: the key with underscores, for example lab_report_ready."
          />
          <Controller
            control={control}
            name="whatsAppDocumentHeader"
            render={({ field }) => (
              <Field hint="This WhatsApp template was approved with a document header; the report's PDF is sent in it. It must match the template registered with Meta, which refuses messages that do not match.">
                <Checkbox
                  label="Document header"
                  checked={field.value}
                  onChange={(_, data) => field.onChange(data.checked === true)}
                />
              </Field>
            )}
          />
        </>
      )}
      <TextField
        control={control}
        name="description"
        label="Description"
        maxLength={300}
        hint="What the message is for, as shown in the list."
      />
      <SwitchField
        control={control}
        name="isActive"
        label="In use"
        hint="When it is not in use, messages use the next version that fits (all facilities, English) or the built-in text."
      />
    </div>
  );
}
