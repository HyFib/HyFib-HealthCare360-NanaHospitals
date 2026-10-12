import {
  Button,
  Checkbox,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Option,
  Radio,
  RadioGroup,
  Spinner,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { CallRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { useDebouncedValue, type LookupItem } from '@hc360/core';
import { FormGrid } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { searchPractitioners } from '@/lib/lookups';
import { fromZonedInput, isInFuture, toZonedInput } from './datetime';
import type { CommunicationMethod, CriticalCallDetails } from './types';

const METHODS: { value: CommunicationMethod; label: string }[] = [
  { value: 'Phone', label: 'Phone' },
  { value: 'InPerson', label: 'In person' },
  { value: 'VideoCall', label: 'Video call' },
  { value: 'SecureMessage', label: 'Secure message' },
];

const ROLES = ['Treating doctor', 'Duty doctor', 'Staff nurse', 'Ward in-charge'] as const;

const schema = z
  .object({
    informedPersonName: z.string().trim().min(1, 'Enter who was informed.').max(200),
    informedPersonRole: z.string().trim().max(100),
    informedPractitionerId: z.string().nullable(),
    method: z.enum(['Phone', 'InPerson', 'VideoCall', 'SecureMessage']),
    informedAt: z.string().min(1, 'Enter when the call was made.'),
    readBackConfirmed: z.boolean(),
    notes: z.string().trim().max(1000),
  })
  .superRefine((values, ctx) => {
    const instant = fromZonedInput(values.informedAt);
    if (!instant) {
      ctx.addIssue({ code: 'custom', path: ['informedAt'], message: 'Enter the date and time of the call.' });
    } else if (isInFuture(instant, new Date())) {
      ctx.addIssue({ code: 'custom', path: ['informedAt'], message: 'The call time cannot be in the future.' });
    }
    if (!values.readBackConfirmed) {
      ctx.addIssue({
        code: 'custom',
        path: ['readBackConfirmed'],
        message: 'Ask the person to read the value back to you, then tick this box.',
      });
    }
  });

type CallValues = z.infer<typeof schema>;

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '14px' },
  list: { margin: '4px 0 0', paddingLeft: '18px' },
  roles: { display: 'flex', gap: '6px', flexWrap: 'wrap', marginTop: '6px' },
  timeRow: { display: 'flex', gap: '8px', alignItems: 'flex-end' },
  readBack: {
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
});

export interface CriticalFinding {
  id: string;
  /** What is critical, in words: "Potassium 6.9 mmol/L (critically high)". */
  label: string;
}

/**
 * Critical value / critical finding call log (LIS-05, NABH): whom the result was communicated to, how, when, and the
 * read-back confirmation. The server refuses sign-off of a critical result without it, so this dialog is shown
 * automatically instead of letting the verification fail.
 */
export function CriticalCallDialog({
  open,
  findings,
  title = 'Inform the doctor about the critical result',
  intro,
  submitLabel = 'Save the call',
  cancelLabel = 'Cancel',
  busy,
  onSubmit,
  onCancel,
}: {
  open: boolean;
  findings: CriticalFinding[];
  title?: string;
  intro?: string;
  submitLabel?: string;
  cancelLabel?: string;
  busy?: boolean;
  onSubmit: (call: CriticalCallDetails) => void;
  onCancel: () => void;
}) {
  const styles = useStyles();
  const form = useForm<CallValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      informedPersonName: '',
      informedPersonRole: '',
      informedPractitionerId: null,
      method: 'Phone',
      informedAt: toZonedInput(new Date()),
      readBackConfirmed: false,
      notes: '',
    },
  });

  const submit = form.handleSubmit((values) => {
    // The confirm button stays focusable while busy, so Enter can still submit: one call record at a time.
    if (busy) {
      return;
    }
    onSubmit({
      informedPersonName: values.informedPersonName,
      informedPersonRole: values.informedPersonRole || null,
      informedPractitionerId: values.informedPractitionerId,
      method: values.method,
      informedAt: fromZonedInput(values.informedAt) ?? new Date().toISOString(),
      readBackConfirmed: values.readBackConfirmed,
      notes: values.notes || null,
    });
  });

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && !busy && onCancel()} modalType="alert">
      <DialogSurface style={{ maxWidth: 640 }}>
        <form onSubmit={(event) => void submit(event)} noValidate>
          <DialogBody>
            <DialogTitle>{title}</DialogTitle>
            <DialogContent className={styles.content}>
              <MessageBar intent="error" layout="multiline" icon={<CallRegular />}>
                <MessageBarBody>
                  <MessageBarTitle>
                    {findings.length === 1 ? 'Critical result' : `${findings.length} critical results`}
                  </MessageBarTitle>
                  {intro ??
                    'Call the treating doctor now. Read out the result and ask them to read it back before you save.'}
                  <ul className={styles.list}>
                    {findings.map((finding) => (
                      <li key={finding.id}>
                        <strong>{finding.label}</strong>
                      </li>
                    ))}
                  </ul>
                </MessageBarBody>
              </MessageBar>

              <FormGrid columns={2}>
                <Controller
                  control={form.control}
                  name="informedPersonName"
                  render={({ field, fieldState }) => (
                    <Field
                      label="Person informed"
                      required
                      validationMessage={fieldState.error?.message}
                      validationState={fieldState.error ? 'error' : 'none'}
                    >
                      <Input {...field} autoFocus placeholder="e.g. Dr. Meena Iyer" maxLength={200} />
                    </Field>
                  )}
                />
                <Controller
                  control={form.control}
                  name="informedPractitionerId"
                  render={({ field }) => (
                    <PractitionerSearch
                      value={field.value}
                      onChange={(item) => {
                        field.onChange(item?.id ?? null);
                        if (item && !form.getValues('informedPersonName')) {
                          form.setValue('informedPersonName', item.name, { shouldValidate: true });
                        }
                      }}
                    />
                  )}
                />
              </FormGrid>

              <Controller
                control={form.control}
                name="informedPersonRole"
                render={({ field }) => (
                  <Field label="Role">
                    <Input {...field} placeholder="e.g. Treating doctor" maxLength={100} />
                    <div className={styles.roles}>
                      {ROLES.map((role) => (
                        <Button
                          key={role}
                          size="small"
                          shape="circular"
                          appearance={field.value === role ? 'primary' : 'outline'}
                          onClick={() => field.onChange(role)}
                        >
                          {role}
                        </Button>
                      ))}
                    </div>
                  </Field>
                )}
              />

              <FormGrid columns={2}>
                <Controller
                  control={form.control}
                  name="method"
                  render={({ field }) => (
                    <Field label="How">
                      <RadioGroup
                        layout="horizontal"
                        value={field.value}
                        onChange={(_, data) => field.onChange(data.value)}
                      >
                        {METHODS.map((method) => (
                          <Radio key={method.value} value={method.value} label={method.label} />
                        ))}
                      </RadioGroup>
                    </Field>
                  )}
                />
                <Controller
                  control={form.control}
                  name="informedAt"
                  render={({ field, fieldState }) => (
                    <Field
                      label="When (hospital time)"
                      required
                      validationMessage={fieldState.error?.message}
                      validationState={fieldState.error ? 'error' : 'none'}
                    >
                      <div className={styles.timeRow}>
                        <Input
                          type="datetime-local"
                          value={field.value}
                          onChange={(_, data) => field.onChange(data.value)}
                          onBlur={field.onBlur}
                          style={{ flex: 1 }}
                        />
                        <Button onClick={() => field.onChange(toZonedInput(new Date()))}>Now</Button>
                      </div>
                    </Field>
                  )}
                />
              </FormGrid>

              <Controller
                control={form.control}
                name="readBackConfirmed"
                render={({ field, fieldState }) => (
                  <Field
                    validationMessage={fieldState.error?.message}
                    validationState={fieldState.error ? 'error' : 'none'}
                  >
                    <div className={styles.readBack}>
                      <Checkbox
                        checked={field.value}
                        onChange={(_, data) => field.onChange(data.checked === true)}
                        label="The person read the result back to me correctly"
                      />
                    </div>
                  </Field>
                )}
              />

              <Controller
                control={form.control}
                name="notes"
                render={({ field }) => (
                  <Field label="Notes">
                    <Textarea
                      {...field}
                      rows={2}
                      maxLength={1000}
                      resize="vertical"
                      placeholder="e.g. Doctor will review the patient now"
                    />
                  </Field>
                )}
              />
            </DialogContent>
            <DialogActions>
              <Button onClick={onCancel} disabled={busy}>
                {cancelLabel}
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={busy}
                // Keeps focus while busy: a disabled button drops it.
                disabledFocusable={busy}
                icon={busy ? <Spinner size="tiny" aria-hidden /> : <CallRegular />}
              >
                {submitLabel}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** Optional link to the doctor's record in the system (fills the name when it is still empty). */
function PractitionerSearch({
  value,
  onChange,
}: {
  value: string | null;
  onChange: (item: LookupItem | null) => void;
}) {
  const [term, setTerm] = useState('');
  const [selected, setSelected] = useState<LookupItem | null>(null);
  const search = useDebouncedValue(term.trim(), 250);
  const results = useQuery({
    queryKey: ['lookups', 'practitioners', 'search', search],
    queryFn: () => searchPractitioners(search),
    enabled: search.length >= 2,
    staleTime: 60_000,
  });
  const items = results.data ?? [];

  return (
    <Field label="Doctor in the system" hint="Optional: links the call to the doctor's record">
      <Combobox
        placeholder="Type 2 letters to search"
        value={term || (value && selected?.id === value ? selected.name : '')}
        selectedOptions={value ? [value] : []}
        onChange={(event) => {
          setTerm(event.target.value);
          if (!event.target.value) {
            setSelected(null);
            onChange(null);
          }
        }}
        onOptionSelect={(_, data) => {
          const item = items.find((i) => i.id === data.optionValue) ?? null;
          setSelected(item);
          setTerm('');
          onChange(item);
        }}
        clearable
      >
        {items.map((item) => (
          <Option key={item.id} value={item.id} text={item.name}>
            {item.name}
          </Option>
        ))}
        {search.length >= 2 && items.length === 0 && !results.isFetching && (
          <Option value="" disabled text="No match">
            No doctor found
          </Option>
        )}
      </Combobox>
    </Field>
  );
}
