import { hasPermission, useSession, type LookupItem } from '@hc360/core';
import type {
  AnalyticsStatus,
  DashboardDetail,
  DashboardListItem,
  DeliveryDto,
  ExtractFolder,
  KpiDefinitions,
  KpiSet,
  ReportCatalogue,
  ReportCatalogueItem,
  ReportParameter,
  ReportResult,
  ScheduleDto,
} from '@/modules/analytics/api';
import { PERIOD_PRESETS } from '@/modules/analytics/labels';
import { FACILITY_ID, paged, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Analytics (MIS/BI): the report catalogue with an OPD census report (the report screen opens it), its result as a
// table, the executive KPIs, dashboards (a template, one of mine, one shared with me), scheduled e-mails and extracts.
// Nothing here draws a chart: charts paint on a canvas, which the test browser (jsdom) has not got, and a page that
// draws one fails. So reports carry no chart, KPIs no daily trend, and dashboard tiles show tables.

const now = Date.now();
const hoursAgo = (hours: number) => new Date(now - hours * 3600_000).toISOString();
const daysAgo = (days: number) => hoursAgo(days * 24);
const dateAgo = (days: number) => daysAgo(days).slice(0, 10);
const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;

// ---------------------------------------------------------------------------------------------------- catalogue

const PERIOD: ReportParameter = {
  key: 'period',
  label: 'Period',
  kind: 'Period',
  required: true,
  defaultValue: 'thisMonth',
  choices: null,
  min: null,
  max: null,
  hint: null,
};
const DEPARTMENT: ReportParameter = {
  key: 'departmentId',
  label: 'Department',
  kind: 'Department',
  required: false,
  defaultValue: null,
  choices: null,
  min: null,
  max: null,
  hint: 'All departments when empty',
};

const report = (
  reportId: string,
  title: string,
  category: ReportCatalogueItem['category'],
  categoryLabel: string,
  description: string,
  columns: ReportCatalogueItem['columns'],
  more: Partial<ReportCatalogueItem> = {},
): ReportCatalogueItem => ({
  id: reportId,
  title,
  category,
  categoryLabel,
  description,
  definition: description,
  parameters: [PERIOD, DEPARTMENT],
  columns,
  chart: null,
  containsPatientIdentifiers: false,
  canSchedule: true,
  maxDays: 366,
  ...more,
});

const CENSUS_COLUMNS: ReportCatalogueItem['columns'] = [
  { key: 'department', label: 'Department', type: 'Text', isPatientIdentifier: false },
  { key: 'newVisits', label: 'New visits', type: 'Integer', isPatientIdentifier: false },
  { key: 'followUps', label: 'Follow-ups', type: 'Integer', isPatientIdentifier: false },
  { key: 'revenue', label: 'Consultation revenue', type: 'Money', isPatientIdentifier: false },
];

const REPORTS: ReportCatalogueItem[] = [
  report(
    RECORD_ID,
    'OPD census by department',
    'OutPatient',
    'Out-patients',
    'New and follow-up OPD visits per department.',
    CENSUS_COLUMNS,
  ),
  report(
    'bed-occupancy',
    'Bed occupancy',
    'InPatient',
    'In-patients',
    'Occupied bed-days against available bed-days per ward.',
    [
      { key: 'ward', label: 'Ward', type: 'Text', isPatientIdentifier: false },
      { key: 'occupancy', label: 'Occupancy', type: 'Percent', isPatientIdentifier: false },
    ],
  ),
  report(
    'revenue-by-department',
    'Revenue by department',
    'Revenue',
    'Revenue',
    'Billed and collected amounts per department.',
    [
      { key: 'department', label: 'Department', type: 'Text', isPatientIdentifier: false },
      { key: 'billed', label: 'Billed', type: 'Money', isPatientIdentifier: false },
    ],
  ),
  report(
    'discharge-list',
    'Discharges with diagnosis',
    'InPatient',
    'In-patients',
    'Every discharge with the patient and diagnosis.',
    [
      { key: 'uhid', label: 'UHID', type: 'Text', isPatientIdentifier: true },
      { key: 'diagnosis', label: 'Diagnosis', type: 'Text', isPatientIdentifier: false },
    ],
    { containsPatientIdentifiers: true, canSchedule: false },
  ),
];

const CATALOGUE: ReportCatalogue = {
  categories: [
    { id: 'OutPatient', label: 'Out-patients', count: 1 },
    { id: 'InPatient', label: 'In-patients', count: 2 },
    { id: 'Revenue', label: 'Revenue', count: 1 },
  ],
  reports: REPORTS,
  periods: [...PERIOD_PRESETS],
  canManageDashboards: true,
  canManageSchedules: true,
  canSeePatientLevel: true,
  canConsolidate: true,
  dataSource: 'Reporting replica',
};

/** Any report's result, as a table: OPD visits per department this month. */
const result = (request: FakeRequest): ReportResult => {
  const reportId = decodeURIComponent(request.path.split('/').at(-2) ?? RECORD_ID);
  const found = REPORTS.find((r) => r.id === reportId) ?? REPORTS[0]!;
  return {
    reportId: found.id,
    title: found.title,
    category: found.category,
    from: `${dateAgo(0).slice(0, 8)}01`,
    to: dateAgo(0),
    facilityName: 'Nana Hospitals, Pune',
    filters: [{ label: 'Period', value: 'This month' }],
    parameters: { period: 'thisMonth' },
    columns: CENSUS_COLUMNS,
    rows: [
      ['General medicine', 412, 655, 4_38_600],
      ['Cardiology', 128, 301, 3_34_500],
      ['Orthopaedics', 96, 188, 2_27_200],
    ],
    rowKeys: ['0192a000-0000-7000-8000-00000000de01', id('de02'), id('de03')],
    totals: ['Total', 636, 1144, 10_00_300],
    summary: [
      { key: 'visits', label: 'OPD visits', value: 1780, type: 'Integer' },
      { key: 'revenue', label: 'Consultation revenue', value: 10_00_300, type: 'Money' },
    ],
    chart: null,
    drillDown: null,
    notes: ['Cancelled visits are not counted.'],
    page: 1,
    pageSize: 50,
    totalRows: 3,
    truncated: false,
    sort: null,
    desc: false,
    generatedAt: hoursAgo(0.01),
    dataSource: 'Reporting replica',
    containsPatientIdentifiers: false,
    pdfNotice: null,
  };
};

const LOOKUPS: Record<string, LookupItem[]> = {
  facilities: [
    { id: FACILITY_ID, code: 'NHP', name: 'Nana Hospitals, Pune' },
    { id: '0192a000-0000-7000-8000-0000000000f2', code: 'NHM', name: 'Nana Hospitals, Mumbai' },
  ],
  departments: [
    { id: '0192a000-0000-7000-8000-00000000de01', code: 'GM', name: 'General medicine' },
    { id: id('de02'), code: 'CARD', name: 'Cardiology' },
  ],
  practitioners: [
    { id: '0192a000-0000-7000-8000-0000000000d1', code: 'D001', name: 'Dr. Meera Iyer' },
    { id: '0192a000-0000-7000-8000-0000000000d2', code: 'D002', name: 'Dr. Anil Rao' },
  ],
  payers: [{ id: id('9a01'), code: 'STAR', name: 'Star Health and Allied Insurance' }],
  wards: [{ id: '0192a000-0000-7000-8000-00000000e001', code: 'GWM', name: 'General Ward — Male' }],
  stores: [{ id: id('5701'), code: 'MAIN', name: 'Main store' }],
  theatres: [{ id: id('07a1'), code: 'OT1', name: 'Operation theatre 1' }],
};

/** /analytics/lookups/{kind}: the facilities, departments… a report input picks from. */
const lookup = (request: FakeRequest) => LOOKUPS[request.path.split('/').at(-1) ?? ''] ?? [];

// ---------------------------------------------------------------------------------------------------- KPIs

const kpi = (
  key: string,
  label: string,
  unit: KpiSet['kpis'][number]['unit'],
  value: number,
  previous: number,
  higherIsBetter: boolean,
  reportId: string | null,
): KpiSet['kpis'][number] => ({
  key,
  label,
  unit,
  value,
  previous,
  changePercent: Math.round(((value - previous) / previous) * 1000) / 10,
  higherIsBetter,
  reportId,
  definition: `${label} in the period.`,
  trend: null,
});

const KPIS: KpiSet = {
  from: `${dateAgo(0).slice(0, 8)}01`,
  to: dateAgo(0),
  previousFrom: dateAgo(55),
  previousTo: dateAgo(31),
  facilityName: 'Nana Hospitals, Pune',
  kpis: [
    kpi('opVisits', 'OPD visits', 'count', 1780, 1690, true, RECORD_ID),
    kpi('bedOccupancy', 'Bed occupancy', 'percent', 82.4, 78.9, true, 'bed-occupancy'),
    kpi('averageLengthOfStay', 'Average length of stay', 'days', 3.8, 4.1, false, null),
    kpi('revenue', 'Revenue billed', 'inr', 1_84_50_000, 1_76_20_000, true, 'revenue-by-department'),
    kpi('nps', 'Net promoter score', 'score', 44, 41, true, null),
  ],
  generatedAt: hoursAgo(0.05),
  dataSource: 'Reporting replica',
};

const DEFINITIONS: KpiDefinitions = {
  kpis: [
    {
      key: 'opVisits',
      label: 'OPD visits',
      unit: 'count',
      higherIsBetter: true,
      formula: 'Count of OPD visits checked in, cancelled visits excluded',
      source: 'Visits',
      target: null,
      audience: 'Operations',
      reportId: RECORD_ID,
      reportTitle: 'OPD census by department',
    },
    {
      key: 'bedOccupancy',
      label: 'Bed occupancy',
      unit: 'percent',
      higherIsBetter: true,
      formula: 'Occupied bed-days ÷ available bed-days × 100',
      source: 'Admissions and beds',
      target: '85%',
      audience: 'Operations',
      reportId: 'bed-occupancy',
      reportTitle: 'Bed occupancy',
    },
    {
      key: 'revenue',
      label: 'Revenue billed',
      unit: 'inr',
      higherIsBetter: true,
      formula: 'Sum of final bill amounts before discounts',
      source: 'Bills',
      target: null,
      audience: 'Finance',
      reportId: 'revenue-by-department',
      reportTitle: 'Revenue by department',
    },
  ],
  reports: REPORTS.map((r) => ({
    id: r.id,
    title: r.title,
    category: r.category,
    categoryLabel: r.categoryLabel,
    description: r.description,
    definition: r.definition,
    containsPatientIdentifiers: r.containsPatientIdentifiers,
    canSchedule: r.canSchedule,
    kpiKeys: r.id === RECORD_ID ? ['opVisits'] : [],
  })),
};

// ---------------------------------------------------------------------------------------------------- dashboards

const DASHBOARDS: DashboardListItem[] = [
  {
    id: RECORD_ID,
    name: 'Daily operations',
    description: 'OPD, beds and revenue at a glance',
    isShared: true,
    isTemplate: false,
    isMine: true,
    canEdit: true,
    ownerName: 'Dr. Meera Iyer',
    widgetCount: 2,
    updatedAt: daysAgo(2),
    version: 5,
  },
  {
    id: id('db02'),
    name: 'Hospital overview',
    description: 'The standard dashboard',
    isShared: true,
    isTemplate: true,
    isMine: false,
    canEdit: true,
    ownerName: null,
    widgetCount: 6,
    updatedAt: daysAgo(60),
    version: 1,
  },
  {
    id: id('db03'),
    name: 'Finance weekly',
    description: null,
    isShared: true,
    isTemplate: false,
    isMine: false,
    canEdit: false,
    ownerName: 'CA Rajesh Gupta',
    widgetCount: 4,
    updatedAt: daysAgo(6),
    version: 2,
  },
];

const DASHBOARD: DashboardDetail = {
  id: RECORD_ID,
  name: 'Daily operations',
  description: 'OPD, beds and revenue at a glance',
  isShared: true,
  isTemplate: false,
  isMine: true,
  canEdit: true,
  ownerName: 'Dr. Meera Iyer',
  updatedAt: daysAgo(2),
  version: 5,
  widgets: [
    {
      id: id('w001'),
      reportId: RECORD_ID,
      title: 'OPD census',
      view: 'Table',
      width: 2,
      parameters: { period: 'today' },
      canView: true,
      reportTitle: 'OPD census by department',
    },
    {
      id: id('w002'),
      reportId: 'revenue-by-department',
      title: 'Revenue this month',
      view: 'Summary',
      width: 2,
      parameters: { period: 'thisMonth' },
      canView: true,
      reportTitle: 'Revenue by department',
    },
  ],
};

// ---------------------------------------------------------------------------------------------------- schedules, extracts

const schedule = (scheduleId: string, name: string, more: Partial<ScheduleDto> = {}): ScheduleDto => ({
  id: scheduleId,
  name,
  reportId: RECORD_ID,
  reportTitle: 'OPD census by department',
  parameters: { period: 'yesterday' },
  format: 'Xlsx',
  frequency: 'Daily',
  time: '07:00',
  dayOfWeek: null,
  dayOfMonth: null,
  when: 'Every day at 7:00 am',
  recipients: ['md@nanahospitals.in', 'ops@nanahospitals.in'],
  isActive: true,
  ownerName: 'Dr. Meera Iyer',
  isMine: true,
  nextRunAt: hoursAgo(-14),
  lastRunAt: hoursAgo(10),
  lastStatus: 'Succeeded',
  lastError: null,
  version: 3,
  destination: 'Email',
  extractName: null,
  extractFile: null,
  lastSucceededAt: hoursAgo(10),
  extractState: null,
  ...more,
});

const SCHEDULES: ScheduleDto[] = [
  schedule(RECORD_ID, 'Daily OPD census to management'),
  schedule(id('5c02'), 'Weekly revenue to accounts', {
    reportId: 'revenue-by-department',
    reportTitle: 'Revenue by department',
    frequency: 'Weekly',
    dayOfWeek: 'Monday',
    when: 'Every Monday at 8:00 am',
    time: '08:00',
    format: 'Pdf',
    isMine: false,
    ownerName: 'CA Rajesh Gupta',
    lastStatus: 'Failed',
    lastError: 'The mail server refused the message.',
  }),
  schedule(id('5c03'), 'Monthly bed occupancy', {
    isActive: false,
    reportId: 'bed-occupancy',
    reportTitle: 'Bed occupancy',
    frequency: 'Monthly',
    dayOfMonth: 1,
    when: 'On the 1st at 7:00 am',
  }),
];

const EXTRACTS: ScheduleDto[] = [
  schedule(id('e701'), 'OPD census for Power BI', {
    destination: 'ExtractFolder',
    format: 'Csv',
    recipients: [],
    frequency: 'Hourly',
    when: 'Every hour',
    parameters: { period: 'thisFinancialYear' },
    extractName: 'opd-census',
    extractFile: 'NHP\\opd-census.csv',
    extractState: 'Fresh',
    lastRunAt: hoursAgo(0.3),
    lastSucceededAt: hoursAgo(0.3),
  }),
  schedule(id('e702'), 'Revenue for Tally', {
    destination: 'ExtractFolder',
    format: 'Csv',
    recipients: [],
    reportId: 'revenue-by-department',
    reportTitle: 'Revenue by department',
    extractName: 'revenue',
    extractFile: 'NHP\\revenue.csv',
    extractState: 'Failing',
    lastStatus: 'Failed',
    lastError: 'The extract folder is not writable.',
    lastSucceededAt: daysAgo(2),
  }),
];

const DELIVERIES: DeliveryDto[] = [
  {
    id: id('de11'),
    scheduleId: RECORD_ID,
    reportTitle: 'OPD census by department',
    trigger: 'Scheduled',
    createdAt: hoursAgo(10),
    status: 'Ready',
    periodFrom: dateAgo(1),
    periodTo: dateAgo(1),
    format: 'Xlsx',
    fileName: 'opd-census.xlsx',
    sizeBytes: 18_432,
    rowCount: 12,
    fileAvailable: true,
    error: null,
    sent: 2,
    pending: 0,
    recipients: [
      { email: 'md@nanahospitals.in', status: 'Sent', attempts: 1, lastError: null, sentAt: hoursAgo(10) },
      { email: 'ops@nanahospitals.in', status: 'Sent', attempts: 1, lastError: null, sentAt: hoursAgo(10) },
    ],
  },
];

const STATUS: AnalyticsStatus = {
  dataSource: 'Reporting replica',
  usesReplica: true,
  replicaLagSeconds: 4,
  activeSchedules: 2,
  lastDeliveryAt: hoursAgo(10),
  failedDeliveriesLast7Days: 1,
  checkedAt: hoursAgo(0.01),
  activeExtracts: 2,
  lateExtracts: 0,
  failingExtracts: 1,
  lastExtractRefreshAt: hoursAgo(0.3),
};

const FOLDER: ExtractFolder = {
  root: 'D:\\HC360\\extracts',
  facilityFolder: 'D:\\HC360\\extracts\\NHP',
  refreshLog: 'D:\\HC360\\extracts\\refresh-log.csv',
  problem: null,
};

/** Scheduled e-mails or extracts, by the `destination` asked for. */
const schedules = (request: FakeRequest) =>
  request.url.searchParams.get('destination') === 'ExtractFolder' ? EXTRACTS : SCHEDULES;

const AS_FOR_EVERY_PERMISSION: FakeAnswers = {
  'GET /api/v1/analytics/reports': CATALOGUE,
  'GET /api/v1/analytics/reports/*': result,
  'GET /api/v1/analytics/lookups/*': lookup,
  'GET /api/v1/analytics/kpis': KPIS,
  'GET /api/v1/analytics/kpi-definitions': DEFINITIONS,
  'GET /api/v1/analytics/status': STATUS,
  'GET /api/v1/analytics/dashboards': DASHBOARDS,
  [`GET /api/v1/analytics/dashboards/${RECORD_ID}`]: DASHBOARD,
  'GET /api/v1/analytics/schedules': schedules,
  'GET /api/v1/analytics/schedules/*': paged(DELIVERIES),
  'GET /api/v1/analytics/extracts/folder': FOLDER,
};

/**
 * What the signed-in user may do with a report or dashboard, as the server works it out from their permissions: the
 * answers above are as a user with every permission sees them, and a role without those permissions is told no.
 */
const MAY: Record<string, string> = {
  canEdit: 'analytics.dashboards.manage',
  canManageDashboards: 'analytics.dashboards.manage',
  canManageSchedules: 'analytics.schedules.manage',
  canSeePatientLevel: 'analytics.clinical.read',
};

function asSignedIn(answer: unknown): unknown {
  if (Array.isArray(answer)) {
    return answer.map(asSignedIn);
  }
  if (answer === null || typeof answer !== 'object') {
    return answer;
  }
  const user = useSession.getState().user;
  return Object.fromEntries(
    Object.entries(answer).map(([key, value]) => [
      key,
      key in MAY && typeof value === 'boolean' ? value && hasPermission(user, MAY[key]) : asSignedIn(value),
    ]),
  );
}

export const ANSWERS: FakeAnswers = Object.fromEntries(
  Object.entries(AS_FOR_EVERY_PERMISSION).map(([key, answer]) => [
    key,
    typeof answer === 'function'
      ? (request: FakeRequest) => asSignedIn((answer as (r: FakeRequest) => unknown)(request))
      : () => asSignedIn(answer),
  ]),
);
