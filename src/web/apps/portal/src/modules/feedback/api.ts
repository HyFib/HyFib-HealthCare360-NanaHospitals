import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { withProof } from '@/lib/proofOfWork';
import type {
  CommunicationPreferences,
  CommunicationPreferencesRequest,
  FeedbackReceipt,
  PortalFeedbackForm,
  PortalFeedbackItem,
  PortalFeedbackRequest,
  PortalFeedbackResult,
  PublicFeedbackForm,
  SubmitFeedbackRequest,
} from '@/lib/types';

const token = (value: string) => encodeURIComponent(value);

export const feedbackQueries = {
  forms: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'feedback', 'forms'],
      queryFn: ({ signal }) => api.get<PortalFeedbackForm[]>('/api/v1/portal/feedback/forms', { signal }),
      staleTime: 10 * 60_000,
    }),

  mine: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'feedback', 'mine'],
      queryFn: ({ signal }) =>
        api.get<PortalFeedbackItem[]>('/api/v1/portal/feedback', { query: { patientId }, signal }),
    }),

  preferences: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'preferences'],
      queryFn: ({ signal }) =>
        api.get<CommunicationPreferences>('/api/v1/portal/communication-preferences', { query: { patientId }, signal }),
    }),
};

export const feedbackApi = {
  submit: (request: PortalFeedbackRequest) => api.post<PortalFeedbackResult>('/api/v1/portal/feedback', request),
  savePreferences: (request: CommunicationPreferencesRequest) =>
    api.put<void>('/api/v1/portal/communication-preferences', request),
};

/** Anonymous links and QR codes (no sign-in; the token only identifies the form or the invitation). */
export const publicFeedbackQueries = {
  form: (formToken: string) =>
    queryOptions({
      queryKey: ['public', 'feedback', 'form', formToken],
      queryFn: ({ signal }) =>
        api.get<PublicFeedbackForm>(`/api/v1/public/feedback/forms/${token(formToken)}`, { signal }),
      staleTime: Infinity,
      retry: false,
    }),

  invitation: (invitationToken: string) =>
    queryOptions({
      queryKey: ['public', 'feedback', 'invitation', invitationToken],
      queryFn: ({ signal }) =>
        api.get<PublicFeedbackForm>(`/api/v1/public/feedback/invitations/${token(invitationToken)}`, { signal }),
      staleTime: Infinity,
      retry: false,
    }),

  optOut: (optOutToken: string) =>
    queryOptions({
      queryKey: ['public', 'unsubscribe', optOutToken],
      queryFn: ({ signal }) =>
        api.get<{ alreadyOptedOut: boolean }>(`/api/v1/public/unsubscribe/${token(optOutToken)}`, { signal }),
      retry: false,
    }),
};

/**
 * Sending feedback asks for a proof of work (SRS SEC-06, lib/proofOfWork): the poster's form is open to anyone who saw
 * the poster. `onSlow` says when the check takes longer than usual, for "Checking your browser…".
 */
export const publicFeedbackApi = {
  submitForm: (formToken: string, body: SubmitFeedbackRequest, kiosk: boolean, onSlow?: (slow: boolean) => void) =>
    withProof(
      'feedback',
      (headers) =>
        api.post<FeedbackReceipt>(`/api/v1/public/feedback/forms/${token(formToken)}`, body, {
          query: { kiosk: kiosk ? true : undefined },
          headers,
        }),
      { onSlow },
    ),
  submitInvitation: (invitationToken: string, body: SubmitFeedbackRequest, onSlow?: (slow: boolean) => void) =>
    withProof(
      'feedback',
      (headers) =>
        api.post<FeedbackReceipt>(`/api/v1/public/feedback/invitations/${token(invitationToken)}`, body, { headers }),
      { onSlow },
    ),
  optOut: (optOutToken: string) => api.post<void>(`/api/v1/public/unsubscribe/${token(optOutToken)}`),
};
