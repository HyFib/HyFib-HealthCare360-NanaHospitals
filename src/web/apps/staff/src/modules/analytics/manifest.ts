import { DataTrendingRegular, DocumentTableRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { ANALYTICS_PERMISSIONS as A, ANY_ANALYTICS } from './labels';
import { AnalyticsSnapshotWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'analytics',
  title: 'Analytics',
  section: 'insights',
  icon: DataTrendingRegular,
  order: 10,
  links: [
    {
      label: 'Executive dashboard',
      to: '/analytics',
      permission: ANY_ANALYTICS,
      keywords: ['kpi', 'bi', 'occupancy', 'revenue', 'collections', 'alos', 'nps', 'management'],
    },
    {
      label: 'MIS reports',
      to: '/analytics/reports',
      permission: ANY_ANALYTICS,
      keywords: [
        'mis',
        'census',
        'report',
        'excel',
        'export',
        'turnaround',
        'tat',
        'no-show',
        'ageing',
        'near expiry',
        'discharge',
      ],
    },
    {
      label: 'KPI definitions',
      to: '/analytics/kpis',
      permission: ANY_ANALYTICS,
      keywords: ['kpi', 'definition', 'formula', 'glossary', 'how calculated', 'metric', 'indicator'],
    },
    {
      label: 'Dashboards',
      to: '/analytics/dashboards',
      permission: ANY_ANALYTICS,
      keywords: ['bi', 'charts', 'saved views', 'widgets'],
    },
    {
      label: 'Scheduled e-mails',
      to: '/analytics/schedules',
      permission: A.schedulesManage,
      keywords: ['email', 'daily report', 'weekly report', 'subscription'],
    },
    {
      label: 'BI extracts',
      to: '/analytics/extracts',
      permission: A.schedulesManage,
      keywords: ['power bi', 'bi', 'csv', 'extract', 'data feed', 'refresh', 'tableau', 'qlik'],
    },
  ],
  quickActions: [
    {
      label: 'MIS reports',
      to: '/analytics/reports',
      permission: ANY_ANALYTICS,
      icon: DocumentTableRegular,
      description: 'Census, occupancy, revenue, TAT… with Excel and PDF export',
    },
  ],
  widgets: [
    { id: 'analytics-snapshot', permission: ANY_ANALYTICS, span: 2, order: 90, component: AnalyticsSnapshotWidget },
  ],
};

export default manifest;
