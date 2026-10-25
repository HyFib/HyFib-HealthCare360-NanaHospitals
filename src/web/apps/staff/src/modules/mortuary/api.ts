import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

// The mortuary (server `MortuaryEndpoints`): cold-storage slots set by the administrator, the register of bodies
// received and released, and the release slip. A patient who died here comes in only once the death is on record; a
// body leaves only to someone named, on the authority its case needs.

export type BodySource = 'InHospitalDeath' | 'BroughtDead' | 'Police';
export type BodyStatus = 'InStorage' | 'Released';
export type ReleaseTo = 'Relatives' | 'PoliceForPostMortem' | 'BodyDonation' | 'Municipality';

export const SOURCE_LABELS: Record<BodySource, string> = {
  InHospitalDeath: 'Died in the hospital',
  BroughtDead: 'Brought dead',
  Police: 'Brought by the police',
};

export const RELEASE_LABELS: Record<ReleaseTo, string> = {
  Relatives: 'Relatives',
  PoliceForPostMortem: 'Police, for post-mortem',
  BodyDonation: 'Body donation (medical college)',
  Municipality: 'Municipality (unclaimed)',
};

export interface MortuarySlot {
  id: string;
  code: string;
  chamber: string | null;
  isActive: boolean;
  bodyId: string | null;
  bodyNumber: string | null;
  name: string | null;
  receivedAt: string | null;
  unclaimed: boolean;
  version: number;
}

export interface Body {
  id: string;
  number: string;
  patientId: string | null;
  uhid: string | null;
  source: BodySource;
  name: string;
  identified: boolean;
  sex: string | null;
  ageYears: number | null;
  diedAt: string | null;
  receivedAt: string;
  receivedByName: string;
  broughtBy: string | null;
  isMlc: boolean;
  mlcNumber: string | null;
  policeStation: string | null;
  identificationMarks: string | null;
  belongings: string | null;
  slotCode: string | null;
  status: BodyStatus;
  unclaimed: boolean;
  releasedAt: string | null;
  releasedTo: ReleaseTo | null;
  receivedBy: string | null;
  relationship: string | null;
  idProof: string | null;
  policeClearance: string | null;
  belongingsHandedOver: boolean;
  releaseNotes: string | null;
  releasedByName: string | null;
  version: number;
}

export interface SlotInput {
  id: string | null;
  code: string;
  chamber: string | null;
  isActive: boolean;
  version: number | null;
}

export interface ReceiveBodyRequest {
  source: BodySource;
  patientId: string | null;
  name: string | null;
  identified: boolean;
  sex: string | null;
  ageYears: number | null;
  diedAt: string | null;
  broughtBy: string | null;
  isMlc: boolean;
  mlcNumber: string | null;
  policeStation: string | null;
  identificationMarks: string | null;
  belongings: string | null;
  slotId: string;
}

export interface ReleaseBodyRequest {
  to: ReleaseTo;
  receivedBy: string;
  relationship: string | null;
  idProof: string | null;
  policeClearance: string | null;
  belongingsHandedOver: boolean;
  notes: string | null;
  version: number;
}

export const mortuaryKeys = {
  all: ['mortuary'] as const,
  slots: () => ['mortuary', 'slots'] as const,
  register: (status: BodyStatus | null, from: string, to: string) =>
    ['mortuary', 'register', status, from, to] as const,
};

const base = '/api/v1/mortuary';

export const mortuaryQueries = {
  slots: () =>
    queryOptions({
      queryKey: mortuaryKeys.slots(),
      queryFn: ({ signal }) => api.get<MortuarySlot[]>(`${base}/slots`, { signal }),
    }),
  register: (status: BodyStatus | null, from: string, to: string) =>
    queryOptions({
      queryKey: mortuaryKeys.register(status, from, to),
      queryFn: ({ signal }) =>
        api.get<Body[]>(`${base}/bodies`, { query: { status: status ?? undefined, from, to }, signal }),
    }),
};

export const mortuaryApi = {
  saveSlots: (slots: SlotInput[]) => api.put<MortuarySlot[]>(`${base}/slots`, { slots }),
  get: (id: string) => api.get<Body>(`${base}/bodies/${id}`),
  receive: (request: ReceiveBodyRequest) => api.post<Body>(`${base}/bodies`, request),
  release: (id: string, request: ReleaseBodyRequest) => api.post<Body>(`${base}/bodies/${id}/release`, request),
  slip: (id: string) => `${base}/bodies/${id}/slip`,
};

export const MORTUARY_PERMISSIONS = {
  /** Receive and release bodies, and keep the register (mortuary nurses, administration). */
  manage: 'mortuary.manage',
  /** Set up the cold-storage slots (administration). */
  configure: 'mortuary.configure',
} as const;

export const MORTUARY_READERS = [MORTUARY_PERMISSIONS.manage, MORTUARY_PERMISSIONS.configure] as const;

/**
 * What the release still needs, as the server checks it: relatives give their relationship and identity proof and
 * take only an identified body, a medico-legal one only on police clearance; the police give the officer's details;
 * the municipality takes an unclaimed body on police clearance; a medico-legal body cannot be donated.
 */
export function releaseProblems(
  body: Pick<Body, 'isMlc' | 'identified'>,
  input: { to: ReleaseTo | ''; receivedBy: string; relationship: string; idProof: string; policeClearance: string },
) {
  const blank = (s: string) => !s.trim();
  return {
    to: !input.to
      ? 'Choose who takes the body.'
      : input.to === 'BodyDonation' && body.isMlc
        ? 'A medico-legal body cannot be donated.'
        : input.to === 'Relatives' && !body.identified
          ? 'An unidentified body goes to relatives only once the police identify it.'
          : null,
    receivedBy: blank(input.receivedBy) ? 'Name who receives the body.' : null,
    relationship: input.to === 'Relatives' && blank(input.relationship) ? "Record the relative's relationship." : null,
    idProof:
      (input.to === 'Relatives' || input.to === 'PoliceForPostMortem') && blank(input.idProof)
        ? input.to === 'Relatives'
          ? 'Record their identity proof.'
          : "Record the officer's name, number and police station."
        : null,
    policeClearance:
      ((input.to === 'Relatives' && body.isMlc) || input.to === 'Municipality') && blank(input.policeClearance)
        ? 'Record the police clearance (inquest or post-mortem reference).'
        : null,
  };
}

/** Labels typed for new slots, one per line or separated by commas, in capitals and each once. */
export function parseSlotCodes(text: string): string[] {
  return [
    ...new Set(
      text
        .split(/[\n,;]+/)
        .map((c) => c.trim().toUpperCase())
        .filter(Boolean),
    ),
  ];
}
