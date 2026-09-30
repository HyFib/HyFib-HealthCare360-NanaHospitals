import { createFileRoute, Navigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { NoAccess } from '@/modules/hr/shared/NoAccess';
import { PRIVACY_PERMISSIONS as P } from '@/modules/privacy/labels';

/** "/privacy" opens the first privacy screen the user may use. */
export const Route = createFileRoute('/_app/privacy/')({
  component: PrivacyHome,
});

function PrivacyHome() {
  const canRead = usePermission(P.requestsRead);
  const canLog = usePermission(P.requestsLog);
  const canBreaches = usePermission(P.breachesManage);
  const canRetention = usePermission(P.retentionManage);
  const canSettings = usePermission(P.settingsManage);
  if (canRead) {
    return <Navigate to="/privacy/requests" replace />;
  }
  if (canBreaches) {
    return <Navigate to="/privacy/breaches" replace />;
  }
  if (canLog) {
    return <Navigate to="/privacy/requests/new" replace />;
  }
  if (canRetention) {
    return <Navigate to="/privacy/retention" replace />;
  }
  if (canSettings) {
    return <Navigate to="/privacy/settings" replace />;
  }
  return <NoAccess what="privacy" />;
}
