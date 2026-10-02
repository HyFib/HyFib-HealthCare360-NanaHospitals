import { Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { HOSPITAL_TIME_ZONE, useSession } from '@hc360/core';
import { EmptyState, Page, PageHeader } from '@hc360/ui';
import { createFileRoute, Link } from '@tanstack/react-router';
import { Suspense, useMemo } from 'react';
import { useFeatureSwitches } from '@/lib/features';
import { moduleManifests } from '@/lib/moduleRegistry';
import { visibleModules } from '@/lib/modules';
import { ErrorBoundary } from '@/shell/ErrorBoundary';

export const Route = createFileRoute('/_app/')({
  component: Dashboard,
});

const useStyles = makeStyles({
  actions: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
  action: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '12px',
    padding: '14px 16px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow4,
    textDecoration: 'none',
    color: tokens.colorNeutralForeground1,
    transitionProperty: 'box-shadow, transform',
    transitionDuration: tokens.durationFaster,
    ':hover': { boxShadow: tokens.shadow8, transform: 'translateY(-1px)' },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  actionIcon: { color: tokens.colorBrandForeground1, fontSize: '24px', display: 'flex', flexShrink: 0 },
  actionText: { display: 'flex', flexDirection: 'column', gap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
  widgets: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    '@media (max-width: 1280px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
    '@media (max-width: 720px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  span1: { gridColumn: 'span 1' },
  span2: { gridColumn: 'span 2', '@media (max-width: 720px)': { gridColumn: 'span 1' } },
  span4: { gridColumn: '1 / -1' },
});

function greeting(): string {
  const hour = Number(
    new Intl.DateTimeFormat('en-IN', { hour: 'numeric', hour12: false, timeZone: HOSPITAL_TIME_ZONE }).format(
      new Date(),
    ),
  );
  return hour < 12 ? 'Good morning' : hour < 17 ? 'Good afternoon' : 'Good evening';
}

/** Role-aware home: quick actions and live widgets contributed by every module the user can use. */
function Dashboard() {
  const styles = useStyles();
  const user = useSession((s) => s.user);
  const features = useFeatureSwitches();
  // Quick actions and widgets come filtered by permission and by the feature switches.
  const modules = useMemo(() => visibleModules(user, moduleManifests, features), [user, features]);

  const quickActions = modules.flatMap((m) => m.quickActions ?? []);
  const widgets = modules.flatMap((m) => m.widgets ?? []).sort((a, b) => (a.order ?? 100) - (b.order ?? 100));

  const firstName = user?.displayName.replace(/^(Dr\.?|Mr\.?|Ms\.?|Mrs\.?)\s+/i, '').split(' ')[0] ?? '';
  const today = new Intl.DateTimeFormat('en-IN', {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
    year: 'numeric',
    timeZone: HOSPITAL_TIME_ZONE,
  }).format(new Date());

  return (
    <Page>
      <PageHeader
        title={`${greeting()}, ${firstName}`}
        subtitle={`${today}${user?.facilityName ? ` · ${user.facilityName}` : ''}`}
      />

      {quickActions.length > 0 && (
        <nav aria-label="Quick actions" className={styles.actions}>
          {quickActions.map((action) => {
            const Icon = action.icon;
            return (
              <Link key={action.to + action.label} to={action.to} className={styles.action}>
                <span className={styles.actionIcon}>
                  <Icon />
                </span>
                <span className={styles.actionText}>
                  <Text weight="semibold">{action.label}</Text>
                  {action.description && <Caption1 className={styles.muted}>{action.description}</Caption1>}
                </span>
              </Link>
            );
          })}
        </nav>
      )}

      {widgets.length > 0 ? (
        <div className={styles.widgets}>
          {widgets.map((widget) => {
            const Widget = widget.component;
            const span = widget.span === 4 ? styles.span4 : widget.span === 2 ? styles.span2 : styles.span1;
            return (
              <div key={widget.id} className={span}>
                <ErrorBoundary>
                  <Suspense fallback={null}>
                    <Widget />
                  </Suspense>
                </ErrorBoundary>
              </div>
            );
          })}
        </div>
      ) : (
        quickActions.length === 0 && (
          <EmptyState
            title="Welcome to HealthCare360"
            description="Use the navigation on the left, or press Ctrl+K to find a patient or a screen."
          />
        )
      )}
    </Page>
  );
}
