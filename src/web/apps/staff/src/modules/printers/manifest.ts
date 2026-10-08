import { PrintRegular } from '@fluentui/react-icons';
import { isDesktop } from '@hc360/core';
import type { ModuleManifest } from '@/lib/modules';
import { PRINTERS_SETUP } from './form';

/**
 * Printers on this computer: only inside HC360 Desktop, which keeps them. Staff who set up printers find it in the
 * menu and the command palette; everyone else opens it from their user menu to see the printers and print a test.
 */
const manifest: ModuleManifest = {
  id: 'printers',
  title: 'Printers',
  section: 'admin',
  icon: PrintRegular,
  order: 860,
  links: isDesktop
    ? [
        {
          label: 'Printers on this computer',
          to: '/workstation/printers',
          permission: PRINTERS_SETUP,
          keywords: [
            'printer',
            'label printer',
            'wristband printer',
            'receipt printer',
            'zebra',
            'tsc',
            'zpl',
            'tspl',
            'barcode',
            'test print',
            'scanner',
            'barcode scanner',
            'serial scanner',
            'com port',
          ],
        },
      ]
    : [],
};

export default manifest;
