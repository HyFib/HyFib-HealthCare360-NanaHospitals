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
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CalendarAddRegular, CheckmarkCircleRegular } from '@fluentui/react-icons';
import { isApiError, todayIso, type Gender } from '@hc360/core';
import { CloseRequestScope, useCloseRequests, useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import type { DuplicateCandidate } from '@/modules/patients/api';
import { crmApi, crmKeys, type LeadConversion, type LeadDetail } from './api';

const useStyles = makeStyles({
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    textAlign: 'center',
    padding: '8px 0',
  },
  uhid: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: '28px',
    lineHeight: '36px',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForeground1,
  },
  candidate: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: '8px',
    alignItems: 'center',
    padding: '8px 10px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'flex-end' },
});

/**
 * Convert a lead: link the patient record (existing or newly registered through the Patients module, with its
 * duplicate check), then book the appointment. Booking an appointment for the lead's number also converts it.
 */
export function ConvertLeadDialog({ open, onClose, lead }: { open: boolean; onClose: () => void; lead: LeadDetail }) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <CloseRequestScope value={closing}>{open && <Body onClose={onClose} lead={lead} />}</CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, lead }: { onClose: () => void; lead: LeadDetail }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRegister = usePermission('patients.register');
  const [mode, setMode] = useState<'existing' | 'register'>(canRegister ? 'register' : 'existing');
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [gender, setGender] = useState<Gender | ''>(lead.gender && lead.gender !== 'Unknown' ? lead.gender : '');
  const [dateOfBirth, setDateOfBirth] = useState('');
  const [age, setAge] = useState(lead.ageYears !== null ? String(lead.ageYears) : '');
  const [candidates, setCandidates] = useState<DuplicateCandidate[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<LeadConversion | null>(null);

  const convert = async (request: Parameters<typeof crmApi.convertLead>[1]) => {
    setBusy(true);
    setError(null);
    try {
      const conversion = await crmApi.convertLead(lead.id, request);
      setResult(conversion);
      setCandidates(null);
      void queryClient.invalidateQueries({ queryKey: crmKeys.all });
    } catch (err) {
      if (isApiError(err) && err.code === 'patient.possible_duplicate' && Array.isArray(err.data)) {
        setCandidates(err.data as DuplicateCandidate[]);
      } else if (isApiError(err) && (err.isValidation || err.status === 422 || err.status === 403)) {
        setError(err.userMessage);
      } else {
        notify.error(err, 'Could not convert the lead');
      }
    } finally {
      setBusy(false);
    }
  };

  const submit = (confirmedNotDuplicate = false) => {
    if (mode === 'existing') {
      if (!patient) {
        setError('Choose the patient.');
        return;
      }
      void convert({ patientId: patient.id, registerPatient: null, appointmentId: null });
      return;
    }
    if (!gender) {
      setError('Choose the gender.');
      return;
    }
    if (!dateOfBirth && !age) {
      setError('Enter the date of birth or the age.');
      return;
    }
    void convert({
      patientId: null,
      registerPatient: {
        gender,
        dateOfBirth: dateOfBirth || null,
        ageYears: dateOfBirth ? null : Number(age),
        confirmedNotDuplicate,
      },
      appointmentId: null,
    });
  };

  const changed = useValuesChanged({ mode, patient, gender, dateOfBirth, age });
  const guard = useUnsavedChanges({ dirty: changed && !result, busy, onClose });

  if (result) {
    return (
      <DialogBody>
        <DialogTitle>Lead converted</DialogTitle>
        <DialogContent>
          <div className={styles.done} role="status">
            <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
            <Text weight="semibold">{lead.name}</Text>
            {result.uhid && (
              <>
                <Caption1>UHID</Caption1>
                <span className={styles.uhid}>{result.uhid}</span>
              </>
            )}
            <Text>Book the appointment now so the patient gets the SMS confirmation.</Text>
          </div>
        </DialogContent>
        <DialogActions fluid>
          {result.patientId && (
            <Button
              onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: result.patientId! } })}
            >
              Open patient
            </Button>
          )}
          <Button onClick={onClose}>Done</Button>
          {result.patientId && (
            <Button
              appearance="primary"
              icon={<CalendarAddRegular />}
              onClick={() =>
                void navigate({
                  to: '/scheduling/book',
                  search: { patientId: result.patientId!, departmentId: lead.interestDepartmentId ?? undefined },
                })
              }
            >
              Book appointment
            </Button>
          )}
        </DialogActions>
      </DialogBody>
    );
  }

  return (
    <>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
        noValidate
      >
        <DialogBody>
          <DialogTitle>
            Convert {lead.name}
            <Caption1 block>{lead.leadNumber}</Caption1>
          </DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <RadioGroup value={mode} onChange={(_, d) => setMode(d.value as 'existing' | 'register')}>
              {canRegister && <Radio value="register" label="Register as a new patient (issues a UHID)" />}
              <Radio value="existing" label="Link to an already registered patient" />
            </RadioGroup>
            {mode === 'existing' ? (
              <PatientPicker value={patient} onChange={setPatient} required />
            ) : (
              <>
                <Caption1>Name, mobile, email and city are taken from the lead.</Caption1>
                <Field label="Gender" required>
                  <RadioGroup layout="horizontal" value={gender} onChange={(_, d) => setGender(d.value as Gender)}>
                    <Radio value="Female" label="Female" />
                    <Radio value="Male" label="Male" />
                    <Radio value="Other" label="Other" />
                  </RadioGroup>
                </Field>
                <div className={styles.row}>
                  <Field label="Date of birth">
                    <Input
                      type="date"
                      value={dateOfBirth}
                      max={todayIso()}
                      onChange={(_, d) => setDateOfBirth(d.value)}
                    />
                  </Field>
                  <Text>or</Text>
                  <Field label="Age (years)">
                    <Input
                      type="number"
                      min={0}
                      max={130}
                      value={age}
                      disabled={Boolean(dateOfBirth)}
                      onChange={(_, d) => setAge(d.value)}
                    />
                  </Field>
                </div>
              </>
            )}
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            {candidates && (
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>This person may already be registered</MessageBarTitle>
                  Registering the same person twice splits their medical history. Use the existing record if it is the
                  same person.
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 6, marginTop: 8 }}>
                    {candidates.map((c) => (
                      <div key={c.id} className={styles.candidate}>
                        <span>
                          <Text weight="semibold">{c.fullName}</Text>
                          <Caption1 block>
                            {c.uhid} · {c.age ?? '—'} / {c.gender.charAt(0)}
                            {c.phoneMasked ? ` · ${c.phoneMasked}` : ''} · {c.reason}
                          </Caption1>
                        </span>
                        <Button
                          size="small"
                          disabled={busy}
                          onClick={() => void convert({ patientId: c.id, registerPatient: null, appointmentId: null })}
                        >
                          Use this patient
                        </Button>
                      </div>
                    ))}
                  </div>
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={busy}>
              Cancel
            </Button>
            {candidates ? (
              <Button
                appearance="primary"
                disabled={busy}
                icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
                onClick={() => submit(true)}
              >
                It is a different person, register
              </Button>
            ) : (
              <Button
                type="submit"
                appearance="primary"
                disabled={busy}
                icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {mode === 'register' ? 'Register and convert' : 'Convert'}
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </form>
      {guard.dialogs}
    </>
  );
}
