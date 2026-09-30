import { Button, type ButtonProps } from '@fluentui/react-components';
import { PersonHeartRegular } from '@fluentui/react-icons';
import { useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { CRM_PERMISSIONS } from './labels';

/**
 * "Patient 360" button for any patient screen (for example the patient profile header). It renders nothing for users
 * without `crm.patient360.read`, so other modules can drop it in without their own permission check.
 */
export function Patient360Link({
  patientId,
  appearance = 'secondary',
  size,
}: {
  patientId: string;
  appearance?: ButtonProps['appearance'];
  size?: ButtonProps['size'];
}) {
  const navigate = useNavigate();
  const allowed = usePermission(CRM_PERMISSIONS.patient360Read);
  if (!allowed) {
    return null;
  }
  return (
    <Button
      appearance={appearance}
      size={size}
      icon={<PersonHeartRegular />}
      onClick={() => void navigate({ to: '/crm/patients/$patientId', params: { patientId } })}
    >
      Patient 360
    </Button>
  );
}
