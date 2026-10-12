import {
  Button,
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  tokens,
} from '@fluentui/react-components';
import { PrintRegular, SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  PRINTER_ROLE_LABEL,
  type PrinterInfo,
  type PrinterRole,
  type PrinterSetupInfo,
  type RolePrinter,
} from '@hc360/core';
import { ChoiceField, FormGrid, NumberField, Section, SelectField, useLeaveGuard } from '@hc360/ui';
import { Controller, useForm, useWatch, type Control, type UseFormSetValue } from 'react-hook-form';
import {
  CODE_OPTIONS,
  DEFAULT_STOCK,
  DPI_OPTIONS,
  guessLanguage,
  LABEL_SIZES,
  LANGUAGE_OPTIONS,
  NO_PRINTER,
  printerSetupSchema,
  ROLE_HELP,
  sizeText,
  toForm,
  toLabelPrinter,
  type LabelRole,
  type PrinterSetupForm,
} from './form';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  role: { display: 'flex', flexDirection: 'column', gap: '12px' },
  sizes: { display: 'flex', flexWrap: 'wrap', gap: '6px' },
  muted: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
});

const ROLES: PrinterRole[] = ['labels', 'wristbands', 'receipts', 'documents'];

/**
 * The printers of this computer, one card per kind of print. A kind with a printer prints straight to it; a kind
 * without one shows the print dialog. Label and wristband printers also say how HC360 talks to them and what stock
 * they hold. Each card prints a test with what is on the screen, saved or not.
 */
export function PrinterSetupForm({
  info,
  installed,
  canEdit,
  saving,
  testing,
  onSave,
  onTest,
}: {
  info: PrinterSetupInfo;
  installed: PrinterInfo[];
  /** The user may set up printers and IT has not fixed them. */
  canEdit: boolean;
  saving: boolean;
  /** The kind of print whose test is printing. */
  testing: PrinterRole | null;
  onSave: (values: PrinterSetupForm) => void;
  onTest: (role: PrinterRole, printer: RolePrinter) => void;
}) {
  const styles = useStyles();
  // The screen keys the form by the setup's save time, so a save (here or in another window) starts it afresh.
  const form = useForm<PrinterSetupForm>({
    resolver: zodResolver(printerSetupSchema),
    defaultValues: toForm(info.setup),
  });
  const values = useWatch({ control: form.control });

  const testPrinter = (role: PrinterRole): RolePrinter | null => {
    if (role === 'labels' || role === 'wristbands') {
      const value = form.getValues(role);
      return printerSetupSchema.shape[role].safeParse(value).success ? toLabelPrinter(role, value) : null;
    }
    const printer = form.getValues(role).printer;
    return printer === NO_PRINTER ? null : { printer };
  };

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: canEdit && form.formState.isDirty, busy: saving });

  return (
    <form className={styles.form} onSubmit={form.handleSubmit(onSave)} noValidate>
      {ROLES.map((role) => {
        const chosen = values[role]?.printer ?? NO_PRINTER;
        return (
          <Section
            key={role}
            title={PRINTER_ROLE_LABEL[role]}
            description={ROLE_HELP[role]}
            actions={
              <Button
                icon={<PrintRegular />}
                disabled={chosen === NO_PRINTER || testing !== null}
                onClick={() => {
                  const printer = testPrinter(role);
                  if (printer) {
                    onTest(role, printer);
                  } else {
                    void form.trigger(role);
                  }
                }}
              >
                {testing === role ? 'Printing…' : 'Print a test'}
              </Button>
            }
          >
            <div className={styles.role}>
              <PrinterField
                control={form.control}
                setValue={form.setValue}
                role={role}
                installed={installed}
                disabled={!canEdit}
              />
              {(role === 'labels' || role === 'wristbands') && chosen !== NO_PRINTER && (
                <LabelPrinterFields control={form.control} setValue={form.setValue} role={role} disabled={!canEdit} />
              )}
            </div>
          </Section>
        );
      })}
      {canEdit && (
        <div className={styles.footer}>
          <Button
            type="submit"
            appearance="primary"
            icon={<SaveRegular />}
            disabled={saving || !form.formState.isDirty}
          >
            {saving ? 'Saving…' : 'Save printers'}
          </Button>
          <Button disabled={saving || !form.formState.isDirty} onClick={() => form.reset(toForm(info.setup))}>
            Undo changes
          </Button>
          <Caption1 className={styles.muted}>The printers apply to everyone who uses HC360 on this computer.</Caption1>
        </div>
      )}
    </form>
  );
}

/** The Windows printer for one kind of print, with a warning when a saved printer is no longer installed. */
function PrinterField({
  control,
  setValue,
  role,
  installed,
  disabled,
}: {
  control: Control<PrinterSetupForm>;
  setValue: UseFormSetValue<PrinterSetupForm>;
  role: PrinterRole;
  installed: PrinterInfo[];
  disabled: boolean;
}) {
  return (
    <Controller
      control={control}
      name={`${role}.printer`}
      render={({ field }) => {
        const missing = field.value !== NO_PRINTER && !installed.some((p) => p.name === field.value);
        return (
          <Field
            label="Printer"
            validationState={missing ? 'warning' : 'none'}
            validationMessage={
              missing ? `"${field.value}" is not installed on this computer. Choose another printer.` : undefined
            }
          >
            <Select
              name={field.name}
              ref={field.ref}
              onBlur={field.onBlur}
              value={field.value}
              disabled={disabled}
              onChange={(_, data) => {
                field.onChange(data.value);
                if ((role === 'labels' || role === 'wristbands') && data.value !== NO_PRINTER) {
                  setValue(`${role}.language`, guessLanguage(data.value), { shouldDirty: true });
                }
              }}
            >
              <option value={NO_PRINTER}>Not set: ask with the print dialog</option>
              {installed.map((printer) => (
                <option key={printer.name} value={printer.name}>
                  {printer.isDefault ? `${printer.name} (Windows default)` : printer.name}
                </option>
              ))}
              {missing && <option value={field.value}>{`${field.value} (not installed)`}</option>}
            </Select>
          </Field>
        );
      }}
    />
  );
}

/** How HC360 talks to a label or band printer, and the stock in it. */
function LabelPrinterFields({
  control,
  setValue,
  role,
  disabled,
}: {
  control: Control<PrinterSetupForm>;
  setValue: UseFormSetValue<PrinterSetupForm>;
  role: LabelRole;
  disabled: boolean;
}) {
  const styles = useStyles();
  const language = useWatch({ control, name: `${role}.language` });
  const band = role === 'wristbands';
  return (
    <>
      <SelectField
        control={control}
        name={`${role}.language`}
        label="How HC360 prints to it"
        options={LANGUAGE_OPTIONS}
        disabled={disabled}
        hint={
          language === 'driver'
            ? 'The label is printed as a page, scaled by the Windows driver. Barcodes scan better with ZPL or TSPL.'
            : 'HC360 sends the printer its own commands, so every bar of a barcode is a whole number of printer dots.'
        }
      />
      {language !== 'driver' && (
        <>
          <FormGrid columns={3} minColumnWidth={160}>
            <SelectField
              control={control}
              name={`${role}.dpi`}
              label="Resolution"
              options={DPI_OPTIONS}
              disabled={disabled}
            />
            <NumberField
              control={control}
              name={`${role}.widthMm`}
              label={band ? 'Band width (mm)' : 'Label width (mm)'}
              min={0}
              step={0.1}
              disabled={disabled}
            />
            <NumberField
              control={control}
              name={`${role}.heightMm`}
              label={band ? 'Band length (mm)' : 'Label height (mm)'}
              min={0}
              step={0.1}
              disabled={disabled}
            />
          </FormGrid>
          {!band && !disabled && (
            <div className={styles.sizes} role="group" aria-label="Common label sizes">
              {LABEL_SIZES.map((size) => (
                <Button
                  key={sizeText(size.widthMm, size.heightMm)}
                  size="small"
                  appearance="secondary"
                  onClick={() => {
                    setValue('labels.widthMm', size.widthMm, { shouldDirty: true, shouldValidate: true });
                    setValue('labels.heightMm', size.heightMm, { shouldDirty: true, shouldValidate: true });
                  }}
                >
                  {sizeText(size.widthMm, size.heightMm)}
                </Button>
              ))}
            </div>
          )}
          {band && (
            <Caption1 className={styles.muted}>
              An adult band is usually {sizeText(DEFAULT_STOCK.wristbands.widthMm, DEFAULT_STOCK.wristbands.heightMm)}{' '}
              (1 × 11 inches). If the print starts on the strap, set the label top in the printer's own settings.
            </Caption1>
          )}
        </>
      )}
      {band && (
        <ChoiceField
          control={control}
          name="wristbands.code"
          label="UHID on the band"
          options={CODE_OPTIONS}
          disabled={disabled}
        />
      )}
      {language !== 'driver' && (
        <MessageBar intent="info">
          <MessageBarBody>
            Print a test after changing these. The test label is framed 1 mm inside its edges: if a side of the frame is
            missing, the size or the printer&apos;s position setting is wrong.
          </MessageBarBody>
        </MessageBar>
      )}
    </>
  );
}
