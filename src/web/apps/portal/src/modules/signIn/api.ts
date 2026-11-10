import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { withProof } from '@/lib/proofOfWork';

/** What a link to the portal shows before a code is asked for: the hospital and the number, masked. */
export interface PublicPortalLink {
  hospital: string;
  maskedPhone: string;
  expiresAt: string;
}

/** A sign-in code sent through the link, as the sign-in page's request answers. */
export interface PortalLinkChallenge {
  challengeId: string;
  expiresAt: string;
  maskedPhone: string;
}

const token = (value: string) => encodeURIComponent(value);

/**
 * A link to the portal that staff sent to the patient's registered number (no sign-in; the token only names the link).
 * Opening it changes nothing; asking for a code through it sends one to that number, with a proof of work as the
 * sign-in page's request has (SRS SEC-06). The page does not know the number, so the proof names none.
 */
export const publicPortalLinkQueries = {
  link: (linkToken: string) =>
    queryOptions({
      queryKey: ['public', 'portal-link', linkToken],
      queryFn: ({ signal }) => api.get<PublicPortalLink>(`/api/v1/public/portal-links/${token(linkToken)}`, { signal }),
      staleTime: Infinity,
      retry: false,
    }),
};

export const publicPortalLinkApi = {
  requestCode: (linkToken: string, onSlow?: (slow: boolean) => void) =>
    withProof(
      'otp',
      (headers) =>
        api.post<PortalLinkChallenge>(`/api/v1/public/portal-links/${token(linkToken)}/otp`, undefined, { headers }),
      { onSlow },
    ),
};
