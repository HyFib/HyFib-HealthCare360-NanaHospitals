import type { BloodGroup } from '@hc360/core';
import { KNOWN_GROUPS, type BloodBagType, type BloodComponentType, type BloodUnitStatus, type KnownBloodGroup, type TtiPanel } from './api';
import { groupLabel } from './labels';

/**
 * Pure transfusion-safety rules for the screens. Each mirrors a server rule (`BloodCompatibility`,
 * `IssueCompatibility`, `CrossMatch`, `ColdChain`, `ComponentShelfLife`, `TtiPanel`) so unsafe choices are
 * explained before the server refuses them. The server enforces every rule again.
 */

export type Abo = 'A' | 'B' | 'AB' | 'O' | 'Bombay';

export function aboOf(group: BloodGroup | null | undefined): Abo | null {
  switch (group) {
    case 'APositive':
    case 'ANegative':
      return 'A';
    case 'BPositive':
    case 'BNegative':
      return 'B';
    case 'ABPositive':
    case 'ABNegative':
      return 'AB';
    case 'OPositive':
    case 'ONegative':
      return 'O';
    case 'BombayPositive':
    case 'BombayNegative':
      return 'Bombay';
    default:
      return null;
  }
}

export function isKnownGroup(group: BloodGroup | null | undefined): group is KnownBloodGroup {
  return aboOf(group) !== null;
}

export function isRhDPositive(group: BloodGroup): boolean {
  return group === 'APositive' || group === 'BPositive' || group === 'ABPositive' || group === 'OPositive' || group === 'BombayPositive';
}

export function containsRedCells(component: BloodComponentType): boolean {
  return component === 'WholeBlood' || component === 'PRBC';
}

/** Rh(D)-negative recipients must not receive Rh(D)-positive red cells. */
function rhCompatible(donor: BloodGroup, recipient: BloodGroup): boolean {
  return !isRhDPositive(donor) || isRhDPositive(recipient);
}

/** Donor antigens must not meet recipient antibodies; Bombay recipients take only Bombay red cells. */
export function redCellsCompatible(donor: BloodGroup, recipient: BloodGroup): boolean {
  const d = aboOf(donor);
  const r = aboOf(recipient);
  if (!d || !r) {
    return false;
  }
  const abo =
    r === 'Bombay'
      ? d === 'Bombay'
      : r === 'AB'
        ? true
        : r === 'A'
          ? d === 'A' || d === 'O' || d === 'Bombay'
          : r === 'B'
            ? d === 'B' || d === 'O' || d === 'Bombay'
            : d === 'O' || d === 'Bombay';
  return abo && rhCompatible(donor, recipient);
}

/** Whole blood carries plasma as well, so it must be ABO-identical (and Rh compatible). */
export function wholeBloodCompatible(donor: BloodGroup, recipient: BloodGroup): boolean {
  const d = aboOf(donor);
  const r = aboOf(recipient);
  return d !== null && r !== null && d === r && rhCompatible(donor, recipient);
}

/** Plasma, platelets, cryoprecipitate: donor antibodies must not meet recipient red cells. */
export function plasmaCompatible(donor: BloodGroup, recipient: BloodGroup): boolean {
  const d = aboOf(donor);
  const r = aboOf(recipient);
  if (!d || !r) {
    return false;
  }
  switch (d) {
    case 'AB':
      return true;
    case 'A':
      return r === 'A' || r === 'O' || r === 'Bombay';
    case 'B':
      return r === 'B' || r === 'O' || r === 'Bombay';
    case 'O':
      return r === 'O' || r === 'Bombay';
    default:
      return r === 'Bombay';
  }
}

export function isCompatible(component: BloodComponentType, donor: BloodGroup, recipient: BloodGroup): boolean {
  switch (component) {
    case 'WholeBlood':
      return wholeBloodCompatible(donor, recipient);
    case 'PRBC':
      return redCellsCompatible(donor, recipient);
    default:
      return plasmaCompatible(donor, recipient);
  }
}

/** Unit groups that may be given to this recipient for this component (empty when the patient is not grouped). */
export function compatibleDonorGroups(component: BloodComponentType, recipient: BloodGroup): KnownBloodGroup[] {
  return KNOWN_GROUPS.filter((donor) => isCompatible(component, donor, recipient));
}

/** Plain-language compatibility for the request screen, e.g. "A+ patient, packed red cells: A+, A−, O+, O−, …". */
export function compatibilityText(component: BloodComponentType, recipient: BloodGroup): string {
  if (!isKnownGroup(recipient)) {
    return 'Group the patient’s sample first. The group recorded at registration is never used for transfusion.';
  }
  const groups = compatibleDonorGroups(component, recipient).map((g) => groupLabel(g));
  const rule =
    component === 'WholeBlood'
      ? 'Whole blood must be ABO-identical.'
      : component === 'PRBC'
        ? 'Red cells: donor cells must not meet the patient’s antibodies; Rh(D)-negative patients get Rh(D)-negative cells.'
        : 'Plasma, platelets and cryoprecipitate: donor plasma must not meet the patient’s red cells.';
  return `${rule} Compatible unit groups for a ${groupLabel(recipient)} patient: ${groups.join(', ')}.`;
}

// ---------------------------------------------------------------- cross-match validity

export const CROSSMATCH_VALIDITY_HOURS = 72;

export interface CrossmatchValidity {
  valid: boolean;
  expiresAt: string;
  hoursLeft: number;
}

/** A compatible cross-match is valid for 72 hours (new antibodies may appear after recent transfusion). */
export function crossmatchValidity(performedAt: string, now: number, compatible = true): CrossmatchValidity {
  const expires = Date.parse(performedAt) + CROSSMATCH_VALIDITY_HOURS * 3_600_000;
  const hoursLeft = Math.max(0, Math.floor((expires - now) / 3_600_000));
  return { valid: compatible && now - Date.parse(performedAt) <= CROSSMATCH_VALIDITY_HOURS * 3_600_000, expiresAt: new Date(expires).toISOString(), hoursLeft };
}

// ---------------------------------------------------------------- issue gate

export interface IssueCheckInput {
  component: BloodComponentType;
  unitGroup: BloodGroup;
  patientGroup: BloodGroup;
  hasValidCompatibleCrossmatch: boolean;
  emergencyReleaseAuthorised: boolean;
  unitStatus: BloodUnitStatus;
  unitExpired: boolean;
  ttiCleared: boolean;
  reservedForOtherRequest: boolean;
}

export interface IssueCheck {
  allowed: boolean;
  /** The unit would go out uncross-matched under a documented emergency release. */
  emergency: boolean;
  reasons: string[];
}

/**
 * The bedside-safety gate at the counter, as the server checks it on every issue (`IssueCompatibility.Check` and
 * `BloodComponent.Issue`): red cells need a compatible cross-match within 72 h, or an authorised emergency release
 * of O Rh(D)-negative PRBC only; plasma products need a plasma-compatible group, or AB plasma for an ungrouped
 * patient under emergency release. Expired, unscreened or someone else's reserved units never go out.
 */
export function issueCheck(input: IssueCheckInput): IssueCheck {
  const reasons: string[] = [];
  let emergency = false;

  if (input.unitExpired || input.unitStatus === 'Expired') {
    reasons.push('This unit has expired and must not be transfused.');
  }
  if (!input.ttiCleared || input.unitStatus === 'Quarantine') {
    reasons.push('This unit has not cleared TTI screening (still in quarantine).');
  }
  if (input.reservedForOtherRequest) {
    reasons.push('This unit is reserved for another patient’s request.');
  }
  if (input.unitStatus === 'Issued' || input.unitStatus === 'Discarded') {
    reasons.push(`This unit is ${input.unitStatus.toLowerCase()}.`);
  }

  if (containsRedCells(input.component)) {
    if (input.hasValidCompatibleCrossmatch) {
      if (!isCompatible(input.component, input.unitGroup, input.patientGroup)) {
        reasons.push('The unit is ABO/Rh incompatible with the patient’s group.');
      }
    } else if (input.emergencyReleaseAuthorised && input.component === 'PRBC' && input.unitGroup === 'ONegative') {
      emergency = true;
    } else {
      reasons.push(
        input.emergencyReleaseAuthorised
          ? 'Uncross-matched emergency release is limited to group O Rh(D)-negative packed red cells.'
          : 'Red cells can only be issued against a compatible cross-match done in the last 72 hours.',
      );
    }
  } else if (isKnownGroup(input.patientGroup)) {
    if (!plasmaCompatible(input.unitGroup, input.patientGroup)) {
      reasons.push(`${groupLabel(input.unitGroup)} plasma is incompatible with a ${groupLabel(input.patientGroup)} patient.`);
    }
  } else if (input.emergencyReleaseAuthorised && aboOf(input.unitGroup) === 'AB') {
    emergency = true;
  } else {
    reasons.push('Group the patient’s sample before issuing plasma, platelets or cryoprecipitate.');
  }

  return { allowed: reasons.length === 0, emergency: reasons.length === 0 && emergency, reasons };
}

// ---------------------------------------------------------------- cold chain on return

export const RETURN_WINDOW_MINUTES = 30;

export interface ReturnOutcome {
  restock: boolean;
  reason: string;
}

/** Mirrors `ColdChain.CanRestock` (plus expiry): what will happen to a unit sent back from the ward. */
export function returnOutcome(component: BloodComponentType, issuedAt: string, returnedAt: number, temperatureMaintained: boolean, expiresAt?: string): ReturnOutcome {
  if (component === 'FFP' || component === 'Cryoprecipitate') {
    return { restock: false, reason: 'Thawed plasma products cannot be refrozen; the unit will be discarded.' };
  }
  if (!temperatureMaintained) {
    return { restock: false, reason: 'Storage temperature was not maintained; the unit will be discarded.' };
  }
  const minutes = (returnedAt - Date.parse(issuedAt)) / 60_000;
  if (minutes < 0 || minutes > RETURN_WINDOW_MINUTES) {
    return { restock: false, reason: `Returned more than ${RETURN_WINDOW_MINUTES} minutes after issue; the unit will be discarded.` };
  }
  if (expiresAt && returnedAt >= Date.parse(expiresAt)) {
    return { restock: false, reason: 'The unit expired while it was out; it will be discarded.' };
  }
  return { restock: true, reason: 'Returned within 30 minutes with the cold chain kept: the unit goes back to stock and its charge is withdrawn.' };
}

// ---------------------------------------------------------------- shelf life and separation

export const SHELF_LIFE_DAYS: Record<BloodComponentType, number> = { WholeBlood: 35, PRBC: 35, FFP: 365, PlateletConcentrate: 5, Cryoprecipitate: 365 };

export const SEPARATION_WINDOW_HOURS = 8;

export function expiryFor(component: BloodComponentType, collectedAt: string): string {
  return new Date(Date.parse(collectedAt) + SHELF_LIFE_DAYS[component] * 86_400_000).toISOString();
}

export function needsPromptSeparation(component: BloodComponentType): boolean {
  return component === 'FFP' || component === 'PlateletConcentrate' || component === 'Cryoprecipitate';
}

export function maxComponents(bag: BloodBagType): number {
  return bag === 'Single' ? 1 : bag === 'Double' ? 2 : bag === 'Triple' ? 3 : 4;
}

/** Typical volume when the bag is not weighed (`ComponentShelfLife.DefaultVolumeMl`). */
export function defaultVolumeMl(component: BloodComponentType, donationVolumeMl: number): number {
  switch (component) {
    case 'WholeBlood':
      return donationVolumeMl;
    case 'PRBC':
      return donationVolumeMl >= 450 ? 280 : 220;
    case 'FFP':
      return donationVolumeMl >= 450 ? 220 : 180;
    case 'PlateletConcentrate':
      return 60;
    default:
      return 20;
  }
}

/** Minutes left to separate plasma or platelets (negative once the 8-hour window has passed). */
export function separationMinutesLeft(collectedAt: string, now: number): number {
  return Math.floor((Date.parse(collectedAt) + SEPARATION_WINDOW_HOURS * 3_600_000 - now) / 60_000);
}

/** Same checks, in the same order, as `ComponentShelfLife.ValidateSeparation`. */
export function separationCheck(bag: BloodBagType, components: readonly BloodComponentType[], collectedAt: string, now: number): string | null {
  if (components.length === 0 || new Set(components).size !== components.length) {
    return 'Choose each component once.';
  }
  if (components.includes('WholeBlood')) {
    return components.length === 1 ? null : 'Whole blood cannot be stored while also separating components from the same bag.';
  }
  if (bag === 'Single') {
    return 'A single bag has no satellite bags; keep it as whole blood.';
  }
  if (!components.includes('PRBC')) {
    return 'Separating a donation always yields packed red cells (PRBC).';
  }
  if (components.length > maxComponents(bag)) {
    return `A ${bag.toLowerCase()} bag gives at most ${maxComponents(bag)} components.`;
  }
  if (components.some(needsPromptSeparation) && separationMinutesLeft(collectedAt, now) < 0) {
    return 'Plasma, platelets and cryoprecipitate must be separated within 8 hours of collection.';
  }
  return null;
}

// ---------------------------------------------------------------- screening and expiry

export interface TtiState {
  complete: boolean;
  /** Every marker non-reactive: the only state in which a unit may leave quarantine. */
  cleared: boolean;
  reactive: (keyof TtiPanel)[];
  pending: (keyof TtiPanel)[];
}

const MARKERS: (keyof TtiPanel)[] = ['hiv', 'hbsAg', 'hcv', 'syphilis', 'malaria'];

export function ttiState(tti: TtiPanel): TtiState {
  return {
    complete: MARKERS.every((m) => tti[m] !== 'Pending'),
    cleared: MARKERS.every((m) => tti[m] === 'NonReactive'),
    reactive: MARKERS.filter((m) => tti[m] === 'Reactive'),
    pending: MARKERS.filter((m) => tti[m] === 'Pending'),
  };
}

export type ExpiryUrgency = 'expired' | 'today' | 'soon' | 'ok';

/** Expired, within 24 hours, within 3 days (the server's "expiring soon"), or fine. */
export function expiryUrgency(expiresAt: string, now: number): ExpiryUrgency {
  const hours = (Date.parse(expiresAt) - now) / 3_600_000;
  return hours <= 0 ? 'expired' : hours <= 24 ? 'today' : hours <= 72 ? 'soon' : 'ok';
}

/** Minimum available units per group before a component shows as low (hospital policy default). */
export const LOW_STOCK_THRESHOLD: Record<BloodComponentType, number> = { WholeBlood: 1, PRBC: 4, FFP: 4, PlateletConcentrate: 2, Cryoprecipitate: 2 };

export type StockLevel = 'out' | 'low' | 'ok';

export function stockLevel(component: BloodComponentType, available: number): StockLevel {
  return available <= 0 ? 'out' : available < LOW_STOCK_THRESHOLD[component] ? 'low' : 'ok';
}

/** Units that may still be discarded: in stock (quarantine, available, reserved) or already expired. */
export function canDiscardUnit(status: BloodUnitStatus): boolean {
  return status === 'Quarantine' || status === 'Available' || status === 'Reserved' || status === 'Expired';
}
