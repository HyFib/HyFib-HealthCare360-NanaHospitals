import { PersonMoneyRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';

const FEES = ['doctorfees.rules.manage', 'doctorfees.payouts.manage'] as const;

const manifest: ModuleManifest = {
  id: 'doctor-fees',
  title: 'Doctor fees',
  section: 'finance',
  icon: PersonMoneyRegular,
  order: 30,
  links: [
    {
      label: 'Doctor fees',
      to: '/doctor-fees',
      permission: FEES,
      keywords: ['doctor share', 'revenue share', 'professional fees'],
    },
    {
      label: 'Sharing rules',
      to: '/doctor-fees/rules',
      permission: FEES,
      keywords: ['share rule', 'percentage', 'doctor share'],
    },
    {
      label: 'Fee accruals',
      to: '/doctor-fees/accruals',
      permission: 'doctorfees.payouts.manage',
      keywords: ['accrual', 'earned fees'],
    },
    {
      label: 'Doctor payouts',
      to: '/doctor-fees/payouts',
      permission: 'doctorfees.payouts.manage',
      keywords: ['payout', 'tds', '194j', 'doctor payment'],
    },
    {
      label: 'Fee statements',
      to: '/doctor-fees/statements',
      permission: 'doctorfees.payouts.manage',
      keywords: ['statement', 'doctor statement'],
    },
  ],
};

export default manifest;
