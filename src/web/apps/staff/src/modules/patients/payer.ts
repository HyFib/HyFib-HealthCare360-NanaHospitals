import type { PayerType } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { usePermission } from '@/lib/permissions';
import { insuranceQueries } from '@/modules/insurance/api';
import { patientQueries, type VisitListItem } from './api';

/**
 * Who pays for the patient's care, for the patient banner (UX-04). The payer is recorded on each visit and admission,
 * not on the patient, so the banner shows the payer of the patient's open visit, or of the last one ("Last payer")
 * when none is open. Screens that know the payer better (an admission) pass their own.
 */

const PAYER_TEXT: Record<PayerType, string> = {
  Self: 'Self-pay',
  Insurance: 'Insurance',
  Corporate: 'Corporate',
  GovernmentScheme: 'Government scheme',
  Staff: 'Staff',
  Charity: 'Charity',
};

/** Who may read a patient's visits, and who may look up a payer's name (as the server allows). */
const VISIT_READERS = 'visits.read';
const PAYER_READERS = [
  'insurance.payers.manage',
  'insurance.policies.manage',
  'insurance.preauth.manage',
  'insurance.claims.manage',
  'billing.op.create',
  'billing.ip.manage',
  'billing.charges.read',
] as const;

/** The visit whose payer the banner shows: the open one, else the latest that was not cancelled. Newest first. */
export function payerVisit(visits: readonly VisitListItem[]): { visit: VisitListItem; isOpen: boolean } | null {
  const open = visits.find((v) => v.status === 'Open' || v.status === 'InProgress');
  if (open) {
    return { visit: open, isOpen: true };
  }
  const last = visits.find((v) => v.status !== 'Cancelled');
  return last ? { visit: last, isOpen: false } : null;
}

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
  const canReadPayers = usePermission(PAYER_READERS);
  const visits = useQuery({
    ...patientQueries.visits(patientId ?? ''),
    enabled: enabled && canReadVisits && Boolean(patientId),
    // Never the previous patient's visits while this one's load: the banner would name the wrong payer.
    placeholderData: undefined,
    retry: false,
  });
  const current = payerVisit((visits.data?.items ?? []).filter((v) => v.patientId === patientId));
  // The visit list carries the payer's type only; the name comes from the visit's payer id and the payer master.
  const thirdParty = current !== null && current.visit.payerType !== 'Self' && canReadPayers;
  const visit = useQuery({
    ...patientQueries.visit(current?.visit.id ?? ''),
    enabled: enabled && thirdParty,
    retry: false,
  });
  const payerId = thirdParty ? (visit.data?.payerId ?? null) : null;
  const payer = useQuery({
    ...insuranceQueries.payer(payerId ?? ''),
    enabled: enabled && Boolean(payerId),
    retry: false,
  });
  if (!enabled || !current) {
    return null;
  }
  return payerText(current.visit.payerType, payerId ? payer.data?.name : null, current.isOpen);
}
