/**
 * Health-checkup day rules (pure, unit-tested): what can be done with a booking, itinerary progress, why a checkup
 * cannot be completed yet, and plain-language wording for the server's refusals (including Billing's package charge).
 */

export type BookingStatus = 'Booked' | 'CheckedIn' | 'InProgress' | 'Completed' | 'Cancelled' | 'NoShow';
export type ItineraryStatus = 'Pending' | 'Done' | 'Skipped';

export const BOOKING_STATUSES: readonly BookingStatus[] = [
  'Booked',
  'CheckedIn',
  'InProgress',
  'Completed',
  'Cancelled',
  'NoShow',
];

export const bookingStatusLabel: Record<BookingStatus, string> = {
  Booked: 'Booked',
  CheckedIn: 'Checked in',
  InProgress: 'In progress',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
  NoShow: 'Did not come',
};

export interface StopLike {
  id: string;
  sequence: number;
  status: ItineraryStatus;
}

export interface BookingLike {
  status: BookingStatus;
  summary: string | null;
  itinerary: readonly StopLike[];
}

export interface Progress {
  done: number;
  skipped: number;
  pending: number;
  total: number;
  /** Share of stops closed (done or skipped), 0–1. */
  ratio: number;
}

export function progress(itinerary: readonly StopLike[]): Progress {
  const done = itinerary.filter((s) => s.status === 'Done').length;
  const skipped = itinerary.filter((s) => s.status === 'Skipped').length;
  const total = itinerary.length;
  return { done, skipped, pending: total - done - skipped, total, ratio: total === 0 ? 0 : (done + skipped) / total };
}

/** The next stop to go to (lowest sequence still pending). */
export function nextStop<T extends StopLike>(itinerary: readonly T[]): T | undefined {
  return itinerary.filter((s) => s.status === 'Pending').toSorted((a, b) => a.sequence - b.sequence)[0];
}

export const isOpen = (status: BookingStatus) =>
  status === 'Booked' || status === 'CheckedIn' || status === 'InProgress';
export const canCheckIn = (status: BookingStatus) => status === 'Booked';
export const canMarkNoShow = (status: BookingStatus) => status === 'Booked';
export const canCancel = isOpen;
export const isStarted = (status: BookingStatus) => status === 'CheckedIn' || status === 'InProgress';

/** Why the checkup cannot be completed yet (empty = ready): checked in, every stop closed, doctor's summary written. */
export function completionBlockers(booking: BookingLike): string[] {
  const blockers: string[] = [];
  if (!isStarted(booking.status)) {
    blockers.push(booking.status === 'Booked' ? 'Check the patient in first.' : 'This checkup is closed.');
    return blockers;
  }
  const pending = progress(booking.itinerary).pending;
  if (pending > 0) {
    blockers.push(
      `${pending} ${pending === 1 ? 'stop is' : 'stops are'} still pending. Complete or skip ${pending === 1 ? 'it' : 'them'}.`,
    );
  }
  if (!booking.summary?.trim()) {
    blockers.push("The reviewing doctor's summary is missing.");
  }
  return blockers;
}

/** The parts of an API error the wording needs (ApiError satisfies it). */
export interface ProblemLike {
  status?: number;
  code?: string;
  userMessage?: string;
  fieldErrors?: Record<string, string[]>;
}

export interface Wording {
  title: string;
  message: string;
}

/**
 * Plain-language explanation of a refused check-in, cancellation or completion. Check-in now charges the package
 * through Billing, so Billing's refusals are explained with what to do next.
 */
export function checkupProblem(
  problem: ProblemLike,
  action: 'check-in' | 'cancel' | 'no-show' | 'complete' | 'book',
): Wording {
  const server = problem.userMessage?.trim() ?? '';
  const visitError = problem.fieldErrors?.visitId?.[0];
  if (visitError) {
    return {
      title: 'That visit belongs to another patient',
      message: 'Choose one of this patient’s visits, or check in without linking a visit.',
    };
  }
  switch (problem.code) {
    case 'package.not_billable':
      return {
        title: 'This package cannot be billed yet',
        message:
          server ||
          'An administrator can open the package under Health check-ups › Packages and save it once to register its billing item; then check in again.',
      };
    case 'booking.package_billed':
      return {
        title: 'The package fee is already on a finalised bill',
        message:
          server ||
          `Ask billing to cancel that bill or issue a credit note first, then ${action === 'no-show' ? 'mark the no-show' : 'cancel the checkup'} again.`,
      };
    case 'billing.service_inactive':
      return {
        title: 'The package is no longer offered for billing',
        message: `${server} Ask the administrator to reactivate the package’s service before checking in.`.trim(),
      };
    case 'billing.unavailable':
      return {
        title: 'Billing is not available',
        message:
          'Check-in charges the package fee through billing, which is not set up. Contact the system administrator.',
      };
    case 'patient.merged':
      return {
        title: 'This UHID was merged into another record',
        message: 'Book and check in the checkup on the surviving patient record.',
      };
    case 'booking.not_booked':
      return {
        title: action === 'no-show' ? 'The patient has already arrived' : 'Already checked in',
        message: server || 'Refresh the page to see the current status.',
      };
    case 'booking.closed':
      return {
        title: 'This checkup is already closed',
        message: server || 'Refresh the page to see the current status.',
      };
    case 'booking.pending_stops':
      return { title: 'Stops are still pending', message: server };
    case 'booking.summary_missing':
      return {
        title: "The doctor's summary is missing",
        message: server || 'The reviewing doctor writes the summary before the checkup is completed.',
      };
    case 'package.inactive':
      return { title: 'This package is no longer offered', message: server || 'Choose another package.' };
    case 'facility.required':
      return {
        title: 'Select a facility first',
        message: 'Choose your hospital branch from your name at the top right, then try again.',
      };
    default: {
      const titles: Record<typeof action, string> = {
        'check-in': 'Could not check the patient in',
        cancel: 'Could not cancel the checkup',
        'no-show': 'Could not mark the no-show',
        complete: 'Could not complete the checkup',
        book: 'Could not book the checkup',
      };
      return { title: titles[action], message: server || 'Please try again.' };
    }
  }
}

/** Counts per status for the day view header. */
export function countByStatus(bookings: readonly { status: BookingStatus }[]): Record<BookingStatus, number> {
  const counts = Object.fromEntries(BOOKING_STATUSES.map((s) => [s, 0])) as Record<BookingStatus, number>;
  for (const booking of bookings) {
    counts[booking.status] += 1;
  }
  return counts;
}
