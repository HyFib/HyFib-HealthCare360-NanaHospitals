import { ShieldCheckmarkRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { ClaimsPendingWidget } from './widgets';

const DESK = [
  'insurance.preauth.manage',
  'insurance.claims.manage',
  'insurance.policies.manage',
  'insurance.payers.manage',
] as const;

const manifest: ModuleManifest = {
  id: 'insurance',
  title: 'Insurance',
  section: 'finance',
  icon: ShieldCheckmarkRegular,
  order: 20,
  links: [
    {
      label: 'Insurance desk',
      to: '/insurance',
      permission: DESK,
      keywords: ['tpa', 'cashless', 'insurance dashboard'],
    },
    {
      label: 'Pre-authorisations',
      to: '/insurance/preauths',
      permission: ['insurance.preauth.manage', 'insurance.claims.manage', 'billing.ip.manage'],
      keywords: ['preauth', 'cashless', 'approval', 'enhancement', 'tpa query'],
    },
    {
      label: 'Claims',
      to: '/insurance/claims',
      permission: 'insurance.claims.manage',
      keywords: ['claim', 'settlement', 'utr', 'deduction', 'disallowance'],
    },
    {
      label: 'Patient policies',
      to: '/insurance/policies',
      permission: [
        'insurance.policies.manage',
        'insurance.preauth.manage',
        'insurance.claims.manage',
        'billing.ip.manage',
        'billing.op.create',
      ],
      keywords: ['policy', 'insurance card', 'mediclaim', 'sum insured'],
    },
    {
      label: 'Payers',
      to: '/insurance/payers',
      permission: [
        'insurance.payers.manage',
        'insurance.policies.manage',
        'insurance.preauth.manage',
        'insurance.claims.manage',
        'billing.op.create',
        'billing.ip.manage',
        'billing.charges.read',
      ],
      keywords: ['insurer', 'tpa', 'corporate', 'scheme', 'cghs', 'pmjay', 'non-payable'],
    },
    {
      label: 'Receivables ageing',
      to: '/insurance/ageing',
      permission: 'insurance.claims.manage',
      keywords: ['ageing', 'outstanding', 'receivable', 'insurer dues'],
    },
  ],
  widgets: [
    {
      id: 'insurance-claims-pending',
      permission: 'insurance.claims.manage',
      span: 2,
      order: 50,
      component: ClaimsPendingWidget,
    },
  ],
};

export default manifest;
