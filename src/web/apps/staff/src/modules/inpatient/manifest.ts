import { BedRegular, PersonAddRegular, PillRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { PendingDischargesWidget, WardOccupancyWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'inpatient',
  title: 'In-patients',
  section: 'inpatient',
  icon: BedRegular,
  order: 10,
  links: [
    {
      label: 'Census',
      to: '/inpatient',
      permission: 'inpatient.admissions.read',
      keywords: ['occupancy', 'dashboard', 'ALOS', 'bed occupancy rate', 'midnight census'],
    },
    {
      label: 'Bed board',
      to: '/inpatient/beds',
      permission: 'inpatient.admissions.read',
      keywords: ['ward', 'beds', 'vacant', 'occupancy', 'reserve', 'block'],
    },
    {
      label: 'Admit patient',
      to: '/inpatient/admit',
      permission: 'inpatient.admissions.admit',
      keywords: ['IP', 'admission', 'ADT', 'wristband'],
    },
    {
      label: 'Admissions',
      to: '/inpatient/admissions',
      permission: 'inpatient.admissions.read',
      keywords: ['IP list', 'discharge', 'transfer', 'in-patients'],
    },
    {
      label: 'Nursing station',
      to: '/nursing',
      permission: ['nursing.chart', 'nursing.mar'],
      keywords: [
        'MAR',
        'medication round',
        'handover',
        'SBAR',
        'intake output',
        'fluid balance',
        'fall risk',
        'Braden',
        'nursing notes',
      ],
    },
    {
      label: 'Housekeeping',
      to: '/inpatient/housekeeping',
      permission: ['beds.housekeeping', 'beds.manage'],
      keywords: ['cleaning', 'bed turnaround'],
    },
    {
      label: 'Wards and beds',
      to: '/inpatient/setup',
      permission: 'inpatient.masters.manage',
      keywords: ['masters', 'rooms', 'add beds', 'ward setup'],
    },
  ],
  quickActions: [
    {
      label: 'Admit patient',
      to: '/inpatient/admit',
      permission: 'inpatient.admissions.admit',
      icon: PersonAddRegular,
      description: 'Choose a bed and print the wristband',
    },
    {
      label: 'Medication round',
      to: '/nursing',
      permission: 'nursing.mar',
      icon: PillRegular,
      description: 'Doses due now on your ward',
    },
  ],
  widgets: [
    {
      id: 'inpatient-occupancy',
      permission: ['inpatient.admissions.read', 'beds.manage'],
      span: 2,
      order: 30,
      component: WardOccupancyWidget,
    },
    {
      id: 'inpatient-pending-discharges',
      permission: 'inpatient.admissions.read',
      span: 2,
      order: 31,
      component: PendingDischargesWidget,
    },
  ],
};

export default manifest;
