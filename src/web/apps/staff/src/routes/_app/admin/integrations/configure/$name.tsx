import { Button, Caption1, makeStyles, tokens } from '@fluentui/react-components';
import { SendRegular } from '@fluentui/react-icons';
import { formatRelative } from '@hc360/core';
import { EmptyState, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { healthDisplay, INTEGRATION_LABELS, integrationQueries, isIntegrationName, providerLabel } from '@/modules/admin/integrationsApi';
import { NoAccess } from '@/modules/admin/NoAccess';
import { SettingsForm } from '@/modules/admin/SettingsForm';
import { INTEGRATION_SETTINGS } from '@/modules/admin/settingsForms';
import { TestIntegrationDialog } from '@/modules/admin/TestIntegrationDialog';

export const Route = createFileRoute('/_app/admin/integrations/configure/$name')({
  component: ConfigureIntegrationPage,
});

const useStyles = makeStyles({
  status: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Settings for one integration: provider, addresses, sender ids and credentials (write-only), then a test. */
function ConfigureIntegrationPage() {
  const styles = useStyles();
  const { name } = Route.useParams();
  const navigate = useNavigate();
  const allowed = usePermission('admin.integrations.manage');
  const status = useQuery({ ...integrationQueries.status(), enabled: allowed });
  const [testing, setTesting] = useState(false);
  const known = isIntegrationName(name) ? name : null;
  const spec = known ? INTEGRATION_SETTINGS[known] : undefined;
  const title = known ? INTEGRATION_LABELS[known] : 'Integration';
  const row = status.data?.find((item) => item.name === name);
  const breadcrumbs = [{ label: 'Integrations', onClick: () => void navigate({ to: '/admin/integrations' }) }, { label: title }];

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Integrations" />
        <NoAccess what="integrations" />
      </Page>
    );
  }

  if (!known) {
    return (
      <Page>
        <PageHeader title="Integration not found" breadcrumbs={breadcrumbs} />
        <EmptyState
          title="There is no such integration"
          description="Go back to Integrations and choose one from the list."
          action={<Button onClick={() => void navigate({ to: '/admin/integrations' })}>Back to integrations</Button>}
        />
      </Page>
    );
  }

  const health = row ? healthDisplay(row.health) : null;

  return (
    <Page maxWidth={1040}>
      <PageHeader
        title={`${title} settings`}
        breadcrumbs={breadcrumbs}
        subtitle={
          row && health ? (
            <span className={styles.status}>
              <StatusBadge status={row.health} tone={health.tone} label={health.label} />
              <span>{providerLabel(row.provider)}</span>
              {row.lastSuccessAt && <span className={styles.muted}>Last worked {formatRelative(row.lastSuccessAt)}</span>}
            </span>
          ) : undefined
        }
        actions={
          <Button icon={<SendRegular />} onClick={() => setTesting(true)}>
            Send a test
          </Button>
        }
      />

      {spec ? (
        <Section title="Settings" description={spec.intro}>
          <SettingsForm spec={spec} />
        </Section>
      ) : (
        <Section>
          <EmptyState
            title={`${title} need no settings`}
            description="Labels are built on the server as ZPL or TSPL and printed by HC360 Desktop on each workstation’s label printer. Send a test to get a sample label."
          />
        </Section>
      )}

      <Caption1 className={styles.muted}>
        After saving, send a test to check the connection. Every setting is also listed under <Link to="/admin/settings">Administration → Settings</Link>.
      </Caption1>

      {testing && <TestIntegrationDialog name={known} displayName={row?.displayName ?? title} onClose={() => setTesting(false)} />}
    </Page>
  );
}
