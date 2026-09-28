import { XrayRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';

const READ = 'radiology.studies.read';
const WORKLIST = 'radiology.worklist';
const WRITE = 'radiology.reports.write';
const VERIFY = 'radiology.reports.verify';

const manifest: ModuleManifest = {
  id: 'radiology',
  title: 'Radiology',
  section: 'diagnostics',
  icon: XrayRegular,
  order: 20,
  links: [
    { label: 'Imaging worklist', to: '/radiology', permission: [READ, WORKLIST, WRITE], keywords: ['x-ray', 'ct', 'mri', 'ultrasound', 'scan', 'pacs', 'ris'] },
    { label: 'Reporting worklist', to: '/radiology/reporting', permission: [WRITE, VERIFY], keywords: ['radiologist', 'report', 'ai triage', 'sign off'] },
    { label: 'Imaging turnaround', to: '/radiology/tat', permission: [READ, VERIFY], keywords: ['tat', 'repeat rate', 'reject analysis'] },
    { label: 'Procedures and templates', to: '/radiology/setup', permission: [VERIFY, 'masters.manage'], keywords: ['imaging procedure', 'report template'] },
  ],
};

export default manifest;
