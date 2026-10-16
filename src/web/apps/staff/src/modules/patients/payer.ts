import type { PayerType } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { usePermission } from '@/lib/permissions';
import { patientQueries } from './api';

/**
 * Who pays for the patient's care, for the patient banner (UX-04). The server chooses the payer
 * (`GET /api/v1/patients/{id}/payer`): the payer of the patient's open visit, or of the last one ("Last payer") when
 * none is open, with the payer's name for staff who may look payers up. Screens that know the payer better (an
 * admission) pass their own.
 */

const PAYER_TEXT: Record<PayerType, string> = {
  Self: 'Self-pay',
  Insurance: 'Insurance',
  Corporate: 'Corporate',
  GovernmentScheme: 'Government scheme',
  Staff: 'Staff',
  Charity: 'Charity',
};

/** Who may read a patient's visits, and so their payer. */
const VISIT_READERS = 'visits.read';

/** "Payer: Insurance · Star Health", or "Last payer: Self-pay" when it comes from a finished visit. */
export function payerText(payerType: PayerType, payerName: string | null | undefined, isOpen = true): string {
  const type = PAYER_TEXT[payerType] ?? payerType;
  return `${isOpen ? 'Payer' : 'Last payer'}: ${payerName ? `${type} · ${payerName}` : type}`;
}

/**
 * The banner's payer line for a patient, or null while it loads, when the patient has had no visit, and for staff who
 * may not read visits. Pass `enabled: false` when the screen supplies the payer itself.
 */
export function usePatientPayer(patientId: string | null | undefined, enabled = true): string | null {
  const canReadVisits = usePermission(VISIT_READERS);
  const payer = useQuery({
    ...patientQueries.payer(patientId ?? ''),
    enabled: enabled && canReadVisits && Boolean(patientId),
    // Never the previous patient's payer while this one's loads: the banner would name the wrong payer.
    placeholderData: undefined,
    retry: false,
  });
  const current = payer.data?.current;
  if (!enabled || !canReadVisits || !patientId || !current) {
    return null;
  }
  return payerText(current.payerType, current.payerName, current.isCurrentVisit);
}
