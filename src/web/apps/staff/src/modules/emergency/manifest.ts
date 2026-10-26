import { HeartPulseRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { ER_READERS, MLC_READERS } from './api';

const manifest: ModuleManifest = {
  id: 'emergency',
  title: 'Emergency',
  section: 'clinical',
  icon: HeartPulseRegular,
  order: 5,
  links: [
    {
      label: 'ER board',
      to: '/emergency',
      permission: [...ER_READERS],
      keywords: ['emergency', 'casualty', 'triage', 'esi', 'er', 'trauma', 'disposition'],
    },
    {
      label: 'MLC register',
      to: '/emergency/mlc',
      permission: [...MLC_READERS],
      keywords: ['mlc', 'medico-legal', 'police intimation', 'accident register', 'wound certificate'],
    },
  ],
};

export default manifest;
