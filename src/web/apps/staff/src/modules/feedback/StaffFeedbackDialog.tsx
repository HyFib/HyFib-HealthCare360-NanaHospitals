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
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
} from '@fluentui/react-components';
import { isApiError, normalizePhone } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { patientQueries, type VisitListItem } from '@/modules/patients/api';
import { feedbackApi, feedbackKeys, feedbackQueries, type FeedbackFormDto } from './api';
import { FeedbackQuestions } from './FeedbackQuestions';
import { formTypeLabel } from './labels';
import { EMPTY_ANSWERS, isLowScore, toRatingsInput, type FeedbackAnswers } from './rules';
import { VisitSelect } from './VisitSelect';

interface Props {
  open: boolean;
  onClose: () => void;
  /** Fixed patient (for example from a follow-up call); leave out to pick one. */
  patient?: PickedPatient | null;
  /** The visit the feedback is about, when known. */
  visitId?: string | null;
}

/**
 * Feedback taken by staff on the patient's behalf, for example during a follow-up call. A low score raises a complaint
 * straight away, exactly as when the patient answers a QR code or link.
 */
export function StaffFeedbackDialog({ open, onClose, patient, visitId }: Props) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 680 }}>
        {open && <Body onClose={onClose} fixedPatient={patient ?? null} fixedVisitId={visitId ?? null} />}
      </DialogSurface>
    </Dialog>
  );
}

/** Prefers the facility's form for the kind of visit (in-patient or out-patient), then a general form. */
function defaultForm(forms: FeedbackFormDto[], visit: VisitListItem | null): FeedbackFormDto | undefined {
  const preferred = visit?.type === 'InPatient' ? 'IP' : 'OP';
  return forms.find((f) => f.type === preferred) ?? forms.find((f) => f.type === 'General') ?? forms[0];
}

type Errors = Partial<
  Record<'patientId' | 'visitId' | 'formId' | 'ratings' | 'nps' | 'comment' | 'contactPhone', string>
>;

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
  const queryClient = useQueryClient();
  const forms = useQuery(feedbackQueries.forms({ activeOnly: true }));
  const [patient, setPatient] = useState<PickedPatient | null>(fixedPatient);
  const [visit, setVisit] = useState<VisitListItem | null>(null);
  const [chosenFormId, setChosenFormId] = useState<string | null>(null);
  const [answers, setAnswers] = useState<FeedbackAnswers>(EMPTY_ANSWERS);
  const [wantsCallback, setWantsCallback] = useState(false);
  const [phone, setPhone] = useState('');
  const [errors, setErrors] = useState<Errors>({});
  const [busy, setBusy] = useState(false);

  // A fixed visit (from a follow-up call) is looked up in the patient's recent visits to choose the IP or OP form.
  const recentVisits = useQuery({
    ...patientQueries.visits(fixedPatient?.id ?? '', 1),
    enabled: Boolean(fixedVisitId && fixedPatient),
  });
  const fixedVisit = recentVisits.data?.items.find((v) => v.id === fixedVisitId) ?? null;

  const available = forms.data ?? [];
  const form = available.find((f) => f.id === chosenFormId) ?? defaultForm(available, visit ?? fixedVisit);
  const ratings = form ? toRatingsInput(form.aspects, answers.ratings) : null;
  const nps = form?.includesNps ? answers.nps : null;
  const comment = form?.allowsComment ? answers.comment.trim() : '';
  const willRaiseComplaint = isLowScore(Object.values(ratings ?? {}), nps);

  const save = async () => {
    const next: Errors = {};
    if (!patient) next.patientId = 'Choose the patient.';
    if (!form) next.formId = 'Choose the feedback form.';
    if (!ratings && nps === null && !comment) next.ratings = 'Answer at least one question.';
    if (wantsCallback && normalizePhone(phone) === null) next.contactPhone = 'Enter the number to call back on.';
    setErrors(next);
    if (!patient || !form || Object.keys(next).length > 0) {
      return;
    }
    setBusy(true);
    try {
      const outcome = await feedbackApi.recordByStaff({
        formId: form.id,
        patientId: patient.id,
        visitId: fixedVisitId ?? visit?.id ?? null,
        ratings,
        nps,
        comment: comment || null,
        wantsCallback,
        contactPhone: wantsCallback ? normalizePhone(phone) : null,
      });
      if (outcome.complaintNumber) {
        notify.warning(
          'Feedback recorded; complaint raised',
          `Complaint ${outcome.complaintNumber} has gone to the complaint handlers.`,
        );
      } else {
        notify.success('Feedback recorded', `Thank you noted for ${patient.fullName}.`);
      }
      void queryClient.invalidateQueries({ queryKey: feedbackKeys.all });
      void queryClient.invalidateQueries({ queryKey: ['crm', 'patient360', patient.id] });
      onClose();
    } catch (error) {
      if (isApiError(error) && error.isValidation) {
        setErrors(
          Object.fromEntries(
            Object.entries(error.fieldErrors).map(([key, messages]) => [key, messages[0] ?? '']),
          ) as Errors,
        );
      } else {
        notify.error(error, 'Could not record the feedback');
      }
    } finally {
      setBusy(false);
    }
  };

  const formsForbidden = isApiError(forms.error) && forms.error.status === 403;

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Record feedback for the patient</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <Caption1>
            Read each question out and record the patient's own answer. Leave out anything they do not want to answer.
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
              hint="The feedback is counted for the visit's department."
              validationMessage={errors.visitId}
            />
          )}

          {forms.isPending ? (
            <Spinner size="small" label="Loading feedback forms…" />
          ) : formsForbidden ? (
            <MessageBar intent="warning">
              <MessageBarBody>
                You need the “View feedback” permission to see the hospital's feedback forms. Ask an administrator.
              </MessageBarBody>
            </MessageBar>
          ) : forms.error ? (
            <MessageBar intent="error">
              <MessageBarBody>Could not load the feedback forms.</MessageBarBody>
            </MessageBar>
          ) : available.length === 0 ? (
            <MessageBar intent="warning">
              <MessageBarBody>
                There is no active feedback form. A feedback manager can add one under Feedback › Forms.
              </MessageBarBody>
            </MessageBar>
          ) : (
            <Field
              label="Feedback form"
              required
              validationMessage={errors.formId}
              validationState={errors.formId ? 'error' : 'none'}
            >
              <Select value={form?.id ?? ''} onChange={(_, d) => setChosenFormId(d.value || null)}>
                {available.map((f) => (
                  <option key={f.id} value={f.id}>
                    {f.title} ({formTypeLabel[f.type]})
                  </option>
                ))}
              </Select>
            </Field>
          )}

          {form && (
            <FeedbackQuestions
              aspects={form.aspects}
              includesNps={form.includesNps}
              allowsComment={form.allowsComment}
              answers={answers}
              onChange={setAnswers}
              disabled={busy}
              errors={errors}
            />
          )}

          {form && (
            <>
              <Switch
                checked={wantsCallback}
                onChange={(_, d) => setWantsCallback(d.checked)}
                label="The patient would like a call back"
              />
              {wantsCallback && (
                <Field
                  label="Call back on"
                  required
                  hint="The patient gets an SMS acknowledgement if this feedback raises a complaint."
                  validationMessage={errors.contactPhone}
                  validationState={errors.contactPhone ? 'error' : 'none'}
                >
                  <Input
                    type="tel"
                    value={phone}
                    onChange={(_, d) => setPhone(d.value)}
                    maxLength={20}
                    autoComplete="off"
                  />
                </Field>
              )}
            </>
          )}

          {willRaiseComplaint && (
            <MessageBar intent="warning">
              <MessageBarBody>
                These answers include a low score (a rating of 2 or less, or a recommendation of 6 or less). Saving
                raises a complaint for the complaint handlers to follow up.
              </MessageBarBody>
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
            disabled={busy || !form}
            icon={busy ? <Spinner size="tiny" /> : undefined}
          >
            Save feedback
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
