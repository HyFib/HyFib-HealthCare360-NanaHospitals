import { HeartPulseRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';

const READ = 'cardiology.studies.read';
const MANAGE = 'cardiology.studies.manage';
const VERIFY = 'cardiology.reports.verify';

const manifest: ModuleManifest = {
  id: 'cardiology',
  title: 'Cardiology',
  section: 'diagnostics',
  icon: HeartPulseRegular,
  order: 30,
  links: [
    {
      label: 'Cardiology studies',
      to: '/cardiology',
      permission: [READ, MANAGE, VERIFY],
      keywords: ['ecg', 'echo', 'tmt', 'holter', 'abpm', 'stress test'],
    },
    {
      label: 'Cardiology reporting',
      to: '/cardiology/reporting',
      permission: VERIFY,
      keywords: ['cardiologist', 'sign off', 'ecg report'],
    },
  ],
};

export default manifest;
