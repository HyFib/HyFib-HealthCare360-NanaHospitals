import { api } from '@/lib/api';

/** What a witness confirms: the server's witness purposes (`WitnessPurpose`). */
export type WitnessPurposeKey =
  | 'mar.dose'
  | 'blood.bedside_check'
  // A patient leaving the ward against medical advice (LAMA, DAMA): a nurse or doctor of the ward team witnesses.
  | 'discharge.against_advice'
  // The doctor who explained the risks of leaving against advice, confirming at the nurse's screen.
  | 'against_advice.doctor'
  // A patient leaving the emergency department against medical advice: a nurse or doctor of the department witnesses.
  | 'er.lama'
  // A swab, needle or instrument count in the theatre: a second member of the OT team witnesses.
  | 'ot.count'
  // The anaesthetist's handover of a patient in the recovery room, confirmed at the recovery nurse's screen.
  | 'ot.recovery_handover'
  // The second registered medical practitioner's opinion on a termination from 20 weeks, confirmed at the first's screen.
  | 'mtp.second_opinion'
  // A body released from the mortuary on the police's clearance: a second member of the mortuary staff witnesses.
  | 'mortuary.release'
  // A patient's consent to share their whole record, given with no signed form: another member of staff witnesses.
  | 'consent.record_sharing';

export interface ConfirmWitnessRequest {
  userName: string;
  password: string;
  /** The witness's authenticator code, when their account has one (the server answers `witness.code_required`). */
  code: string | null;
  purpose: WitnessPurposeKey;
  patientId: string | null;
}

/** A witness's confirmation: good for one record of this user's, of this purpose and patient, for a few minutes. */
export interface WitnessConfirmed {
  confirmationId: string;
  userId: string;
  name: string;
  expiresAt: string;
}

export const witnessApi = {
  /**
   * A second member of staff confirms at this screen with their own user name and password (server
   * `AuthService.ConfirmWitnessAsync`). On the auth path, so the request carries no idempotency key and the password is
   * never hashed into the server's idempotency store; nothing is signed in.
   */
  confirm: (body: ConfirmWitnessRequest) => api.post<WitnessConfirmed>('/api/v1/auth/witness', body),
};
