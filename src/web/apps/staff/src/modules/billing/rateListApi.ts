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

export interface RateListView {
  rates: RateList;
  /** The local language the printed list is written in beside English ("hi", "ta"), or null when HC360 does not write it. */
  language: string | null;
}

export const rateListQueries = {
  current: () =>
    queryOptions({
      queryKey: ['billing', 'rate-list'],
      queryFn: ({ signal }) => api.get<RateListView>(RATE_LIST_PATH, { signal }),
    }),
};
