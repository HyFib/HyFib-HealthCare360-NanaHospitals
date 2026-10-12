import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  OverlayDrawer,
  Radio,
  RadioGroup,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowResetRegular, DismissRegular, DocumentSearchRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { MAX_CLOSING_NOTE_LENGTH, MAX_PRINT_SPACE_MM } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  CloseRequestScope,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import { LETTERHEAD_KEY, useLetterhead } from '@/lib/letterhead';
import { printTemplateKeys, printTemplatesApi, type PrintTemplateItem, type PrintTemplateList } from './api';
import {
  initialScope,
  offersScope,
  printTemplateFormSchema,
  toForm,
  toSaveRequest,
  toTemplate,
  type PrintTemplateForm,
  type TemplateScope,
} from './form';
import { templatePreviewHtml } from './preview';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '14px' },
  muted: { color: tokens.colorNeutralForeground3 },
  spaces: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))', gap: '12px' },
  restore: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  surface: { maxWidth: 'min(900px, 96vw)', width: '900px' },
  frame: {
    width: '100%',
    height: 'min(70vh, 720px)',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: '#e9e9e9',
  },
});

const PAPER_LABEL: Record<string, string> = { A4: 'A4', A5: 'A5 (half of A4)' };

/** Change how one kind of document prints, hospital-wide or at this facility. */
export function PrintTemplateDrawer({
  item,
  list,
  onClose,
}: {
  item: PrintTemplateItem;
  list: PrintTemplateList;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={closing.onOpenChange}>
      <CloseRequestScope value={closing}>
        <PrintTemplateEditor item={item} list={list} onClose={onClose} />
      </CloseRequestScope>
    </OverlayDrawer>
  );
}

function PrintTemplateEditor({
  item,
  list,
  onClose,
}: {
  item: PrintTemplateItem;
  list: PrintTemplateList;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const [preview, setPreview] = useState<string | null>(null);
  const form = useForm<PrintTemplateForm>({
    resolver: zodResolver(printTemplateFormSchema),
    defaultValues: toForm(item, initialScope(item, list)),
  });
  const facility = list.facilityName ?? 'this facility';

  const changed = (title: string, detail: string) => {
    notify.success(title, detail);
    void queryClient.invalidateQueries({ queryKey: printTemplateKeys.all });
    // This computer's own prints follow at once; others within ten minutes.
    void queryClient.invalidateQueries({ queryKey: LETTERHEAD_KEY });
    onClose();
  };

  const save = useMutation({
    mutationFn: (values: PrintTemplateForm) => printTemplatesApi.save(item.key, toSaveRequest(values, item)),
    onSuccess: (_, values) =>
      changed(
        'Print template saved',
        values.scope === 'facility'
          ? `${item.name} printed at ${facility} from now on follow it.`
          : `${item.name} printed from now on follow it${list.facilityCount > 1 ? ', except at facilities with their own' : ''}.`,
      ),
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the print template');
      }
    },
  });

  const removeOwn = useMutation({
    mutationFn: () => printTemplatesApi.removeFacilityTemplate(item.key),
    onSuccess: () =>
      changed('Print template removed', `${item.name} printed at ${facility} follow the hospital-wide template again.`),
    onError: (error) => notify.error(error, 'Could not remove the print template'),
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
          }
        >
          {item.name}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          <PrintTemplateFields
            control={form.control}
            item={item}
            list={list}
            onScopeChange={(scope) => form.reset(toForm(item, scope))}
          />
          {item.facility !== null && list.facilityId !== null && (
            <div className={styles.restore}>
              <Button icon={<ArrowResetRegular />} disabled={removeOwn.isPending} onClick={() => removeOwn.mutate()}>
                Use the hospital-wide template here
              </Button>
              <Caption1 className={styles.muted}>Removes {facility}'s own template.</Caption1>
            </div>
          )}
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button type="submit" appearance="primary" disabled={save.isPending || !form.formState.isDirty}>
          Save
        </Button>
        <Button
          icon={<DocumentSearchRegular />}
          onClick={() => setPreview(templatePreviewHtml(item, toTemplate(form.getValues(), item), letterhead))}
        >
          Preview
        </Button>
        <Button onClick={guard.requestClose}>Cancel</Button>
      </DrawerFooter>
      {preview !== null && <PreviewDialog title={item.name} html={preview} onClose={() => setPreview(null)} />}
      {guard.dialogs}
    </form>
  );
}

/**
 * The template's fields: hospital-wide or this facility's own, the letterhead or pre-printed stationery with the blank
 * space it needs, the logo, the closing note and the paper where there is a choice.
 */
export function PrintTemplateFields({
  control,
  item,
  list,
  onScopeChange,
}: {
  control: Control<PrintTemplateForm>;
  item: PrintTemplateItem;
  list: PrintTemplateList;
  onScopeChange: (scope: TemplateScope) => void;
}) {
  const styles = useStyles();
  const letterhead = useWatch({ control, name: 'letterhead' });
  const preprinted = item.allowsPreprinted && letterhead === 'Preprinted';

  return (
    <>
      <Text>{item.covers}</Text>
      {offersScope(item, list) && (
        <Controller
          control={control}
          name="scope"
          render={({ field }) => (
            <Field
              label="Applies to"
              hint="Each choice shows the template it prints with now. A facility without its own prints with the hospital-wide one."
            >
              <RadioGroup value={field.value} onChange={(_, data) => onScopeChange(data.value as TemplateScope)}>
                <Radio value="hospital" label="Every facility (hospital-wide)" />
                <Radio value="facility" label={`Only ${list.facilityName ?? 'this facility'}`} />
              </RadioGroup>
            </Field>
          )}
        />
      )}
      {item.allowsPreprinted ? (
        <ChoiceField
          control={control}
          name="letterhead"
          label="Top of the page"
          hint={
            preprinted
              ? 'For paper with the hospital letterhead already printed: HC360 leaves it blank. Tax invoices still print the GSTIN and PAN.'
              : 'HC360 prints the letterhead from Facilities and letterhead.'
          }
          options={[
            { value: 'Printed', label: 'Print the letterhead' },
            { value: 'Preprinted', label: 'Pre-printed stationery' },
          ]}
        />
      ) : (
        <Caption1 className={styles.muted}>
          These documents print on 80 mm rolls, which are never pre-printed, so the letterhead is always printed.
        </Caption1>
      )}
      {preprinted && (
        <div className={styles.spaces}>
          <NumberField
            control={control}
            name="topSpaceMm"
            label="Space at the top (mm)"
            hint="From the top edge of the paper to below its printed letterhead."
            min={0}
            max={MAX_PRINT_SPACE_MM}
          />
          <NumberField
            control={control}
            name="bottomSpaceMm"
            label="Space at the foot (mm)"
            hint="For a printed footer; 0 when there is none."
            min={0}
            max={MAX_PRINT_SPACE_MM}
          />
        </div>
      )}
      <SwitchField
        control={control}
        name="showLogo"
        label="Print the logo"
        hint={
          preprinted
            ? 'On pre-printed stationery the logo is printed on 80 mm slips only.'
            : 'The logo from Facilities and letterhead, beside the hospital name.'
        }
      />
      <TextAreaField
        control={control}
        name="closingNote"
        label="Closing note"
        hint="Printed at the end of each document, such as terms or a thank-you line. Plain text; line breaks are kept."
        rows={3}
        maxLength={MAX_CLOSING_NOTE_LENGTH}
      />
      {item.papers.length > 0 && (
        <SelectField
          control={control}
          name="paper"
          label="Paper"
          options={item.papers.map((paper) => ({ value: paper, label: PAPER_LABEL[paper] ?? paper }))}
        />
      )}
    </>
  );
}

/**
 * A sample page with the letterhead, blank space and closing note as they print, from the values being edited (not yet
 * saved). It is shown in a sandbox that runs no scripts.
 */
function PreviewDialog({ title, html, onClose }: { title: string; html: string; onClose: () => void }) {
  const styles = useStyles();
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface className={styles.surface}>
        <DialogBody>
          <DialogTitle>{title}: preview</DialogTitle>
          <DialogContent>
            <iframe className={styles.frame} title={`${title} preview`} sandbox="" srcDoc={html} />
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>
              Close
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
