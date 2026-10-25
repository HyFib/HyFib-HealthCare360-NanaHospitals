import { VehicleTruckProfileRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { AMBULANCE_PERMISSIONS, AMBULANCE_READERS } from './api';

const manifest: ModuleManifest = {
  id: 'ambulance',
  title: 'Ambulance',
  section: 'support',
  icon: VehicleTruckProfileRegular,
  order: 50,
  links: [
    {
      label: 'Ambulance',
      to: '/ambulance',
      permission: [...AMBULANCE_READERS],
      keywords: ['ambulance', 'dispatch', 'trip', 'fleet', 'bls', 'als', 'hearse', 'pickup', 'transfer'],
    },
  ],
  quickActions: [
    {
      label: 'Take an ambulance call',
      to: '/ambulance',
      permission: AMBULANCE_PERMISSIONS.dispatch,
      icon: VehicleTruckProfileRegular,
      description: 'Record the call and send a vehicle',
    },
  ],
};

export default manifest;
