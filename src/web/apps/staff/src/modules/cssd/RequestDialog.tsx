import {
  Button,
  Caption1,
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
  Select,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import { applyServerErrors, ChoiceField, SelectField, TextAreaField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useFieldArray, useForm } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments } from '@/lib/lookups';
import { useStoredState } from '@/modules/inpatient/hooks';
import { fromIstInput, toIstInput } from '@/modules/inpatient/time';
import { cssdApi, cssdKeys, cssdQueries } from './api';

const useStyles = makeStyles({
  line: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 96px auto', gap: '8px', alignItems: 'end' },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const schema = z
  .object({
    departmentId: z.string().nullable(),
    requiredBy: z.string(),
    priority: z.enum(['Routine', 'Urgent']),
    notes: z.string().trim().max(500),
    lines: z.array(
      z.object({
        instrumentSetId: z.string(),
        quantity: z.number().int().min(1, 'At least 1.').max(50, 'At most 50.').nullable(),
      }),
    ),
  })
  .superRefine((v, ctx) => {
    if (!v.departmentId) {
      ctx.addIssue({ code: 'custom', path: ['departmentId'], message: 'Choose the requesting department.' });
    }
    const filled = v.lines.filter((l) => l.instrumentSetId);
    if (filled.length === 0) {
      ctx.addIssue({ code: 'custom', path: ['lines'], message: 'Request at least one set.' });
    }
    v.lines.forEach((l, i) => {
      if (l.instrumentSetId && !l.quantity) {
        ctx.addIssue({ code: 'custom', path: ['lines', i, 'quantity'], message: 'Enter how many.' });
      }
    });
  });

type RequestValues = z.infer<typeof schema>;

/** Department request for sterile sets (OT, wards, labour room). Urgent requests alert CSSD at once. */
export function RequestDialog({ onClose }: { onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const sets = useQuery(cssdQueries.sets());
  const [lastDepartment, setLastDepartment] = useStoredState('hc360:cssd:request-department');
  const [minRequired] = useState(() => toIstInput(new Date()));
  const form = useForm<RequestValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      departmentId: lastDepartment || null,
      requiredBy: '',
      priority: 'Routine',
      notes: '',
      lines: [{ instrumentSetId: '', quantity: 1 }],
    },
  });
  const lines = useFieldArray({ control: form.control, name: 'lines' });

  const create = useMutation({
    mutationFn: (v: RequestValues) =>
      cssdApi.createRequest({
        departmentId: v.departmentId!,
        requiredBy: v.requiredBy ? fromIstInput(v.requiredBy) : null,
        priority: v.priority,
        notes: v.notes || null,
        lines: v.lines
          .filter((l) => l.instrumentSetId && l.quantity)
          .map((l) => ({ instrumentSetId: l.instrumentSetId, quantity: l.quantity! })),
      }),
    onSuccess: (request, v) => {
      setLastDepartment(v.departmentId ?? '');
      notify.success(
        `Request ${request.requestNumber} sent to CSSD`,
        request.priority === 'Urgent' ? 'CSSD has been alerted.' : undefined,
      );
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      onClose();
    },
    onError: (error) => applyServerErrors(error, form.setError),
  });

  const setOptions = (sets.data ?? []).map((s) => ({
    value: s.id,
    label: `${s.name} (${s.code}) · ${s.packsAvailable} on shelf`,
  }));
  const serverError = create.error && !(isApiError(create.error) && create.error.isValidation) ? create.error : null;

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !create.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <form onSubmit={form.handleSubmit((v) => create.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>Request sterile sets</DialogTitle>
            <DialogContent className={styles.stack}>
              <SelectField
                control={form.control}
                name="departmentId"
                label="For department"
                required
                placeholder="Choose department"
                options={toOptions(departments.data)}
              />
              <Field
                label="Sets"
                required
                validationMessage={form.formState.errors.lines?.message}
                validationState={form.formState.errors.lines?.message ? 'error' : 'none'}
              >
                <div className={styles.stack}>
                  {lines.fields.map((line, index) => (
                    <div key={line.id} className={styles.line}>
                      <Controller
                        control={form.control}
                        name={`lines.${index}.instrumentSetId`}
                        render={({ field }) => (
                          <Select
                            value={field.value}
                            onChange={(_, d) => field.onChange(d.value)}
                            aria-label={`Set ${index + 1}`}
                          >
                            <option value="">Choose a set</option>
                            {setOptions.map((o) => (
                              <option key={o.value} value={o.value}>
                                {o.label}
                              </option>
                            ))}
                          </Select>
                        )}
                      />
                      <Controller
                        control={form.control}
                        name={`lines.${index}.quantity`}
                        render={({ field, fieldState }) => (
                          <Field
                            validationMessage={fieldState.error?.message}
                            validationState={fieldState.error ? 'error' : 'none'}
                          >
                            <Input
                              type="number"
                              min={1}
                              max={50}
                              aria-label={`Quantity for set ${index + 1}`}
                              value={field.value === null ? '' : String(field.value)}
                              onChange={(_, d) => field.onChange(d.value === '' ? null : Number(d.value))}
                            />
                          </Field>
                        )}
                      />
                      <Button
                        appearance="subtle"
                        icon={<DeleteRegular />}
                        aria-label={`Remove set ${index + 1}`}
                        disabled={lines.fields.length === 1}
                        onClick={() => lines.remove(index)}
                      />
                    </div>
                  ))}
                  <div>
                    <Button
                      size="small"
                      icon={<AddRegular />}
                      onClick={() => lines.append({ instrumentSetId: '', quantity: 1 })}
                    >
                      Add another set
                    </Button>
                  </div>
                </div>
              </Field>
              <Controller
                control={form.control}
                name="requiredBy"
                render={({ field }) => (
                  <Field label="Needed by" hint="Leave blank if not time-bound">
                    <Input
                      type="datetime-local"
                      value={field.value}
                      min={minRequired}
                      onChange={(_, d) => field.onChange(d.value)}
                    />
                  </Field>
                )}
              />
              <ChoiceField
                control={form.control}
                name="priority"
                label="Priority"
                options={[
                  { value: 'Routine', label: 'Routine' },
                  { value: 'Urgent', label: 'Urgent (alerts CSSD)' },
                ]}
              />
              <TextAreaField
                control={form.control}
                name="notes"
                label="Notes"
                rows={2}
                maxLength={500}
                placeholder="e.g. For LSCS list at 2 pm, OT 3"
              />
              {sets.data && sets.data.length === 0 && (
                <Caption1 className={styles.muted}>No instrument sets are set up yet.</Caption1>
              )}
              {serverError && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {isApiError(serverError) ? serverError.userMessage : 'The request could not be sent.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={create.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={create.isPending}
                icon={create.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Send request
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
