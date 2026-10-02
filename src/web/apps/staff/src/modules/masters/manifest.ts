import { BuildingMultipleRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { MASTERS_MANAGE } from './api';

/** Hospital setup (HMS-03, HMS-04): the organisation masters, how the hospital's numbers and documents read, and which features are on. */
const manifest: ModuleManifest = {
  id: 'masters',
  title: 'Hospital setup',
  section: 'admin',
  icon: BuildingMultipleRegular,
  order: 840,
  links: [
    {
      label: 'Facilities and letterhead',
      to: '/setup/facilities',
      permission: MASTERS_MANAGE,
      keywords: ['hospital', 'branch', 'facility', 'letterhead', 'logo', 'gstin', 'pan', 'nabh', 'address', 'hfr'],
    },
    {
      label: 'Departments',
      to: '/setup/departments',
      permission: MASTERS_MANAGE,
      keywords: ['department', 'opd', 'clinic', 'token prefix', 'queue', 'head of department'],
    },
    {
      label: 'Specialities',
      to: '/setup/specialties',
      permission: MASTERS_MANAGE,
      keywords: ['speciality', 'specialty', 'cardiology', 'orthopaedics', 'medicine'],
    },
    {
      label: 'Doctors and clinical staff',
      to: '/setup/practitioners',
      permission: MASTERS_MANAGE,
      keywords: [
        'doctor',
        'consultant',
        'practitioner',
        'nurse',
        'signature',
        'consultation fee',
        'registration',
        'nmc',
      ],
    },
    {
      label: 'Services and prices',
      to: '/setup/services',
      permission: MASTERS_MANAGE,
      keywords: ['service', 'price', 'charge', 'rate', 'sac', 'gst', 'catalogue', 'procedure'],
    },
    {
      label: 'Number formats',
      to: '/setup/numbering',
      permission: 'admin.numbering.manage',
      keywords: [
        'numbering',
        'number series',
        'uhid',
        'bill number',
        'receipt number',
        'invoice number',
        'prefix',
        'format',
      ],
    },
    {
      label: 'Features',
      to: '/setup/features',
      permission: 'admin.settings.manage',
      keywords: ['feature', 'switch', 'turn off', 'patient portal', 'video consultation', 'telehealth'],
    },
  ],
};

export default manifest;
