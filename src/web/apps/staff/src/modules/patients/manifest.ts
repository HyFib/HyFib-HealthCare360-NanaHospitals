import { PersonAddRegular, PersonSearchRegular, PeopleRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';

const manifest: ModuleManifest = {
  id: 'patients',
  title: 'Patients',
  section: 'patients',
  icon: PeopleRegular,
  order: 10,
  links: [
    {
      label: 'Find patient',
      to: '/patients',
      permission: 'patients.read',
      keywords: ['search', 'uhid', 'mobile', 'mrn'],
    },
    {
      label: 'Register patient',
      to: '/patients/register',
      permission: 'patients.register',
      keywords: ['new', 'registration', 'uhid'],
    },
  ],
  quickActions: [
    {
      label: 'Register patient',
      to: '/patients/register',
      permission: 'patients.register',
      icon: PersonAddRegular,
      description: 'New UHID with duplicate check',
    },
    {
      label: 'Find patient',
      to: '/patients',
      permission: 'patients.read',
      icon: PersonSearchRegular,
      description: 'By UHID, mobile or name',
    },
  ],
};

export default manifest;
