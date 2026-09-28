/** Insurance enums as the server sends them, with plain words for the insurance desk. Pure module. */

export const PAYER_CATEGORIES = ['Insurer', 'Tpa', 'Corporate', 'GovernmentScheme'] as const;
export type PayerCategory = (typeof PAYER_CATEGORIES)[number];
export const PAYER_CATEGORY_LABEL: Record<PayerCategory, string> = {
  Insurer: 'Insurance company',
  Tpa: 'TPA',
  Corporate: 'Corporate',
  GovernmentScheme: 'Government scheme',
};

export const PREAUTH_STATUSES = ['Draft', 'Submitted', 'Query', 'Approved', 'PartiallyApproved', 'Rejected'] as const;
export type PreAuthStatus = (typeof PREAUTH_STATUSES)[number];
export const PREAUTH_STATUS_LABEL: Record<PreAuthStatus, string> = {
  Draft: 'Draft',
  Submitted: 'Waiting for insurer',
  Query: 'Insurer asked a question',
  Approved: 'Approved',
  PartiallyApproved: 'Partly approved',
  Rejected: 'Rejected',
};

export const ENHANCEMENT_STATUSES = ['Pending', 'Approved', 'Rejected'] as const;
export type EnhancementStatus = (typeof ENHANCEMENT_STATUSES)[number];
export const ENHANCEMENT_STATUS_LABEL: Record<EnhancementStatus, string> = { Pending: 'Waiting for insurer', Approved: 'Approved', Rejected: 'Rejected' };

export const CLAIM_STATUSES = ['Submitted', 'Query', 'Approved', 'Settled', 'PartiallySettled', 'Rejected'] as const;
export type ClaimStatus = (typeof CLAIM_STATUSES)[number];
export const CLAIM_STATUS_LABEL: Record<ClaimStatus, string> = {
  Submitted: 'Submitted',
  Query: 'Insurer asked a question',
  Approved: 'Approved, awaiting payment',
  Settled: 'Settled',
  PartiallySettled: 'Partly settled',
  Rejected: 'Rejected',
};

/** Claims still owed money or an answer. */
export const OPEN_CLAIM_STATUSES = ['Submitted', 'Query', 'Approved', 'PartiallySettled'] as const satisfies readonly ClaimStatus[];

export function preAuthTone(status: PreAuthStatus): 'success' | 'warning' | 'danger' | 'informative' | 'brand' {
  switch (status) {
    case 'Approved':
      return 'success';
    case 'PartiallyApproved':
    case 'Query':
      return 'warning';
    case 'Rejected':
      return 'danger';
    case 'Submitted':
      return 'brand';
    default:
      return 'informative';
  }
}

export function claimTone(status: ClaimStatus): 'success' | 'warning' | 'danger' | 'informative' | 'brand' {
  switch (status) {
    case 'Settled':
      return 'success';
    case 'Query':
    case 'PartiallySettled':
      return 'warning';
    case 'Rejected':
      return 'danger';
    case 'Approved':
      return 'brand';
    default:
      return 'informative';
  }
}
