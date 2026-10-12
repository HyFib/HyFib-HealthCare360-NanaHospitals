import {
  Button,
  Caption1,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  Label,
  makeStyles,
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import {
  CloseRequestScope,
  FormGrid,
  TextAreaField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { Controller, useFieldArray, useForm, useWatch, type FieldPath } from 'react-hook-form';
import { usePermission } from '@/lib/permissions';
import { patientQueries } from '@/modules/patients/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { privacyApi, privacyKeys, type CreatedDataRequest } from './api';
import { intakeDefaults, intakeFieldFor, intakeSchema, toCreateRequest, type IntakeValues } from './intake';
import {
  correctableFieldLabel,
  MAX_CORRECTIONS,
  REQUEST_TYPES,
  requestChannelLabel,
  REQUESTER_RELATIONS,
  requesterRelationHelp,
  requesterRelationLabel,
  requestTypeHelp,
  requestTypeLabel,
  STAFF_CHANNELS,
  STAFF_IDENTITY_CHECKS,
  identityCheckLabel,
  staffIdentityCheckHelp,
  type DataRequestType,
} from './labels';
import { availableCorrectionFields, canAddCorrection } from './rules';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '16px' },
  group: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    paddingTop: '12px',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  groupTitle: { fontWeight: tokens.fontWeightSemibold },
  choices: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))', gap: '2px 16px' },
  choiceLabel: { display: 'flex', flexDirection: 'column', gap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
  change: {
    display: 'grid',
    gridTemplateColumns: 'minmax(150px, 220px) minmax(0, 1fr) auto',
    gap: '8px',
    alignItems: 'start',
    '@media (max-width: 560px)': { gridTemplateColumns: 'minmax(0, 1fr) auto' },
  },
  changes: { display: 'flex', flexDirection: 'column', gap: '8px' },
  linkButton: { alignSelf: 'flex-start' },
});

const detailsHint: Record<DataRequestType, string> = {
  Access: 'In their words, for example: they want to know what we hold, or about a particular visit.',
  Correction: 'Anything else about the changes. Optional when the changes are listed above.',
  Erasure: 'What they want erased, and why, in their words.',
  Grievance: 'What the complaint is about, in their words: what happened and when.',
};

/**
 * Log a privacy request received at the desk, by e-mail, letter or phone. The grievance officer is alerted, and the
 * person gets the request number and reply-by date by SMS or e-mail where the hospital has them.
 */
export function RequestIntakeDialog({
  open,
  onClose,
  onCreated,
  patientId,
}: {
  open: boolean;
  onClose: () => void;
  onCreated: (created: CreatedDataRequest) => void;
  /** Starts with this patient chosen (from the patient record). */
  patientId?: string;
}) {
  const [busy, setBusy] = useState(false);
  const body = { busy, setBusy, onClose, onCreated };
  const closing = useCloseRequests({ onClose, busy });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <CloseRequestScope value={closing}>
          {open &&
            (patientId ? (
              <WithPatient {...body} patientId={patientId} />
            ) : (
              <IntakeBody {...body} initialPatient={null} />
            ))}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

interface BodyProps {
  busy: boolean;
  setBusy: (busy: boolean) => void;
  onClose: () => void;
  onCreated: (created: CreatedDataRequest) => void;
}

/** Loads the patient the request is for, then opens the form with them chosen. */
function WithPatient({ patientId, ...props }: BodyProps & { patientId: string }) {
  const canReadPatients = usePermission('patients.read');
  const patient = useQuery({
    ...patientQueries.detail(patientId),
    enabled: canReadPatients,
    // Usually already loaded by the patient record the request is logged from.
    staleTime: 5 * 60_000,
    retry: false,
  });
  if (canReadPatients && patient.isLoading) {
    return (
      <DialogBody>
        <DialogTitle>Log a privacy request</DialogTitle>
        <DialogContent>
          <Spinner size="small" label="Loading the patient…" labelPosition="after" />
        </DialogContent>
      </DialogBody>
    );
  }
  const p = patient.data;
  return <IntakeBody {...props} initialPatient={p ? { id: p.id, uhid: p.uhid, fullName: p.fullName } : null} />;
}

function IntakeBody({
  busy,
  setBusy,
  onClose,
  onCreated,
  initialPatient,
}: BodyProps & { initialPatient: PickedPatient | null }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [showContact, setShowContact] = useState(false);
  const changesLabelId = useId();
  const changesHintId = useId();
  const form = useForm<IntakeValues>({
    resolver: zodResolver(intakeSchema),
    defaultValues: { ...intakeDefaults(), patient: initialPatient },
  });
  const changes = useFieldArray({ control: form.control, name: 'corrections' });
  const [notRegistered, type, requestedBy, identityCheckedNow, rows] = useWatch({
    control: form.control,
    name: ['notRegistered', 'type', 'requestedBy', 'identityCheckedNow', 'corrections'],
  });
  const errors = form.formState.errors;
  const contactVisible = notRegistered || showContact || Boolean(errors.contactPhone || errors.contactEmail);
  const chosenFields = rows.map((row) => row.field);
  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy, onClose });

  const submit = async (values: IntakeValues) => {
    setBusy(true);
    try {
      const created = await privacyApi.createRequest(toCreateRequest(values));
      void queryClient.invalidateQueries({ queryKey: privacyKeys.requests });
      void queryClient.invalidateQueries({ queryKey: privacyKeys.overview });
      onCreated(created);
    } catch (error) {
      let applied = false;
      if (isApiError(error) && error.isValidation) {
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          const path = intakeFieldFor(key);
          if (path) {
            form.setError(path as FieldPath<IntakeValues>, { type: 'server', message: messages[0] });
            applied = true;
          }
        }
      }
      if (!applied) {
        notify.error(error, 'Could not log the request');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={form.handleSubmit(submit)} noValidate>
      <DialogBody>
        <DialogTitle>Log a privacy request</DialogTitle>
        <DialogContent className={styles.content}>
          <div className={styles.changes}>
            {!notRegistered && (
              <Controller
                control={form.control}
                name="patient"
                render={({ field, fieldState }) => (
                  <PatientPicker
                    label="Who is the request about?"
                    required
                    autoFocus={!initialPatient}
                    value={field.value}
                    onChange={field.onChange}
                    validationMessage={fieldState.error?.message}
                    hint="Search by UHID, mobile number or name."
                  />
                )}
              />
            )}
            <Controller
              control={form.control}
              name="notRegistered"
              render={({ field }) => (
                <Checkbox
                  checked={field.value}
                  onChange={(_, d) => field.onChange(Boolean(d.checked))}
                  label="The person is not a registered patient"
                />
              )}
            />
            {notRegistered && (
              <TextField
                control={form.control}
                name="principalName"
                label="Name of the person the request is about"
                required
                autoFocus
                maxLength={200}
                autoComplete="off"
              />
            )}
            {contactVisible ? (
              <FormGrid columns={2} minColumnWidth={220}>
                <TextField
                  control={form.control}
                  name="contactPhone"
                  label={notRegistered ? 'Phone number to reply to' : 'Reply to this phone number instead'}
                  type="tel"
                  maxLength={20}
                  autoComplete="off"
                  hint={notRegistered ? 'A phone number or an e-mail address is needed.' : undefined}
                />
                <TextField
                  control={form.control}
                  name="contactEmail"
                  label={notRegistered ? 'E-mail address to reply to' : 'Reply to this e-mail address instead'}
                  type="email"
                  maxLength={200}
                  autoComplete="off"
                />
              </FormGrid>
            ) : (
              <Button appearance="transparent" className={styles.linkButton} onClick={() => setShowContact(true)}>
                Reply to a different phone number or e-mail address
              </Button>
            )}
            {contactVisible && !notRegistered && (
              <Caption1 className={styles.muted}>
                Leave these empty to reply to the patient’s registered mobile number and e-mail address.
              </Caption1>
            )}
          </div>

          <div className={styles.group}>
            <Controller
              control={form.control}
              name="type"
              render={({ field, fieldState }) => (
                <Field
                  label="What are they asking for?"
                  required
                  validationMessage={fieldState.error?.message}
                  validationState={fieldState.error ? 'error' : 'none'}
                >
                  <RadioGroup
                    value={field.value ?? ''}
                    onChange={(_, d) => {
                      field.onChange(d.value);
                      if (d.value === 'Correction' && changes.fields.length === 0) {
                        changes.append({ field: '', requestedValue: '' });
                      }
                    }}
                    className={styles.choices}
                  >
                    {REQUEST_TYPES.map((t) => (
                      <Radio
                        key={t}
                        value={t}
                        label={
                          <span className={styles.choiceLabel}>
                            <Text weight="semibold">{requestTypeLabel[t]}</Text>
                            <Caption1 className={styles.muted}>{requestTypeHelp[t]}</Caption1>
                          </span>
                        }
                      />
                    ))}
                  </RadioGroup>
                </Field>
              )}
            />

            {type === 'Correction' && (
              <div
                role="group"
                aria-labelledby={changesLabelId}
                aria-describedby={changesHintId}
                className={styles.changes}
              >
                <Label id={changesLabelId}>Changes they ask for</Label>
                <div className={styles.changes}>
                  {changes.fields.map((change, index) => {
                    const current = rows[index]?.field ?? '';
                    const options = availableCorrectionFields(chosenFields, current);
                    const detailName = current ? correctableFieldLabel[current] : `change ${index + 1}`;
                    return (
                      <div key={change.id} className={styles.change}>
                        <Controller
                          control={form.control}
                          name={`corrections.${index}.field`}
                          render={({ field, fieldState }) => (
                            <Field
                              validationMessage={fieldState.error?.message}
                              validationState={fieldState.error ? 'error' : 'none'}
                            >
                              <Select
                                value={field.value}
                                onChange={(_, d) => field.onChange(d.value)}
                                onBlur={field.onBlur}
                                ref={field.ref}
                                aria-label={`Detail to correct, change ${index + 1}`}
                              >
                                <option value="">Choose the detail</option>
                                {options.map((option) => (
                                  <option key={option} value={option}>
                                    {correctableFieldLabel[option]}
                                  </option>
                                ))}
                              </Select>
                            </Field>
                          )}
                        />
                        <Controller
                          control={form.control}
                          name={`corrections.${index}.requestedValue`}
                          render={({ field, fieldState }) => (
                            <Field
                              validationMessage={fieldState.error?.message}
                              validationState={fieldState.error ? 'error' : 'none'}
                            >
                              <Input
                                value={field.value}
                                onChange={(_, d) => field.onChange(d.value)}
                                onBlur={field.onBlur}
                                ref={field.ref}
                                maxLength={500}
                                placeholder="The correct value"
                                aria-label={`Correct value for ${detailName}`}
                              />
                            </Field>
                          )}
                        />
                        <Button
                          appearance="subtle"
                          icon={<DeleteRegular />}
                          aria-label={`Remove ${detailName}`}
                          onClick={() => changes.remove(index)}
                        />
                      </div>
                    );
                  })}
                  <Button
                    size="small"
                    icon={<AddRegular />}
                    className={styles.linkButton}
                    disabled={!canAddCorrection(changes.fields.length)}
                    onClick={() => changes.append({ field: '', requestedValue: '' })}
                  >
                    Add a change
                  </Button>
                </div>
                <Caption1 id={changesHintId} className={styles.muted}>
                  Up to {MAX_CORRECTIONS} changes, each detail once. Registration details are changed in the patient
                  record once accepted; the treating doctor answers changes to the health record.
                </Caption1>
              </div>
            )}

            <TextAreaField
              control={form.control}
              name="details"
              label={type === 'Correction' ? 'Anything else' : 'What they ask'}
              required={type !== null && type !== 'Correction'}
              rows={3}
              maxLength={4000}
              hint={type ? detailsHint[type] : 'Choose what they are asking for first.'}
            />
          </div>

          <div className={styles.group}>
            <Controller
              control={form.control}
              name="channel"
              render={({ field }) => (
                <Field label="How did it reach us?">
                  <RadioGroup layout="horizontal" value={field.value} onChange={(_, d) => field.onChange(d.value)}>
                    {STAFF_CHANNELS.map((c) => (
                      <Radio key={c} value={c} label={requestChannelLabel[c]} />
                    ))}
                  </RadioGroup>
                </Field>
              )}
            />
            <FormGrid columns={2} minColumnWidth={220}>
              <Controller
                control={form.control}
                name="requestedBy"
                render={({ field }) => (
                  <Field label="Who is asking?" hint={requesterRelationHelp[field.value]}>
                    <Select
                      value={field.value}
                      onChange={(_, d) => field.onChange(d.value)}
                      onBlur={field.onBlur}
                      ref={field.ref}
                    >
                      {REQUESTER_RELATIONS.map((r) => (
                        <option key={r} value={r}>
                          {requesterRelationLabel[r]}
                        </option>
                      ))}
                    </Select>
                  </Field>
                )}
              />
              {requestedBy !== 'Self' && (
                <TextField
                  control={form.control}
                  name="requesterName"
                  label="Their name"
                  required
                  maxLength={200}
                  autoComplete="off"
                />
              )}
            </FormGrid>
          </div>

          <div className={styles.group}>
            <Controller
              control={form.control}
              name="identityCheckedNow"
              render={({ field }) => (
                <Checkbox
                  checked={field.value}
                  onChange={(_, d) => field.onChange(Boolean(d.checked))}
                  label="I have checked who is asking, and that they may act for the patient"
                />
              )}
            />
            {identityCheckedNow ? (
              <>
                <Controller
                  control={form.control}
                  name="identityCheck"
                  render={({ field, fieldState }) => (
                    <Field
                      label="How?"
                      required
                      validationMessage={fieldState.error?.message}
                      validationState={fieldState.error ? 'error' : 'none'}
                    >
                      <RadioGroup value={field.value ?? ''} onChange={(_, d) => field.onChange(d.value)}>
                        {STAFF_IDENTITY_CHECKS.map((check) => (
                          <Radio
                            key={check}
                            value={check}
                            label={
                              <span className={styles.choiceLabel}>
                                <Text>{identityCheckLabel[check]}</Text>
                                <Caption1 className={styles.muted}>{staffIdentityCheckHelp[check]}</Caption1>
                              </span>
                            }
                          />
                        ))}
                      </RadioGroup>
                    </Field>
                  )}
                />
                <TextField
                  control={form.control}
                  name="identityNote"
                  label="Note"
                  maxLength={500}
                  hint="Say how, if it was not a photo ID or someone staff know. Do not write down ID numbers."
                />
              </>
            ) : (
              <Caption1 className={styles.muted}>
                Checking now saves the grievance officer a step. No data is given out, changed or erased until the
                identity has been checked.
              </Caption1>
            )}
          </div>

          <Caption1 className={styles.muted}>
            The grievance officer is alerted with the request number only. The person gets the number and the reply-by
            date by SMS or e-mail.
          </Caption1>
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            Log request
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
