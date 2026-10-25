import { DocumentLockRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';

const READ = 'mtp.read';
const CERTIFY = 'mtp.certify';
const REGISTER = 'mtp.register';

const manifest: ModuleManifest = {
  id: 'mtp',
  title: 'Termination of pregnancy',
  section: 'clinical',
  icon: DocumentLockRegular,
  order: 55,
  links: [
    {
      label: 'MTP cases',
      to: '/mtp',
      permission: [READ, CERTIFY],
      keywords: ['mtp', 'abortion', 'termination of pregnancy', 'form i', 'form c', 'consent', 'opinion'],
    },
    {
      label: 'MTP register',
      to: '/mtp/register',
      permission: REGISTER,
      keywords: [
        'mtp',
        'abortion',
        'termination of pregnancy',
        'form iii',
        'form ii',
        'admission register',
        'monthly report',
      ],
    },
  ],
};

export default manifest;
