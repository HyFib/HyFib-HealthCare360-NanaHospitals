import { BeakerRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';

// Temporary stub while the module is being built (no links yet, so nothing shows in navigation).
const manifest: ModuleManifest = { id: 'cardiology', title: 'Cardiology', section: 'diagnostics', icon: BeakerRegular, order: 900, links: [] };

export default manifest;
