import { Caption1 } from '@fluentui/react-components';
import { PersonHeartRegular } from '@fluentui/react-icons';
import { Page, PageHeader, Section } from '@hc360/ui';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { CRM_PERMISSIONS as C } from '@/modules/crm/labels';
import { Stack } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { PatientPicker } from '@/modules/patients/PatientPicker';

export const Route = createFileRoute('/_app/crm/patients/')({
  component: () => (
    <RequirePermission permission={C.patient360Read} what="the Patient 360 view">
      <Patient360Search />
    </RequirePermission>
  ),
});

function Patient360Search() {
  const navigate = useNavigate();
  return (
    <Page maxWidth={760}>
      <PageHeader
        title="Patient 360"
        subtitle="One view of a patient's relationship with the hospital: visits, spend, feedback, complaints, follow-up calls and communication consent."
        icon={<PersonHeartRegular />}
        breadcrumbs={[{ label: 'Patient relations', onClick: () => void navigate({ to: '/crm' }) }]}
      />
      <Section>
        <Stack gap={12}>
          <PatientPicker
            value={null}
            onChange={(patient) => patient && void navigate({ to: '/crm/patients/$patientId', params: { patientId: patient.id } })}
            label="Find the patient"
            hint="Type at least 3 characters of the UHID, mobile number or name."
          />
          <Caption1>Opening a patient's 360 view is recorded in the audit log. Restricted (VIP) records open only with emergency access.</Caption1>
        </Stack>
      </Section>
    </Page>
  );
}
