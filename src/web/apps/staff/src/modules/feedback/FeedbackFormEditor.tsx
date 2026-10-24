import {
  Body1,
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Subtitle2,
  Switch,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { SaveRegular } from '@fluentui/react-icons';
import { isApiError, useHotkey } from '@hc360/core';
import { Section, useLeaveGuard, useNotify, type LeaveQuestion } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { useBaseVersion } from '@/lib/baseVersion';
import { toOptions, useDepartments } from '@/lib/lookups';
import { MainAside, Stack } from '@/modules/hr/shared/layout';
import { feedbackApi, feedbackKeys, feedbackQueries, type FeedbackFormDto, type SaveFeedbackFormRequest } from './api';
import { FeedbackQuestions } from './FeedbackQuestions';
import { ASPECTS, aspectLabel, FORM_TYPES, formTypeLabel, type FeedbackAspect, type FeedbackFormType } from './labels';
import { EMPTY_ANSWERS, type FeedbackAnswers } from './rules';

const useStyles = makeStyles({
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: '12px' },
  aspects: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))',
    padding: '4px 8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  preview: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  thanks: {
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorPaletteGreenBackground1,
  },
  footer: { display: 'flex', gap: '8px', alignItems: 'center' },
});

interface Draft {
  code: string;
  title: string;
  type: FeedbackFormType;
  departmentId: string;
  aspects: FeedbackAspect[];
  includesNps: boolean;
  allowsComment: boolean;
  introText: string;
  thankYouText: string;
  isActive: boolean;
}

const NEW_FORM: Draft = {
  code: '',
  title: '',
  type: 'OP',
  departmentId: '',
  aspects: ['Doctor', 'Nursing', 'FrontDesk', 'Billing', 'Cleanliness', 'Overall'],
  includesNps: true,
  allowsComment: true,
  introText: '',
  thankYouText: '',
  isActive: true,
};

function toDraft(form: FeedbackFormDto | null): Draft {
  if (!form) {
    return NEW_FORM;
  }
  return {
    code: form.code,
    title: form.title,
    type: form.type,
    departmentId: form.departmentId ?? '',
    aspects: ASPECTS.filter((a) => form.aspects.includes(a)),
    includesNps: form.includesNps,
    allowsComment: form.allowsComment,
    introText: form.introText ?? '',
    thankYouText: form.thankYouText ?? '',
    isActive: form.isActive,
  };
}

function sameDraft(a: Draft, b: Draft): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

type Errors = Partial<Record<keyof Draft, string>>;

const LEAVE_FORM: LeaveQuestion = {
  title: 'Leave without saving?',
  body: 'Your changes to this feedback form are not saved.',
  confirmLabel: 'Discard changes',
  destructive: true,
};

function validate(d: Draft): Errors {
  const errors: Errors = {};
  const code = d.code.trim();
  if (!code) errors.code = 'Enter a short code.';
  else if (code.length > 32) errors.code = 'At most 32 characters.';
  else if (!/^[A-Za-z0-9_-]+$/.test(code)) errors.code = "Use letters, digits, '-' or '_'.";
  if (!d.title.trim()) errors.title = 'Enter the title patients see.';
  if (d.aspects.length === 0 && !d.includesNps && !d.allowsComment) errors.aspects = 'Ask at least one question.';
  return errors;
}

/**
 * Builds a feedback questionnaire (FB-01): which aspects are rated 1–5, whether the 0–10 recommendation question and a
 * comment box are shown, and the texts around them, with a live preview of what patients see.
 */
export function FeedbackFormEditor({
  form,
  canManage,
  onSaved,
  aside,
  ownChange,
}: {
  form: FeedbackFormDto | null;
  canManage: boolean;
  onSaved: (id: string) => void;
  /** Extra content above the preview (for example the QR panel of a saved form). */
  aside?: ReactNode;
  /** The form as this manager's latest change to it elsewhere on the page returned it (a new QR code). */
  ownChange?: FeedbackFormDto | null;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const [draft, setDraft] = useState<Draft>(() => toDraft(form));
  // The form as the editor was filled, then as this manager saved it: its version goes with the save. The page reads
  // it again in the background, and a version read so must never be sent with this draft (REL-04). The editor follows
  // this manager's own changes elsewhere on the page while the form they return still holds what it was filled from.
  const [loaded, setLoaded] = useState(form);
  const [version, setVersion] = useBaseVersion(form?.version ?? null, ownChange, (returned) =>
    sameDraft(toDraft(returned), toDraft(loaded)),
  );
  const [errors, setErrors] = useState<Errors>({});
  const [busy, setBusy] = useState(false);
  const [previewAnswers, setPreviewAnswers] = useState<FeedbackAnswers>(EMPTY_ANSWERS);

  // Compared with what the editor was filled from, which saving fills again.
  const dirty = canManage && !sameDraft(draft, toDraft(loaded));
  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  const leave = useLeaveGuard({ dirty, busy, leaveQuestion: LEAVE_FORM });

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => {
    setDraft((d) => ({ ...d, [key]: value }));
    setErrors((e) => ({ ...e, [key]: undefined }));
  };

  const toggleAspect = (aspect: FeedbackAspect, on: boolean) => {
    setDraft((d) => ({ ...d, aspects: ASPECTS.filter((a) => (a === aspect ? on : d.aspects.includes(a))) }));
    setErrors((e) => ({ ...e, aspects: undefined }));
  };

  /** The save answers with nothing: the editor is filled again from the form read after it, version included. */
  const refill = async (id: string) => {
    try {
      const saved = await queryClient.fetchQuery({ ...feedbackQueries.form(id), staleTime: 0 });
      setLoaded(saved);
      setDraft(toDraft(saved));
      setVersion(saved.version);
    } catch {
      // Not read: the editor keeps the version it had, so its next save is refused rather than saved over a change.
    }
  };

  const save = async () => {
    const found = validate(draft);
    setErrors(found);
    if (Object.keys(found).length > 0) {
      return;
    }
    const request: SaveFeedbackFormRequest = {
      code: draft.code.trim().toUpperCase(),
      title: draft.title.trim(),
      type: draft.type,
      aspects: draft.aspects,
      includesNps: draft.includesNps,
      allowsComment: draft.allowsComment,
      introText: draft.introText.trim() || null,
      thankYouText: draft.thankYouText.trim() || null,
      departmentId: draft.departmentId || null,
      isActive: draft.isActive,
      version,
    };
    setBusy(true);
    try {
      if (loaded) {
        await feedbackApi.updateForm(loaded.id, request);
        notify.success(
          'Form saved',
          draft.isActive ? 'Patients see the changes straight away.' : 'The form is inactive.',
        );
        await refill(loaded.id);
        await queryClient.invalidateQueries({ queryKey: feedbackKeys.forms });
      } else {
        const id = await feedbackApi.createForm(request);
        notify.success('Form created', 'Print its QR code for posters and kiosks.');
        void queryClient.invalidateQueries({ queryKey: feedbackKeys.forms });
        leave.allowLeaving();
        onSaved(id);
      }
    } catch (error) {
      if (isApiError(error) && error.code === 'feedback.form_code_taken') {
        setErrors({ code: error.userMessage });
      } else if (isApiError(error) && error.code === 'concurrency') {
        notify.warning(
          'Someone else changed this form',
          'Reload the page to see their changes, then make yours again.',
        );
        // Read again for the page, which fills the editor afresh when opened again; the editor keeps refusing until then.
        void queryClient.invalidateQueries({ queryKey: feedbackKeys.forms });
      } else if (isApiError(error) && error.isValidation) {
        setErrors(
          Object.fromEntries(
            Object.entries(error.fieldErrors).map(([key, messages]) => [key, messages[0] ?? '']),
          ) as Errors,
        );
      } else {
        notify.error(error, 'Could not save the form');
      }
    } finally {
      setBusy(false);
    }
  };

  useHotkey('mod+s', () => void save(), { allowInInputs: true, enabled: canManage && !busy });

  const disabled = !canManage || busy;

  return (
    <>
      <MainAside>
        <Section
          title="Questionnaire"
          description={canManage ? 'Changes apply to every QR poster, kiosk and link of this form.' : undefined}
        >
          <form
            onSubmit={(event) => {
              event.preventDefault();
              void save();
            }}
            noValidate
          >
            <Stack gap={14}>
              <div className={styles.row}>
                <Field
                  label="Code"
                  required
                  hint="Short and unique, for example OPD-GEN."
                  validationMessage={errors.code}
                  validationState={errors.code ? 'error' : 'none'}
                >
                  <Input
                    value={draft.code}
                    onChange={(_, d) => set('code', d.value)}
                    maxLength={32}
                    disabled={disabled}
                    style={{ textTransform: 'uppercase' }}
                  />
                </Field>
                <Field label="Used for" required>
                  <Select
                    value={draft.type}
                    onChange={(_, d) => set('type', d.value as FeedbackFormType)}
                    disabled={disabled}
                  >
                    {FORM_TYPES.map((t) => (
                      <option key={t} value={t}>
                        {formTypeLabel[t]}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>
              <Field
                label="Title patients see"
                required
                validationMessage={errors.title}
                validationState={errors.title ? 'error' : 'none'}
              >
                <Input
                  value={draft.title}
                  onChange={(_, d) => set('title', d.value)}
                  maxLength={150}
                  disabled={disabled}
                  placeholder="How was your visit today?"
                />
              </Field>
              <Field
                label="Department"
                hint="Feedback through this form's QR code is counted for this department. Leave empty for the whole hospital."
                validationMessage={errors.departmentId}
                validationState={errors.departmentId ? 'error' : 'none'}
              >
                <Select
                  value={draft.departmentId}
                  onChange={(_, d) => set('departmentId', d.value)}
                  disabled={disabled}
                >
                  <option value="">Whole hospital</option>
                  {toOptions(departments.data).map((o) => (
                    <option key={o.value} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field
                label="Rated from 1 to 5 stars"
                validationMessage={errors.aspects}
                validationState={errors.aspects ? 'error' : 'none'}
              >
                <div className={styles.aspects}>
                  {ASPECTS.map((aspect) => (
                    <Checkbox
                      key={aspect}
                      label={aspectLabel[aspect]}
                      checked={draft.aspects.includes(aspect)}
                      onChange={(_, d) => toggleAspect(aspect, Boolean(d.checked))}
                      disabled={disabled}
                    />
                  ))}
                </div>
              </Field>
              <Switch
                checked={draft.includesNps}
                onChange={(_, d) => set('includesNps', d.checked)}
                disabled={disabled}
                label="Ask how likely they are to recommend us (0–10, for the NPS)"
              />
              <Switch
                checked={draft.allowsComment}
                onChange={(_, d) => set('allowsComment', d.checked)}
                disabled={disabled}
                label="Offer a comment box"
              />
              <Field
                label="Opening text"
                hint="Shown above the questions. Up to 500 characters."
                validationMessage={errors.introText}
              >
                <Textarea
                  value={draft.introText}
                  onChange={(_, d) => set('introText', d.value)}
                  maxLength={500}
                  rows={2}
                  disabled={disabled}
                />
              </Field>
              <Field
                label="Thank-you text"
                hint="Shown after the answers are sent. Leave empty for the standard thank-you."
                validationMessage={errors.thankYouText}
              >
                <Textarea
                  value={draft.thankYouText}
                  onChange={(_, d) => set('thankYouText', d.value)}
                  maxLength={500}
                  rows={2}
                  disabled={disabled}
                />
              </Field>
              <Switch
                checked={draft.isActive}
                onChange={(_, d) => set('isActive', d.checked)}
                disabled={disabled}
                label={
                  draft.isActive ? 'Active: patients can answer it' : 'Inactive: its QR code and links show “not valid”'
                }
              />
              {canManage && (
                <div className={styles.footer}>
                  <Button
                    type="submit"
                    appearance="primary"
                    icon={busy ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
                    disabled={busy || (!!form && !dirty)}
                  >
                    {form ? 'Save changes' : 'Create form'}
                  </Button>
                  {dirty && <Caption1>Unsaved changes · Ctrl+S saves</Caption1>}
                </div>
              )}
            </Stack>
          </form>
        </Section>
        <Stack>
          {aside}
          <Section title="What patients see" description="Try the questions; nothing is sent from this preview.">
            <div className={styles.preview}>
              <Subtitle2>{draft.title.trim() || 'Form title'}</Subtitle2>
              {draft.introText.trim() && <Body1>{draft.introText.trim()}</Body1>}
              {draft.aspects.length === 0 && !draft.includesNps && !draft.allowsComment ? (
                <MessageBar intent="warning">
                  <MessageBarBody>The form has no questions yet.</MessageBarBody>
                </MessageBar>
              ) : (
                <FeedbackQuestions
                  aspects={draft.aspects}
                  includesNps={draft.includesNps}
                  allowsComment={draft.allowsComment}
                  answers={previewAnswers}
                  onChange={setPreviewAnswers}
                />
              )}
              <Caption1 className={styles.thanks}>
                {draft.thankYouText.trim() || 'Thank you for your feedback. It helps us care for you better.'}
              </Caption1>
            </div>
          </Section>
        </Stack>
      </MainAside>
    </>
  );
}
