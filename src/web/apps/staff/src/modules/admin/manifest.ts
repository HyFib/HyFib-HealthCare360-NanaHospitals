import { SettingsRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { SystemHealthWidget } from './SystemHealthWidget';

const manifest: ModuleManifest = {
  id: 'admin',
  title: 'Administration',
  section: 'admin',
  icon: SettingsRegular,
  order: 900,
  links: [
    { label: 'Users', to: '/admin/users', permission: 'admin.users.read', keywords: ['staff', 'login', 'password', 'account'] },
    { label: 'Roles and permissions', to: '/admin/roles', permission: 'admin.users.read', keywords: ['access', 'rights'] },
    { label: 'Devices', to: '/admin/devices', permission: 'admin.devices.manage', keywords: ['workstation', 'kiosk', 'enrol', 'tablet'] },
    { label: 'System health', to: '/admin/system', permission: 'admin.system.read', keywords: ['wifi', 'server', 'ups', 'disk', 'certificate'] },
    { label: 'Audit log', to: '/admin/audit', permission: 'admin.audit.read', keywords: ['security', 'who accessed', 'history'] },
    { label: 'Settings', to: '/admin/settings', permission: 'admin.settings.manage', keywords: ['configuration', 'sms', 'whatsapp'] },
    { label: 'Backups', to: '/admin/backups', permission: 'admin.backups.manage', keywords: ['restore', 'recovery key'] },
  ],
  widgets: [{ id: 'admin-system-health', permission: 'admin.system.read', span: 2, order: 900, component: SystemHealthWidget }],
};

export default manifest;
