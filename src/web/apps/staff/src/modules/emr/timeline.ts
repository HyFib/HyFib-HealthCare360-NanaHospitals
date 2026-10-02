/**
 * EHR timeline and chart-navigation helpers (pure, unit-tested): entry types and labels, URL (de)serialisation of
 * the type filter, grouping by IST calendar day and where a click on an entry should take the doctor.
 */

export type TimelineEntryType =
  | 'Encounter'
  | 'Vitals'
  | 'Diagnosis'
  | 'Prescription'
  | 'Order'
  | 'Note'
  | 'Immunization'
  | 'Document'
  | 'DischargeSummary'
  | 'Result';

export const TIMELINE_TYPES: readonly TimelineEntryType[] = [
  'Encounter',
  'Vitals',
  'Diagnosis',
  'Prescription',
  'Order',
  'Result',
  'Note',
  'Immunization',
  'Document',
  'DischargeSummary',
];

export const timelineTypeLabel: Record<TimelineEntryType, string> = {
  Encounter: 'Encounters',
  Vitals: 'Vitals',
  Diagnosis: 'Diagnoses',
  Prescription: 'Prescriptions',
  Order: 'Orders',
  Result: 'Results',
  Note: 'Notes',
  Immunization: 'Immunisations',
  Document: 'Documents',
  DischargeSummary: 'Discharge summaries',
};

export interface TimelineEntry {
  type: TimelineEntryType;
  id: string;
  occurredAt: string;
  title: string;
  summary: string | null;
  status: string | null;
  encounterId: string | null;
  isCritical: boolean;
}

/** Tabs of the patient chart (kept in the URL as `?tab=`). */
export const CHART_TABS = [
  'timeline',
  'encounters',
  'vitals',
  'allergies',
  'alerts',
  'diagnoses',
  'notes',
  'immunisations',
  'orders',
  'prescriptions',
  'documents',
  'consents',
  'admissions',
] as const;

export type ChartTab = (typeof CHART_TABS)[number];

export const chartTabLabel: Record<ChartTab, string> = {
  timeline: 'Timeline',
  encounters: 'Encounters',
  vitals: 'Vitals',
  allergies: 'Allergies',
  alerts: 'Alerts',
  diagnoses: 'Diagnoses',
  notes: 'Notes',
  immunisations: 'Immunisations',
  orders: 'Orders',
  prescriptions: 'Prescriptions',
  documents: 'Documents',
  consents: 'Consents',
  admissions: 'Admissions',
};

export function parseChartTab(value: unknown): ChartTab | undefined {
  return typeof value === 'string' && (CHART_TABS as readonly string[]).includes(value)
    ? (value as ChartTab)
    : undefined;
}

/** "Encounter,Vitals" → ['Encounter', 'Vitals']; unknown names are dropped; nothing valid → undefined (all types). */
export function parseTypes(value: unknown): TimelineEntryType[] | undefined {
  if (typeof value !== 'string' || !value.trim()) {
    return undefined;
  }
  const types = value
    .split(',')
    .map((part) => part.trim())
    .filter((part): part is TimelineEntryType => (TIMELINE_TYPES as readonly string[]).includes(part));
  const unique = [...new Set(types)];
  return unique.length > 0 ? unique : undefined;
}

/** Stable order for the URL and the server's `types` parameter; all or none selected → undefined. */
export function serializeTypes(types: readonly TimelineEntryType[] | undefined): string | undefined {
  if (!types || types.length === 0 || types.length >= TIMELINE_TYPES.length) {
    return undefined;
  }
  return TIMELINE_TYPES.filter((type) => types.includes(type)).join(',');
}

export function toggleType(
  types: readonly TimelineEntryType[] | undefined,
  type: TimelineEntryType,
): TimelineEntryType[] | undefined {
  const current = types ?? [];
  const next = current.includes(type) ? current.filter((t) => t !== type) : [...current, type];
  return next.length === 0 ? undefined : next;
}

const IST_DAY = new Intl.DateTimeFormat('en-CA', {
  timeZone: 'Asia/Kolkata',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

/** IST calendar day ("yyyy-MM-dd") of an instant. */
export function istDay(iso: string): string {
  return IST_DAY.format(new Date(iso));
}

export interface TimelineDay {
  day: string;
  entries: TimelineEntry[];
}

/** Groups entries (already newest first) into days, keeping their order. */
export function groupByDay(entries: readonly TimelineEntry[], dayOf: (iso: string) => string = istDay): TimelineDay[] {
  const days: TimelineDay[] = [];
  for (const entry of entries) {
    const day = dayOf(entry.occurredAt);
    const last = days.at(-1);
    if (last && last.day === day) {
      last.entries.push(entry);
    } else {
      days.push({ day, entries: [entry] });
    }
  }
  return days;
}

const ENCOUNTER_WORDS: Record<string, string> = {
  OutPatient: 'OP',
  InPatient: 'In-patient',
  Emergency: 'Emergency',
  Telehealth: 'Teleconsultation',
  HealthCheckup: 'Health check-up',
};

/** Server titles use enum names ("OutPatient encounter"); show them in plain words ("OP consultation"). */
export function prettyTitle(entry: Pick<TimelineEntry, 'type' | 'title'>): string {
  if (entry.type === 'Encounter') {
    const match = /^(\w+) encounter$/.exec(entry.title);
    if (match) {
      const word = ENCOUNTER_WORDS[match[1] ?? ''] ?? match[1];
      return `${word} consultation`;
    }
  }
  return entry.title;
}

export type TimelineTarget = { kind: 'encounter'; encounterId: string } | { kind: 'tab'; tab: ChartTab };

/** Where a click on a timeline entry leads: the encounter it belongs to, or the matching chart tab. */
export function timelineTarget(entry: Pick<TimelineEntry, 'type' | 'id' | 'encounterId'>): TimelineTarget {
  if (entry.type === 'Encounter') {
    return { kind: 'encounter', encounterId: entry.id };
  }
  if (entry.encounterId && ['Vitals', 'Diagnosis', 'Prescription', 'Order', 'Note'].includes(entry.type)) {
    return { kind: 'encounter', encounterId: entry.encounterId };
  }
  const tab: Record<TimelineEntryType, ChartTab> = {
    Encounter: 'encounters',
    Vitals: 'vitals',
    Diagnosis: 'diagnoses',
    Prescription: 'prescriptions',
    Order: 'orders',
    Result: 'orders',
    Note: 'notes',
    Immunization: 'immunisations',
    Document: 'documents',
    DischargeSummary: 'admissions',
  };
  return { kind: 'tab', tab: tab[entry.type] };
}
