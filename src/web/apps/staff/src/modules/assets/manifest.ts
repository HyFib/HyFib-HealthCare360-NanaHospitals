import { WrenchRegular, WrenchScrewdriverRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { ASSET_READERS, SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { EquipmentAttentionWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'assets',
  title: 'Equipment and assets',
  section: 'support',
  icon: WrenchScrewdriverRegular,
  order: 40,
  links: [
    {
      label: 'Equipment overview',
      to: '/assets',
      permission: ASSET_READERS,
      keywords: ['biomedical', 'dashboard', 'downtime'],
    },
    {
      label: 'Asset register',
      to: '/assets/register',
      permission: ASSET_READERS,
      keywords: ['equipment', 'asset tag', 'amc', 'cmc', 'warranty'],
    },
    {
      label: 'Breakdown tickets',
      to: '/assets/tickets',
      permission: ASSET_READERS,
      keywords: ['repair', 'complaint', 'not working'],
    },
    {
      label: 'Maintenance and calibration',
      to: '/assets/maintenance',
      permission: ASSET_READERS,
      keywords: ['pm', 'preventive', 'calibration', 'nabh'],
    },
    {
      label: 'Condemnation',
      to: '/assets/condemnations',
      permission: ASSET_READERS,
      keywords: ['write off', 'dispose', 'scrap', 'e-waste'],
    },
  ],
  quickActions: [
    {
      label: 'Report equipment breakdown',
      to: '/assets/tickets',
      permission: S.assetsRead,
      icon: WrenchRegular,
      description: 'Tell biomedical engineering',
    },
  ],
  widgets: [
    {
      id: 'assets-attention',
      permission: [S.assetsMaintenance, S.assetsManage],
      span: 2,
      order: 60,
      component: EquipmentAttentionWidget,
    },
  ],
};

export default manifest;
