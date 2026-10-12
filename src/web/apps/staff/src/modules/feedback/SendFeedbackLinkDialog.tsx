import {
  Body1,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
} from '@fluentui/react-components';
import { SendRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { ignoreEscapeWhile } from '@/lib/dialogFocus';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import type { VisitListItem } from '@/modules/patients/api';
import { feedbackApi, feedbackQueries } from './api';
import { formTypeLabel } from './labels';
import { VisitSelect } from './VisitSelect';

interface Props {
  open: boolean;
  onClose: () => void;
  /** Fixed patient (for example from a follow-up call); leave out to pick one. */
  patient?: PickedPatient | null;
  /** Fixed visit; leave out to pick one of the patient's visits. */
  visitId?: string | null;
}

/** Plain-words help for the reasons the server refuses a link. */
const REFUSALS: Record<string, string> = {
  'feedback.no_consent':
    'The patient has not agreed to SMS updates, so no link can be sent. You can record their feedback by phone instead.',
  'feedback.no_phone':
    'The patient has no mobile number on record. Update the registration, or record their feedback by phone instead.',
  'feedback.already_invited': 'A feedback link has already been sent for this visit. Each visit gets one link.',
  'feedback.no_form':
    'There is no active feedback form for this kind of visit. A feedback manager can add one under Feedback › Forms.',
  'feedback.no_portal_url':
    "The hospital's portal address is not set up yet, so links cannot be built. Ask an administrator to set 'portal.baseUrl'.",
  'feedback.patient_inactive': 'Feedback links are not sent for this patient record.',
};

/**
 * Sends the patient a personal, single-use feedback link by SMS for one of their visits. The link expires after 14 days.
 * In-patient stays are invited automatically on discharge.
 */
export function SendFeedbackLinkDialog({ open, onClose, patient, visitId }: Props) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        {open && <Body onClose={onClose} fixedPatient={patient ?? null} fixedVisitId={visitId ?? null} />}
      </DialogSurface>
    </Dialog>
  );
}

function Body({
  onClose,
  fixedPatient,
  fixedVisitId,
}: {
  onClose: () => void;
  fixedPatient: PickedPatient | null;
  fixedVisitId: string | null;
}) {
  const notify = useNotify();
  const forms = useQuery({ ...feedbackQueries.forms({ activeOnly: true }), retry: false });
  const [patient, setPatient] = useState<PickedPatient | null>(fixedPatient);
  const [visit, setVisit] = useState<VisitListItem | null>(null);
  const [formId, setFormId] = useState('');
  const [errors, setErrors] = useState<Partial<Record<'patientId' | 'visitId', string>>>({});
  const [refusal, setRefusal] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const send = async () => {
    const chosenVisit = fixedVisitId ?? visit?.id ?? null;
    const next: typeof errors = {};
    if (!patient) next.patientId = 'Choose the patient.';
    if (!chosenVisit) next.visitId = 'Choose the visit the feedback is about.';
    setErrors(next);
    setRefusal(null);
    if (!patient || !chosenVisit) {
      return;
    }
    setBusy(true);
    try {
      await feedbackApi.sendLink(patient.id, chosenVisit, formId || null);
      notify.success(
        'Feedback link sent',
        `${patient.fullName} will get the link by SMS. It can be used once, within 14 days.`,
      );
      onClose();
    } catch (error) {
      if (isApiError(error) && error.code && REFUSALS[error.code]) {
        setRefusal(REFUSALS[error.code] ?? error.userMessage);
      } else if (isApiError(error) && error.isValidation) {
        setErrors({ patientId: error.fieldErrors.patientId?.[0], visitId: error.fieldErrors.visitId?.[0] });
      } else {
        notify.error(error, 'Could not send the feedback link');
      }
    } finally {
      setBusy(false);
    }
  };

  // Staff without feedback permissions can still send a link with the automatic form.
  const available = forms.data ?? [];

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (!busy) {
          void send();
        }
      }}
      onKeyDown={ignoreEscapeWhile(busy)}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Send a feedback link</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <Caption1>
            The patient gets a personal link by SMS. It works once and expires after 14 days. Links go only to patients
            who agreed to SMS updates.
          </Caption1>
          {fixedPatient ? (
            <Body1>
              <strong>{fixedPatient.fullName}</strong> · {fixedPatient.uhid}
            </Body1>
          ) : (
            <PatientPicker
              value={patient}
              onChange={(p) => {
                setPatient(p);
                setVisit(null);
                setRefusal(null);
              }}
              required
              validationMessage={errors.patientId}
            />
          )}
          {patient && !fixedVisitId && (
            <VisitSelect
              patientId={patient.id}
              value={visit?.id ?? null}
              onChange={setVisit}
              required
              validationMessage={errors.visitId}
            />
          )}
          {available.length > 0 && (
            <Field
              label="Feedback form"
              hint="Automatic uses the hospital's form for the kind of visit (in-patient or out-patient)."
            >
              <Select value={formId} onChange={(_, d) => setFormId(d.value)}>
                <option value="">Automatic</option>
                {available.map((f) => (
                  <option key={f.id} value={f.id}>
                    {f.title} ({formTypeLabel[f.type]})
                  </option>
                ))}
              </Select>
            </Field>
          )}
          {refusal && (
            <MessageBar intent="warning">
              <MessageBarBody>{refusal}</MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={busy}
            // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={busy}
            icon={busy ? <Spinner size="tiny" aria-hidden /> : <SendRegular />}
          >
            Send link
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
