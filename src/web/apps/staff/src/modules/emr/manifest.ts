import { StethoscopeRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { ClinicalTasksWidget, ResultsToReviewWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'emr',
  title: "Doctor's desk",
  section: 'clinical',
  icon: StethoscopeRegular,
  order: 10,
  links: [
    {
      label: 'My worklist',
      to: '/emr',
      permission: 'clinical.emr.read',
      keywords: ['emr', 'ehr', 'doctor', 'op', 'consultation', 'patients', 'results', 'chart', 'prescription'],
    },
    {
      label: 'Controlled drug register',
      to: '/emr/controlled-drugs',
      permission: ['clinical.prescriptions.sign', 'mrd.records'],
      keywords: ['schedule h1', 'schedule x', 'narcotic', 'psychotropic', 'register'],
    },
    {
      label: 'Drug safety tables',
      to: '/emr/drug-safety',
      permission: 'clinical.templates.manage',
      keywords: ['interactions', 'dose limits', 'maximum dose', 'prescribing checks'],
    },
    {
      label: 'Clinical note templates',
      to: '/emr/templates',
      permission: 'clinical.templates.manage',
      keywords: ['template', 'speciality', 'note', 'opd', 'complaints', 'default medicines'],
    },
    {
      label: 'Order sets',
      to: '/emr/order-sets',
      permission: 'clinical.templates.manage',
      keywords: ['order set', 'panel', 'work-up', 'investigations', 'bundle'],
    },
  ],
  quickActions: [
    {
      label: 'My patients',
      to: '/emr',
      permission: 'clinical.emr.read',
      icon: StethoscopeRegular,
      description: "Today's queue, results and unsigned work",
    },
  ],
  widgets: [
    {
      id: 'emr-results-to-review',
      permission: 'clinical.emr.write',
      span: 2,
      order: 15,
      component: ResultsToReviewWidget,
    },
    { id: 'emr-clinical-tasks', permission: 'clinical.emr.write', span: 2, order: 16, component: ClinicalTasksWidget },
  ],
};

export default manifest;
