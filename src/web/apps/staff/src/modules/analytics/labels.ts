import type {
  DeliveryStatus,
  DeliveryTrigger,
  ExportFormat,
  RecipientStatus,
  ReportCategory,
  ReportChartType,
  ScheduleFrequency,
  ScheduleRunStatus,
  Weekday,
  WidgetWidth,
} from './api';

/** Server permission names (docs/modules/analytics.md). */
export const ANALYTICS_PERMISSIONS = {
  misRead: 'analytics.mis.read',
  financeRead: 'analytics.finance.read',
  clinicalRead: 'analytics.clinical.read',
  dashboardsManage: 'analytics.dashboards.manage',
  schedulesManage: 'analytics.schedules.manage',
} as const;

/** Any analytics reader: the server filters the catalogue per report. */
export const ANY_ANALYTICS: readonly string[] = [
  ANALYTICS_PERMISSIONS.misRead,
  ANALYTICS_PERMISSIONS.financeRead,
  ANALYTICS_PERMISSIONS.clinicalRead,
];

export const CATEGORY_ORDER: readonly ReportCategory[] = [
  'OutPatient',
  'InPatient',
  'Revenue',
  'Diagnostics',
  'Pharmacy',
  'SpecialtyCare',
  'Quality',
  'Experience',
  'People',
];

export const CATEGORY_LABELS: Record<ReportCategory, string> = {
  OutPatient: 'Out-patient',
  InPatient: 'In-patient and beds',
  Revenue: 'Revenue and collections',
  Diagnostics: 'Laboratory and radiology',
  Pharmacy: 'Pharmacy and stores',
  SpecialtyCare: 'OT, dialysis and blood bank',
  Quality: 'Quality and safety',
  Experience: 'Patient experience',
  People: 'People',
};

export const CHART_TYPES: readonly ReportChartType[] = ['Bar', 'StackedBar', 'Line', 'Area', 'Pie', 'Table', 'Summary'];

export const CHART_TYPE_LABELS: Record<ReportChartType, string> = {
  Bar: 'Bars',
  StackedBar: 'Stacked bars',
  Line: 'Line',
  Area: 'Area',
  Pie: 'Pie',
  Table: 'Table',
  Summary: 'Headline figures',
};

/** The period keys every report shares (mirrors the server's ReportPeriods; the catalogue sends the same list). */
export const CUSTOM_PERIOD = 'custom';

export const PERIOD_PRESETS: readonly { value: string; label: string }[] = [
  { value: 'today', label: 'Today' },
  { value: 'yesterday', label: 'Yesterday' },
  { value: 'last7Days', label: 'Last 7 days' },
  { value: 'last30Days', label: 'Last 30 days' },
  { value: 'thisWeek', label: 'This week' },
  { value: 'lastWeek', label: 'Last week' },
  { value: 'thisMonth', label: 'This month' },
  { value: 'lastMonth', label: 'Last month' },
  { value: 'thisQuarter', label: 'This quarter' },
  { value: 'lastQuarter', label: 'Last quarter' },
  { value: 'thisFinancialYear', label: 'This financial year' },
  { value: 'lastFinancialYear', label: 'Last financial year' },
  { value: CUSTOM_PERIOD, label: 'Custom dates' },
];

export const DEFAULT_PERIOD = 'thisMonth';

export const WEEKDAYS: readonly Weekday[] = [
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
  'Sunday',
];

/** Day-of-month value that means "the last day of the month". */
export const LAST_DAY_OF_MONTH = 31;

export const FREQUENCIES: readonly ScheduleFrequency[] = ['Daily', 'Weekly', 'Monthly'];

export const FREQUENCY_LABELS: Record<ScheduleFrequency, string> = {
  Daily: 'Every day',
  Weekly: 'Every week',
  Monthly: 'Every month',
};

export const FORMAT_LABELS: Record<ExportFormat, string> = {
  Xlsx: 'Excel workbook (.xlsx)',
  Csv: 'CSV file (.csv)',
  Pdf: 'PDF, print-ready (.pdf)',
};

/** File name extension of each export format (the server names its files the same way). */
export const FORMAT_EXTENSIONS: Record<ExportFormat, string> = { Xlsx: 'xlsx', Csv: 'csv', Pdf: 'pdf' };

export const RUN_STATUS_LABELS: Record<ScheduleRunStatus, string> = {
  Succeeded: 'Sent',
  Failed: 'Failed',
  Skipped: 'Skipped',
};

export const DELIVERY_STATUS_LABELS: Record<DeliveryStatus, string> = {
  Generating: 'Generating',
  Ready: 'Ready',
  Failed: 'Failed',
  Skipped: 'Skipped',
};

export const DELIVERY_TRIGGER_LABELS: Record<DeliveryTrigger, string> = {
  Scheduled: 'On schedule',
  Manual: 'Sent now',
};

export const RECIPIENT_STATUS_LABELS: Record<RecipientStatus, string> = {
  Pending: 'Waiting to send',
  Sent: 'Sent',
  Cancelled: 'Not sent',
};

export const WIDGET_WIDTHS: readonly { value: WidgetWidth; label: string }[] = [
  { value: 1, label: 'Small' },
  { value: 2, label: 'Medium' },
  { value: 4, label: 'Full width' },
];

/** Most-used reports, offered as shortcuts on the executive dashboard (only those the user may open are shown). */
export const FEATURED_REPORTS: readonly string[] = [
  'op-census',
  'bed-occupancy',
  'revenue-by-department',
  'daily-revenue',
  'lab-volume-tat',
  'discharge-outcomes',
  'outstanding-ageing',
  'feedback-nps',
];

export const PAGE_SIZES: readonly number[] = [25, 50, 100, 200];
export const DEFAULT_PAGE_SIZE = 50;
