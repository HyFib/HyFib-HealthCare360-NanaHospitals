import { isApiError } from '@hc360/core';
import type { Tone } from '@/components/ListRow';
import type { MessageKey } from '@/i18n';
import { isValidMobile, sanitizePhoneInput, toE164 } from '@/lib/otp';
import type { ComplaintStatus, FeedbackAspect, RatingsInput, SubmitFeedbackRequest } from '@/lib/types';

export const COMMENT_MAX = 2000;

export const ASPECT_KEY: Record<FeedbackAspect, MessageKey> = {
  Overall: 'feedback.aspect.Overall',
  Doctor: 'feedback.aspect.Doctor',
  Nursing: 'feedback.aspect.Nursing',
  FrontDesk: 'feedback.aspect.FrontDesk',
  Billing: 'feedback.aspect.Billing',
  Cleanliness: 'feedback.aspect.Cleanliness',
  Food: 'feedback.aspect.Food',
};

export const COMPLAINT_STATUS: Record<ComplaintStatus, { key: MessageKey; tone: Tone }> = {
  Open: { key: 'feedback.complaint.Open', tone: 'warning' },
  InProgress: { key: 'feedback.complaint.InProgress', tone: 'brand' },
  Resolved: { key: 'feedback.complaint.Resolved', tone: 'success' },
  Closed: { key: 'feedback.complaint.Closed', tone: 'neutral' },
};

/** What the form asks (any feedback form: portal, personal link, QR poster or kiosk). */
export interface FeedbackQuestions {
  aspects: readonly FeedbackAspect[];
  includesNps: boolean;
  allowsComment: boolean;
}

export interface FeedbackAnswers {
  ratings: Partial<Record<FeedbackAspect, number | null>>;
  nps: number | null;
  comment: string;
  wantsCallback: boolean;
  contactPhone: string;
}

export function emptyAnswers(contactPhone = ''): FeedbackAnswers {
  return { ratings: {}, nps: null, comment: '', wantsCallback: false, contactPhone };
}

const isRating = (value: number | null | undefined): value is number =>
  typeof value === 'number' && Number.isInteger(value) && value >= 1 && value <= 5;
const isNps = (value: number | null | undefined): value is number =>
  typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= 10;

/** At least one question answered (the server's rule: a rating, a score or a comment). */
export function hasAnswer(answers: FeedbackAnswers, questions: FeedbackQuestions): boolean {
  return (
    questions.aspects.some((aspect) => isRating(answers.ratings[aspect])) ||
    (questions.includesNps && isNps(answers.nps)) ||
    (questions.allowsComment && answers.comment.trim().length > 0)
  );
}

/** The server's field name for an aspect ("FrontDesk" → "frontDesk"). */
export function ratingField(aspect: FeedbackAspect): keyof RatingsInput {
  return `${aspect.charAt(0).toLowerCase()}${aspect.slice(1)}` as keyof RatingsInput;
}

/**
 * The request body: only questions this form asks, only valid answers, and a phone number only when the patient
 * asked for a call-back (anonymous feedback carries nothing else about the person).
 */
export function toFeedbackRequest(answers: FeedbackAnswers, questions: FeedbackQuestions): SubmitFeedbackRequest {
  const ratings: RatingsInput = {};
  let rated = false;
  for (const aspect of questions.aspects) {
    const value = answers.ratings[aspect];
    if (isRating(value)) {
      ratings[ratingField(aspect)] = value;
      rated = true;
    }
  }
  const comment = questions.allowsComment ? answers.comment.trim() : '';
  const phone = sanitizePhoneInput(answers.contactPhone);
  return {
    ratings: rated ? ratings : null,
    nps: questions.includesNps && isNps(answers.nps) ? answers.nps : null,
    comment: comment ? comment.slice(0, COMMENT_MAX) : null,
    wantsCallback: answers.wantsCallback,
    contactPhone: answers.wantsCallback && isValidMobile(phone) ? toE164(phone) : null,
  };
}

/** Field problems before sending (message keys); empty when the answers can be sent. */
export function checkAnswers(
  answers: FeedbackAnswers,
  questions: FeedbackQuestions,
): Partial<Record<'form' | 'comment' | 'contactPhone', MessageKey>> {
  const problems: Partial<Record<'form' | 'comment' | 'contactPhone', MessageKey>> = {};
  if (!hasAnswer(answers, questions)) {
    problems.form = 'feedback.empty';
  }
  if (answers.comment.trim().length > COMMENT_MAX) {
    problems.comment = 'feedback.comment.tooLong';
  }
  if (answers.wantsCallback) {
    const phone = sanitizePhoneInput(answers.contactPhone);
    if (!phone) {
      problems.contactPhone = 'feedback.phone.required';
    } else if (!isValidMobile(phone)) {
      problems.contactPhone = 'feedback.phone.invalid';
    }
  }
  return problems;
}

export type LinkProblem = 'used' | 'expired' | 'invalid';

/**
 * Why a personal feedback link cannot be used: already answered (422 `feedback.link_used`, or 409 when two taps raced),
 * older than 14 days (422 `feedback.link_expired`), or not a link at all (404). Anything else is a passing problem.
 */
export function linkProblem(error: unknown): LinkProblem | null {
  if (!isApiError(error)) {
    return null;
  }
  if (error.status === 404) {
    return 'invalid';
  }
  if (error.code === 'feedback.link_used' || error.status === 409) {
    return 'used';
  }
  return error.code === 'feedback.link_expired' ? 'expired' : null;
}
