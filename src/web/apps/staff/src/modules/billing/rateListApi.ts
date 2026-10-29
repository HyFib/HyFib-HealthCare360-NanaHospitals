import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { PackageUse, ServiceCategory } from './labels';

/**
 * The rate list: what a self-paying patient is charged at the signed-in facility today, worked out as a bill works it
 * out, and printed for display in English and the local language (Clinical Establishments Rules 2012, r.9(i)).
 */

/** The document's resource path: `/document` to print, `/pdf` to download. */
export const RATE_LIST_PATH = '/api/v1/billing/rate-list';

export interface RateLine {
  code: string;
  name: string;
  /** The name in the local language, when the hospital has entered one. */
  localName: string | null;
  /** One of the service, after the plan's concession and before GST. */
  rate: number;
  gstRate: number;
  /** What the patient pays, GST included. */
  payable: number;
  /** The billing counter may set a different price. */
  mayVary: boolean;
}

export interface DoctorFeeLine {
  name: string;
  department: string | null;
  fee: RateLine;
  /** By video, when the doctor consults by video. */
  video: RateLine | null;
  /** Days after a paid consultation in which a follow-up is free (none when 0). */
  freeFollowUpDays: number;
}

export interface RoomRateLine {
  day: RateLine;
  intensiveCare: boolean;
  wards: string[];
  nursing: RateLine | null;
  visit: RateLine | null;
  /** In-patient procedures, surgery, nursing and doctors' visits cost this many times their rate in the room. */
  multiplier: number;
}

export interface RateSection {
  category: ServiceCategory;
  lines: RateLine[];
}

export interface PackageLine {
  price: RateLine;
  lengthOfStayDays: number | null;
  usedFor: PackageUse;
}

export interface RateList {
  asOn: string;
  planName: string | null;
  concessionPercent: number;
  registration: RateLine | null;
  doctors: DoctorFeeLine[];
  rooms: RoomRateLine[];
  sections: RateSection[];
  packages: PackageLine[];
}

/** A language of India, by its code (such as ta) and its name in English. */
export interface IndianLanguage {
  code: string;
  name: string;
}

export interface RateListView {
  rates: RateList;
  /** The language the printed list's own words are in beside English ("hi", "ta"): the local language, when HC360 writes it. */
  language: string | null;
  /** The facility's local language, which each service's name prints in under the English one; null when not known. */
  localLanguage: IndianLanguage | null;
  /** The hospital's choice of the facility's language ({@link LANGUAGE_SETTING}), when it names no language. */
  unknownSetting: string | null;
  /** The state of the facility's address, when it names no state and so gives no local language. */
  unknownState: string | null;
}

/** The setting that chooses a facility's local language in place of its state's. */
export const LANGUAGE_SETTING = 'billing.rateList.language';

export interface LanguageNote {
  intent: 'info' | 'warning';
  text: string;
}

/**
 * What the screen says about the languages of the printed list, which the law has displayed in the local language as
 * well as English: which they are, and what to do when the local language is not known or HC360 does not write it.
 */
export function languageNotes(
  view: Pick<RateListView, 'language' | 'localLanguage' | 'unknownSetting' | 'unknownState'>,
): LanguageNote[] {
  const notes: LanguageNote[] = [];
  if (view.unknownSetting !== null) {
    notes.push({
      intent: 'warning',
      text: `The setting ${LANGUAGE_SETTING} ("${view.unknownSetting}") names no language HC360 knows. Enter a language's code or name, such as kn or Kannada, in Administration › Settings.`,
    });
  }

  const local = view.localLanguage;
  if (local === null) {
    notes.push({
      intent: 'warning',
      text:
        view.unknownState !== null
          ? `HC360 does not know the state "${view.unknownState}" of this facility's address, so it does not know the local language, and the printed list is in English only. Correct the state in Setup › Facilities, or choose the language in Administration › Settings (${LANGUAGE_SETTING}).`
          : `This facility's local language is not known: its address names no state, or a state with no one language of its own. The printed list is in English only until you choose the language in Administration › Settings (${LANGUAGE_SETTING}).`,
    });
  } else if (view.language !== null) {
    notes.push({
      intent: 'info',
      text: `The printed list is in English and ${local.name}, this facility's local language, with each service's name in ${local.name} where Setup › Services has one. Display it where patients can read it, and print it again when prices change.`,
    });
  } else {
    notes.push({
      intent: 'warning',
      text: `The printed list's own words are in English: HC360 writes them in Hindi and Tamil, not yet in ${local.name}, this facility's local language. Each service's name in ${local.name} prints under its English name: enter them in Setup › Services.`,
    });
  }

  return notes;
}

export const rateListQueries = {
  current: () =>
    queryOptions({
      queryKey: ['billing', 'rate-list'],
      queryFn: ({ signal }) => api.get<RateListView>(RATE_LIST_PATH, { signal }),
    }),
};
