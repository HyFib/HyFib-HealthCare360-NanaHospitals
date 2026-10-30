import {
  Badge,
  Button,
  Caption1,
  Combobox,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Option,
  Select,
  SpinButton,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  AddRegular,
  ChevronDownRegular,
  ChevronUpRegular,
  DeleteRegular,
  DocumentArrowLeftRegular,
  SaveRegular,
  ShieldCheckmarkRegular,
  SignatureRegular,
  StarRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatRelative, isApiError, useSession } from '@hc360/core';
import { LEAVE_KEEPING_DRAFT, useLeaveGuard, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState, type ReactElement } from 'react';
import { Controller, useFieldArray, useForm, useWatch, type Control, type UseFormReturn } from 'react-hook-form';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import { emrApi, emrKeys, emrQueries, type CurrentMedication } from './api';
import {
  DOSAGE_FORMS,
  DOSE_FREQUENCIES,
  DURATION_PRESETS,
  directionsText,
  frequencyOptionLabel,
  INSTRUCTION_PRESETS,
  quantityUnit,
  ROUTES,
  type DoseFrequency,
} from './dosing';
import { SignedPrescriptionNotice } from './DigitalSignature';
import { DrugPicker } from './DrugPicker';
import {
  appendLines,
  applyDefaults,
  autoQuantity,
  duplicateGenerics,
  emptyLine,
  emptyRxForm,
  favouriteDefaults,
  favouriteFromLine,
  fromCurrentMedication,
  hasRxContent,
  rxFormSchema,
  sameRxContent,
  toRequestBody,
  toRxFormValues,
  type MedicineDefaults,
  type RxFormValues,
  type RxLineValues,
} from './prescriptionForm';
import { prescriptionQueries, prescriptionsApi, type PrescriptionDto } from './prescriptionsApi';
import { SafetyReview } from './SafetyReview';
import {
  alertsFromProblem,
  emptyDecision,
  groupAlerts,
  signBlockers,
  signRequirements,
  toSignRequest,
  type SafetyCheckResponse,
  type SignDecision,
} from './safety';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px' },
  quick: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  header: {
    display: 'grid',
    gridTemplateColumns: '28px minmax(200px, 2fr) 110px 180px 90px 100px minmax(140px, 1fr) 72px',
    gap: '8px',
    padding: '0 8px',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    '@media (max-width: 1200px)': { display: 'none' },
  },
  row: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '8px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  rowControlled: { borderLeft: `4px solid ${tokens.colorPaletteRedBorder2}` },
  grid: {
    display: 'grid',
    gridTemplateColumns: '28px minmax(200px, 2fr) 110px 180px 90px 100px minmax(140px, 1fr) 72px',
    gap: '8px',
    alignItems: 'start',
    '@media (max-width: 1200px)': { gridTemplateColumns: '28px repeat(3, minmax(0, 1fr))' },
  },
  number: { paddingTop: '6px', fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground3 },
  drug: { '@media (max-width: 1200px)': { gridColumn: '2 / -1' } },
  narrowLabel: { display: 'none', '@media (max-width: 1200px)': { display: 'block' } },
  actions: { display: 'flex', gap: '2px', justifyContent: 'flex-end' },
  details: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))',
    gap: '8px',
    paddingLeft: '36px',
  },
  preview: { paddingLeft: '36px', display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  switchRow: { display: 'flex', alignItems: 'center', minHeight: '32px' },
  bottom: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr 150px',
    gap: '12px',
    '@media (max-width: 900px)': { gridTemplateColumns: '1fr' },
  },
  buttons: { display: 'flex', gap: '8px', justifyContent: 'flex-end', alignItems: 'center', flexWrap: 'wrap' },
  blockers: { color: tokens.colorNeutralForeground3, textAlign: 'right' },
});

function RowField({
  label,
  error,
  children,
  className,
}: {
  label: string;
  error?: string;
  children: ReactElement;
  className?: string;
}) {
  const styles = useStyles();
  return (
    <Field
      className={className}
      label={{ children: label, className: styles.narrowLabel }}
      validationMessage={error}
      validationState={error ? 'error' : 'none'}
    >
      {children}
    </Field>
  );
}

function LineRow({
  index,
  control,
  form,
  line,
  onRemove,
  canRemove,
  suggestions,
  onSaveFavourite,
  canFavourite,
  autoFocus,
}: {
  index: number;
  control: Control<RxFormValues>;
  form: UseFormReturn<RxFormValues>;
  line: RxLineValues;
  onRemove: () => void;
  canRemove: boolean;
  suggestions: readonly MedicineDefaults[];
  onSaveFavourite: (line: RxLineValues) => void;
  canFavourite: boolean;
  autoFocus: boolean;
}) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const errors = form.formState.errors.lines?.[index];
  // The generic name is in the details: they open when it is missing, so the doctor sees why the line is refused.
  const showDetails = open || Boolean(errors?.genericName);
  const auto = autoQuantity(line);
  const name = line.drugName.trim() || `Medicine ${index + 1}`;

  const pick = (defaults: MedicineDefaults) => {
    form.setValue(`lines.${index}`, applyDefaults(form.getValues(`lines.${index}`), defaults), { shouldDirty: true });
  };

  return (
    <li
      className={mergeClasses(styles.row, line.isControlledDrug ? styles.rowControlled : undefined)}
      aria-label={name}
    >
      <div className={styles.grid}>
        <span className={styles.number}>{index + 1}.</span>
        <Controller
          control={control}
          name={`lines.${index}.drugName`}
          render={({ field }) => (
            <RowField label="Medicine" error={errors?.drugName?.message} className={styles.drug}>
              <DrugPicker
                ariaLabel={`Medicine ${index + 1}`}
                value={field.value}
                onChange={field.onChange}
                onPick={pick}
                suggestions={suggestions}
                invalid={Boolean(errors?.drugName)}
                autoFocus={autoFocus}
              />
            </RowField>
          )}
        />
        <Controller
          control={control}
          name={`lines.${index}.dose`}
          render={({ field }) => (
            <RowField label="Dose" error={errors?.dose?.message}>
              <Input {...field} aria-label={`${name} dose`} placeholder="1 tab" maxLength={64} />
            </RowField>
          )}
        />
        <div>
          <Controller
            control={control}
            name={`lines.${index}.frequency`}
            render={({ field }) => (
              <RowField label="Frequency" error={errors?.frequency?.message}>
                <Select
                  value={field.value}
                  onChange={(_, d) => field.onChange(d.value as DoseFrequency)}
                  onBlur={field.onBlur}
                  aria-label={`${name} frequency`}
                >
                  {DOSE_FREQUENCIES.map((f) => (
                    <option key={f} value={f}>
                      {frequencyOptionLabel(f)}
                    </option>
                  ))}
                </Select>
              </RowField>
            )}
          />
          {line.frequency === 'Custom' && (
            <Controller
              control={control}
              name={`lines.${index}.customFrequency`}
              render={({ field }) => (
                <Field
                  validationMessage={errors?.customFrequency?.message}
                  validationState={errors?.customFrequency ? 'error' : 'none'}
                >
                  <Input
                    {...field}
                    aria-label={`${name} custom frequency`}
                    placeholder="1-0-1 or 8 hourly"
                    maxLength={64}
                  />
                </Field>
              )}
            />
          )}
        </div>
        <Controller
          control={control}
          name={`lines.${index}.durationDays`}
          render={({ field }) => (
            <RowField label="Days" error={errors?.durationDays?.message}>
              <Combobox
                freeform
                aria-label={`${name} days`}
                disabled={line.frequency === 'STAT'}
                value={line.frequency === 'STAT' ? '1' : field.value === null ? '' : String(field.value)}
                selectedOptions={[]}
                onChange={(event) => {
                  const text = event.target.value.replace(/\D/g, '');
                  field.onChange(text === '' ? null : Number(text));
                }}
                onOptionSelect={(_, d) => d.optionValue && field.onChange(Number(d.optionValue))}
                onBlur={field.onBlur}
                style={{ minWidth: 0 }}
              >
                {DURATION_PRESETS.map((days) => (
                  <Option key={days} value={String(days)} text={String(days)}>
                    {`${days} ${days === 1 ? 'day' : 'days'}`}
                  </Option>
                ))}
              </Combobox>
            </RowField>
          )}
        />
        <Controller
          control={control}
          name={`lines.${index}.quantity`}
          render={({ field }) => (
            <RowField label="Quantity" error={errors?.quantity?.message}>
              <Tooltip
                content={
                  auto !== null
                    ? `Calculated: ${auto} ${quantityUnit(line.dosageForm, line.dose)}. Type to override.`
                    : 'Enter the quantity to dispense (it cannot be calculated for this dose).'
                }
                relationship="description"
              >
                <Input
                  type="number"
                  min={0}
                  aria-label={`${name} quantity`}
                  placeholder={auto !== null ? `${auto}` : 'Qty'}
                  value={field.value === null ? '' : String(field.value)}
                  onChange={(_, d) => field.onChange(d.value === '' ? null : Number(d.value))}
                  onBlur={field.onBlur}
                />
              </Tooltip>
            </RowField>
          )}
        />
        <Controller
          control={control}
          name={`lines.${index}.instructions`}
          render={({ field }) => (
            <RowField label="Instructions" error={errors?.instructions?.message}>
              <Combobox
                freeform
                aria-label={`${name} instructions`}
                placeholder="After food"
                value={field.value}
                selectedOptions={[]}
                onChange={(event) => field.onChange(event.target.value.slice(0, 500))}
                onOptionSelect={(_, d) => d.optionText && field.onChange(d.optionText)}
                onBlur={field.onBlur}
                style={{ minWidth: 0 }}
              >
                {INSTRUCTION_PRESETS.map((preset) => (
                  <Option key={preset}>{preset}</Option>
                ))}
              </Combobox>
            </RowField>
          )}
        />
        <span className={styles.actions}>
          <Tooltip
            content={showDetails ? 'Hide details' : 'Generic name, strength, form, route, Schedule H1/X'}
            relationship="label"
          >
            <Button
              size="small"
              appearance="subtle"
              icon={showDetails ? <ChevronUpRegular /> : <ChevronDownRegular />}
              onClick={() => setOpen(!showDetails)}
              aria-expanded={showDetails}
            />
          </Tooltip>
          {canRemove && (
            <Tooltip content="Remove this medicine" relationship="label">
              <Button size="small" appearance="subtle" icon={<DeleteRegular />} onClick={onRemove} />
            </Tooltip>
          )}
        </span>
      </div>
      {showDetails && (
        <div className={styles.details}>
          <Controller
            control={control}
            name={`lines.${index}.genericName`}
            render={({ field }) => (
              <Field
                label="Generic name / composition"
                required
                validationMessage={errors?.genericName?.message}
                validationState={errors?.genericName ? 'error' : 'none'}
              >
                <Input {...field} maxLength={300} placeholder="Paracetamol" />
              </Field>
            )}
          />
          <Controller
            control={control}
            name={`lines.${index}.strength`}
            render={({ field }) => (
              <Field label="Strength">
                <Input {...field} maxLength={64} placeholder="500 mg" />
              </Field>
            )}
          />
          <Controller
            control={control}
            name={`lines.${index}.dosageForm`}
            render={({ field }) => (
              <Field label="Form">
                <Select value={field.value} onChange={(_, d) => field.onChange(d.value)}>
                  <option value="">—</option>
                  {DOSAGE_FORMS.map((f) => (
                    <option key={f} value={f}>
                      {f}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
          />
          <Controller
            control={control}
            name={`lines.${index}.route`}
            render={({ field }) => (
              <Field label="Route">
                <Select value={field.value} onChange={(_, d) => field.onChange(d.value)}>
                  <option value="">—</option>
                  {ROUTES.map((r) => (
                    <option key={r} value={r}>
                      {r}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
          />
          <Controller
            control={control}
            name={`lines.${index}.isControlledDrug`}
            render={({ field }) => (
              // The field's label names the switch; a label of the switch's own would be a second one. Its state is
              // announced as on or off, so the words beside it are for the eye.
              <Field label="Controlled medicine">
                <div className={styles.switchRow}>
                  <Switch
                    checked={field.value}
                    onChange={(_, d) => {
                      field.onChange(d.checked);
                      if (d.checked && !form.getValues(`lines.${index}.controlledSchedule`)) {
                        form.setValue(`lines.${index}.controlledSchedule`, 'H1', { shouldDirty: true });
                      }
                    }}
                  />
                  <Text aria-hidden>{field.value ? 'Schedule H1/X' : 'No'}</Text>
                </div>
              </Field>
            )}
          />
          {line.isControlledDrug && (
            <Controller
              control={control}
              name={`lines.${index}.controlledSchedule`}
              render={({ field }) => (
                <Field label="Schedule">
                  <Select value={field.value || 'H1'} onChange={(_, d) => field.onChange(d.value)}>
                    <option value="H1">Schedule H1</option>
                    <option value="X">Schedule X</option>
                  </Select>
                </Field>
              )}
            />
          )}
        </div>
      )}
      {line.drugName.trim() && line.dose.trim() && (
        <div className={styles.preview}>
          <Caption1>
            {[line.genericName.trim().toUpperCase(), line.strength].filter((v) => v.trim()).join(' ')}
            {line.genericName.trim() || line.strength.trim() ? ' — ' : ''}
            {directionsText({
              dose: line.dose,
              frequency: line.frequency,
              customFrequency: line.customFrequency,
              durationDays: line.durationDays,
              route: line.route,
              instructions: line.instructions,
            })}
          </Caption1>
          {line.isControlledDrug && (
            <Badge size="small" color="danger" appearance="outline">
              Schedule {line.controlledSchedule || 'H1'}
            </Badge>
          )}
          {canFavourite && (
            <Button size="small" appearance="transparent" icon={<StarRegular />} onClick={() => onSaveFavourite(line)}>
              Save as favourite
            </Button>
          )}
        </div>
      )}
    </li>
  );
}

type CheckState = { response: SafetyCheckResponse; version: number };

/**
 * E-prescription for an encounter (ERX-01..06): medicine rows with dose, frequency, days, quantity and instructions;
 * favourites, template medicines and current medicines in one click; the draft is kept on this computer while typing
 * and on the server when saved. Signing requires a fresh safety check whose alerts are resolved in place.
 */
export function PrescriptionEditor({
  patientId,
  encounterId,
  visitId = null,
  admissionId = null,
  isInpatient = false,
  initialDraft,
  templateMedicines = [],
  currentMedications = [],
  onSigned,
}: {
  patientId: string;
  encounterId: string | null;
  visitId?: string | null;
  admissionId?: string | null;
  isInpatient?: boolean;
  initialDraft: PrescriptionDto | null;
  templateMedicines?: readonly MedicineDefaults[];
  currentMedications?: readonly CurrentMedication[];
  onSigned?: (prescription: PrescriptionDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const practitionerId = useSession((s) => s.user?.practitionerId ?? null);
  const canWrite = usePermission('clinical.prescriptions.write');
  const canSign = usePermission('clinical.prescriptions.sign');
  const [current, setCurrent] = useState<PrescriptionDto | null>(initialDraft);
  const [defaults] = useState<RxFormValues>(() => (initialDraft ? toRxFormValues(initialDraft) : emptyRxForm()));
  const form = useForm<RxFormValues>({ resolver: zodResolver(rxFormSchema), defaultValues: defaults });
  const { fields, append, remove } = useFieldArray({ control: form.control, name: 'lines' });
  const lines = useWatch({ control: form.control, name: 'lines' });
  const draft = useFormDraft(form, `emr-rx:${encounterId ?? initialDraft?.id ?? patientId}`);
  const { isDirty } = form.formState;
  const favourites = useQuery(emrQueries.favourites('Drug'));

  const [check, setCheck] = useState<CheckState | null>(null);
  const [decision, setDecision] = useState<SignDecision>(emptyDecision);
  const [signError, setSignError] = useState<string | null>(null);
  const [signed, setSigned] = useState<PrescriptionDto | null>(null);
  const [focusRow, setFocusRow] = useState<number | null>(null);

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });

  const validValues = async (): Promise<RxFormValues | null> => {
    const ok = await form.trigger();
    if (!ok) {
      notify.warning(
        'Check the highlighted fields',
        'Every medicine needs a name, dose, frequency, days and a quantity when it cannot be calculated.',
      );
      return null;
    }
    return form.getValues();
  };

  const saveDraft = async (values: RxFormValues): Promise<PrescriptionDto> => {
    const body = toRequestBody(values);
    const saved = current
      ? await prescriptionsApi.update(current.id, { ...body, version: current.version })
      : await prescriptionsApi.create({
          patientId,
          encounterId,
          visitId,
          admissionId,
          isInpatient,
          practitionerId: null,
          ...body,
        });
    setCurrent(saved);
    form.reset(values);
    draft.discard();
    invalidate();
    return saved;
  };

  const handleError = (error: unknown, fallback: string) => {
    if (isApiError(error) && error.isConflict && current) {
      // Usually the same doctor in a second window: keep what is typed here and take the latest version number.
      void queryClient
        .fetchQuery({ ...prescriptionQueries.detail(current.id), staleTime: 0 })
        .then((fresh) => {
          setCurrent(fresh);
          setCheck(null);
          notify.warning(
            'This draft was changed in another window',
            'Your version is kept on this screen. Save again to replace the other one.',
          );
        })
        .catch((reloadError: unknown) => notify.error(reloadError, 'Could not reload the prescription'));
    } else {
      notify.error(error, fallback);
    }
  };

  const save = useMutation({
    mutationFn: async () => {
      const values = await validValues();
      return values ? saveDraft(values) : null;
    },
    onSuccess: (saved) =>
      saved && notify.success('Draft saved', `${saved.prescriptionNumber} · not yet sent to the pharmacy`),
    onError: (error) => handleError(error, 'Could not save the prescription'),
  });

  const runCheck = useMutation({
    mutationFn: async (): Promise<CheckState | null> => {
      let rx = current;
      if (!rx || form.formState.isDirty) {
        const values = await validValues();
        if (!values) {
          return null;
        }
        rx = await saveDraft(values);
      }
      const response = await prescriptionsApi.check(rx.id);
      return { response, version: rx.version };
    },
    onSuccess: (result) => {
      if (result) {
        setCheck(result);
        setDecision(emptyDecision);
        setSignError(null);
      }
    },
    onError: (error) => handleError(error, 'The safety check could not run'),
  });

  const stale = check !== null && (isDirty || !current || check.version !== current.version);
  const liveCheck = check && !stale ? check : null;
  const grouped = liveCheck ? groupAlerts(liveCheck.response.alerts) : null;
  const requirements = grouped && liveCheck ? signRequirements(grouped, liveCheck.response.hasControlledDrugs) : null;
  const blockers = requirements ? signBlockers(requirements, decision) : [];
  const notPrescriber = current !== null && practitionerId !== null && current.practitionerId !== practitionerId;

  const sign = useMutation({
    mutationFn: () => {
      if (!current || !requirements) {
        throw new Error('Run the safety check first.');
      }
      return prescriptionsApi.sign(current.id, toSignRequest(requirements, decision, current.version));
    },
    onSuccess: (result) => {
      setSigned(result.prescription);
      setCurrent(null);
      setCheck(null);
      setDecision(emptyDecision);
      setSignError(null);
      draft.discard();
      form.reset(emptyRxForm());
      invalidate();
      notify.success(
        `Prescription ${result.prescription.prescriptionNumber} signed`,
        'It is now in the pharmacy queue.',
      );
      onSigned?.(result.prescription);
    },
    onError: (error) => {
      if (!isApiError(error)) {
        notify.error(error, 'Could not sign the prescription');
        return;
      }
      const alerts = alertsFromProblem(error.code, error.data);
      if (alerts && current) {
        setCheck({
          response: {
            isBlocked: alerts.some((a) => a.isBlocking),
            requiresAcknowledgement: alerts.some((a) => !a.isBlocking && a.kind !== 'ControlledDrug'),
            hasControlledDrugs: current.items.some((i) => i.isControlledDrug),
            alerts,
          },
          version: current.version,
        });
        setDecision((d) => ({ ...d, acknowledgeWarnings: false, confirmControlledDrugs: false }));
        setSignError(
          'The safety check found something new since you reviewed it. Review the alerts below and sign again.',
        );
      } else if (error.code === 'prescription.not_prescriber' || error.isForbidden) {
        setSignError(error.userMessage);
      } else if (error.isConflict) {
        setCheck(null);
        setSignError('The prescription was changed elsewhere. Reload the page, then check and sign again.');
      } else {
        setSignError(error.userMessage);
      }
    },
  });

  const saveFavourite = useMutation({
    mutationFn: (line: RxLineValues) =>
      emrApi.addFavourite({
        ...favouriteFromLine(line),
        kind: 'Drug',
        serviceItemId: null,
        displayOrder: (favourites.data?.length ?? 0) + 1,
      }),
    onSuccess: (favourite) => {
      notify.success('Added to your favourites', favourite.label);
      void queryClient.invalidateQueries({ queryKey: emrKeys.favourites('Drug') });
    },
    onError: (error) => notify.error(error, 'Could not save the favourite'),
  });

  const addLines = (added: RxLineValues[]) => {
    const next = appendLines(form.getValues('lines'), added);
    form.setValue('lines', next, { shouldDirty: true });
  };

  const addBlank = () => {
    append(emptyLine(), { shouldFocus: false });
    setFocusRow(fields.length);
  };

  const duplicates = duplicateGenerics(lines ?? []);
  const favouritesList = favourites.data ?? [];
  const controlledNames = (current?.items ?? []).filter((i) => i.isControlledDrug).map((i) => i.drugName);

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: isDirty, busy: save.isPending || sign.isPending, leaveQuestion: LEAVE_KEEPING_DRAFT });

  if (!canWrite) {
    return <Text className={styles.muted}>You can view prescriptions but not write them.</Text>;
  }

  return (
    <div className={styles.root}>
      {signed && (
        <SignedPrescriptionNotice
          prescription={signed}
          onSignWithDsc={() =>
            void navigate({ to: '/emr/prescriptions/$prescriptionId', params: { prescriptionId: signed.id } })
          }
          onDismiss={() => setSigned(null)}
        />
      )}

      {!signed && (
        <>
          {draft.saved && hasRxContent(draft.saved.value) && !sameRxContent(draft.saved.value, defaults) && (
            <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
              <MessageBarBody>
                Unsaved prescription changes from {formatRelative(new Date(draft.saved.savedAt).toISOString())} were
                found on this computer.
              </MessageBarBody>
              <MessageBarActions>
                <Button onClick={draft.restore}>Restore them</Button>
                <Button appearance="subtle" onClick={draft.discard}>
                  Discard
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}

          {(favouritesList.length > 0 || templateMedicines.length > 0 || currentMedications.length > 0) && (
            <div className={styles.quick}>
              {templateMedicines.length > 0 && (
                <Button
                  size="small"
                  appearance="primary"
                  icon={<AddRegular />}
                  onClick={() => addLines(templateMedicines.map((m) => applyDefaults(emptyLine(), m)))}
                >
                  Template medicines ({templateMedicines.length})
                </Button>
              )}
              {favouritesList.slice(0, 12).map((f) => (
                <Button
                  key={f.id}
                  size="small"
                  icon={<StarRegular />}
                  onClick={() => addLines([applyDefaults(emptyLine(), favouriteDefaults(f))])}
                >
                  {f.label}
                </Button>
              ))}
              {currentMedications.slice(0, 6).map((m) => (
                <Tooltip
                  key={m.itemId}
                  content={`Continue ${m.drugName}: ${m.dose} ${m.frequency} (prescribed by ${m.prescriberName ?? 'a doctor'})`}
                  relationship="description"
                >
                  <Button
                    size="small"
                    appearance="outline"
                    icon={<AddRegular />}
                    onClick={() => addLines([fromCurrentMedication(m)])}
                  >
                    Continue {m.drugName}
                  </Button>
                </Tooltip>
              ))}
            </div>
          )}

          <div className={styles.header} aria-hidden>
            <span />
            <span>Medicine</span>
            <span>Dose</span>
            <span>Frequency</span>
            <span>Days</span>
            <span>Quantity</span>
            <span>Instructions</span>
            <span />
          </div>
          <ol
            style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 8 }}
            aria-label="Medicines"
          >
            {fields.map((field, index) => (
              <LineRow
                key={field.id}
                index={index}
                control={form.control}
                form={form}
                line={lines?.[index] ?? field}
                onRemove={() => remove(index)}
                canRemove={fields.length > 1}
                suggestions={templateMedicines}
                onSaveFavourite={(line) => saveFavourite.mutate(line)}
                canFavourite={
                  !favouritesList.some(
                    (f) => f.label.toLowerCase() === (lines?.[index]?.drugName ?? '').trim().toLowerCase(),
                  )
                }
                autoFocus={focusRow === index}
              />
            ))}
          </ol>
          <div>
            <Button icon={<AddRegular />} onClick={addBlank}>
              Add medicine
            </Button>
          </div>
          {form.formState.errors.lines?.root?.message && (
            <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{form.formState.errors.lines.root.message}</Text>
          )}
          {duplicates.length > 0 && (
            <MessageBar intent="warning">
              <MessageBarBody>
                The same medicine appears more than once: {duplicates.join(', ')}. Check before signing.
              </MessageBarBody>
            </MessageBar>
          )}

          <div className={styles.bottom}>
            <Controller
              control={form.control}
              name="advice"
              render={({ field, fieldState }) => (
                <Field
                  label="Advice for the patient"
                  hint="Printed on the prescription (diet, rest, warning signs)"
                  validationMessage={fieldState.error?.message}
                  validationState={fieldState.error ? 'error' : 'none'}
                >
                  <Textarea {...field} rows={3} resize="vertical" maxLength={2000} />
                </Field>
              )}
            />
            <Controller
              control={form.control}
              name="notes"
              render={({ field, fieldState }) => (
                <Field
                  label="Note for the pharmacist or nurse"
                  validationMessage={fieldState.error?.message}
                  validationState={fieldState.error ? 'error' : 'none'}
                >
                  <Textarea {...field} rows={3} resize="vertical" maxLength={1000} />
                </Field>
              )}
            />
            <Controller
              control={form.control}
              name="refillsAllowed"
              render={({ field }) => (
                <Field label="Refills allowed" hint="0 for none">
                  <SpinButton
                    min={0}
                    max={12}
                    value={field.value}
                    onChange={(_, d) =>
                      field.onChange(Math.min(12, Math.max(0, d.value ?? (Number(d.displayValue) || 0))))
                    }
                  />
                </Field>
              )}
            />
          </div>

          {stale && (
            <MessageBar intent="warning">
              <MessageBarBody>
                The prescription changed after the safety check. Check it again before signing.
              </MessageBarBody>
            </MessageBar>
          )}
          {grouped && requirements && (
            <SafetyReview
              grouped={grouped}
              requirements={requirements}
              decision={decision}
              onDecisionChange={setDecision}
              items={current?.items ?? []}
              controlledItems={controlledNames}
              disabled={sign.isPending}
            />
          )}
          {signError && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Not signed</MessageBarTitle>
                {signError}
              </MessageBarBody>
            </MessageBar>
          )}
          {notPrescriber && (
            <MessageBar intent="info">
              <MessageBarBody>
                Only the prescribing doctor ({current?.practitionerName ?? 'another doctor'}) can sign this
                prescription.
              </MessageBarBody>
            </MessageBar>
          )}

          <div className={styles.buttons}>
            {current && (
              <Caption1 className={styles.muted}>
                Draft {current.prescriptionNumber}
                {isDirty ? ' · unsaved changes' : ' · saved'}
              </Caption1>
            )}
            <Button
              icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
              disabled={save.isPending || runCheck.isPending || sign.isPending}
              onClick={() => save.mutate()}
            >
              Save draft
            </Button>
            <Button
              appearance={liveCheck ? 'secondary' : 'primary'}
              icon={runCheck.isPending ? <Spinner size="tiny" aria-hidden /> : <ShieldCheckmarkRegular />}
              disabled={runCheck.isPending || sign.isPending}
              onClick={() => runCheck.mutate()}
            >
              {liveCheck ? 'Check again' : 'Check safety'}
            </Button>
            {canSign && (
              <Tooltip
                content={
                  !liveCheck
                    ? 'Run the safety check first'
                    : blockers.length > 0
                      ? blockers.join(' ')
                      : 'Sign and send to the pharmacy'
                }
                relationship="description"
              >
                <Button
                  appearance="primary"
                  icon={sign.isPending ? <Spinner size="tiny" aria-hidden /> : <SignatureRegular />}
                  disabled={!liveCheck || blockers.length > 0 || sign.isPending || notPrescriber}
                  onClick={() => sign.mutate()}
                >
                  Sign prescription
                </Button>
              </Tooltip>
            )}
          </div>
          {liveCheck && blockers.length > 0 && <Caption1 className={styles.blockers}>{blockers.join(' ')}</Caption1>}
        </>
      )}
    </div>
  );
}
