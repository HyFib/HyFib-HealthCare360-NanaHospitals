import { BeakerEditRegular, BeakerRegular, DropRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { CriticalResultsWidget, LabTatWidget } from './widgets';

const P = {
  read: 'lab.orders.read',
  create: 'lab.orders.create',
  collect: 'lab.samples.collect',
  receive: 'lab.samples.receive',
  enter: 'lab.results.enter',
  verify: 'lab.results.verify',
  qc: 'lab.qc.manage',
  masters: 'lab.masters.manage',
} as const;

const manifest: ModuleManifest = {
  id: 'lab',
  title: 'Laboratory',
  section: 'diagnostics',
  icon: BeakerRegular,
  order: 10,
  links: [
    {
      label: 'Lab overview',
      to: '/lab',
      permission: [P.read, P.collect, P.receive, P.enter, P.verify, P.qc],
      keywords: ['lis', 'laboratory', 'dashboard'],
    },
    {
      label: 'Lab orders',
      to: '/lab/orders',
      permission: P.read,
      keywords: ['accession', 'walk-in', 'test booking', 'investigation'],
    },
    {
      label: 'Sample collection',
      to: '/lab/collection',
      permission: P.collect,
      keywords: ['phlebotomy', 'blood draw', 'barcode label', 'tube'],
    },
    {
      label: 'Receive samples',
      to: '/lab/receive',
      permission: P.receive,
      keywords: ['scan', 'accession', 'reject', 'recollect'],
    },
    {
      label: 'Result entry',
      to: '/lab/worklist',
      permission: [P.enter, P.verify],
      keywords: ['worklist', 'verify', 'bench', 'results', 'critical'],
    },
    { label: 'Lab reports', to: '/lab/reports', permission: P.read, keywords: ['release', 'print', 'amend', 'qr'] },
    {
      label: 'Quality control',
      to: '/lab/qc',
      permission: [P.qc, P.verify],
      keywords: ['qc', 'westgard', 'levey-jennings', 'control lot'],
    },
    {
      label: 'Outsourced tests',
      to: '/lab/outsourcing',
      permission: [P.read, P.receive],
      keywords: ['referral lab', 'send out', 'reference laboratory'],
    },
    {
      label: 'Lab turnaround',
      to: '/lab/tat',
      permission: P.read,
      keywords: ['tat', 'turnaround', 'rejection rate', 'nabh'],
    },
    {
      label: 'Critical call register',
      to: '/lab/critical-calls',
      permission: P.read,
      keywords: ['critical value', 'panic value', 'nabh'],
    },
    {
      label: 'Lab setup',
      to: '/lab/setup',
      permission: P.masters,
      keywords: ['test master', 'reference range', 'analyser', 'panel'],
    },
  ],
  quickActions: [
    {
      label: 'Collect samples',
      to: '/lab/collection',
      permission: P.collect,
      icon: DropRegular,
      description: 'Draw blood and print tube labels',
    },
    {
      label: 'Enter results',
      to: '/lab/worklist',
      permission: [P.enter, P.verify],
      icon: BeakerEditRegular,
      description: 'Bench worklist and verification',
    },
  ],
  widgets: [
    {
      id: 'lab-critical-results',
      permission: [P.enter, P.verify],
      span: 2,
      order: 40,
      component: CriticalResultsWidget,
    },
    { id: 'lab-tat-today', permission: P.read, span: 2, order: 41, component: LabTatWidget },
  ],
};

export default manifest;
