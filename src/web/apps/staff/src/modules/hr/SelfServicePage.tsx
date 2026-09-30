import { PersonQuestionMarkRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { hrQueries, type MyProfileDto } from './api';
import { HR_PERMISSIONS } from './labels';
import { SELF_NAV, TEAM_NAV_ITEM } from './nav';
import { RequirePermission } from './shared/NoAccess';
import { SubNav } from './shared/SubNav';

/**
 * Frame of every "My HR" screen: the header, the tabs (with "My team" for reporting managers) and a friendly message
 * when the sign-in is not linked to an employee record.
 */
export function SelfServicePage({
  title,
  subtitle,
  icon,
  actions,
  maxWidth,
  children,
}: {
  title: string;
  subtitle?: ReactNode;
  icon?: ReactNode;
  actions?: ReactNode;
  maxWidth?: number;
  children: (profile: MyProfileDto) => ReactNode;
}) {
  return (
    <RequirePermission permission={HR_PERMISSIONS.self} what="HR self-service">
      <Frame title={title} subtitle={subtitle} icon={icon} actions={actions} maxWidth={maxWidth}>
        {children}
      </Frame>
    </RequirePermission>
  );
}

function Frame({
  title,
  subtitle,
  icon,
  actions,
  maxWidth,
  children,
}: {
  title: string;
  subtitle?: ReactNode;
  icon?: ReactNode;
  actions?: ReactNode;
  maxWidth?: number;
  children: (profile: MyProfileDto) => ReactNode;
}) {
  const profile = useQuery({ ...hrQueries.myProfile(), retry: false });
  const notLinked =
    isApiError(profile.error) &&
    (profile.error.code === 'hr.self.not_linked' || profile.error.code === 'hr.self.no_user');
  const items = profile.data && profile.data.reportees > 0 ? [...SELF_NAV, TEAM_NAV_ITEM] : SELF_NAV;

  return (
    <Page maxWidth={maxWidth}>
      <PageHeader title={title} subtitle={subtitle} icon={icon} actions={profile.data ? actions : undefined} />
      {profile.data && <SubNav items={items} ariaLabel="My HR screens" />}
      {profile.isLoading && <LoadingState />}
      {notLinked && (
        <Section>
          <EmptyState
            icon={<PersonQuestionMarkRegular />}
            title="Your sign-in is not linked to an employee record"
            description="Ask HR to link your HealthCare360 sign-in on your employee record. Then your leave, attendance, roster and payslips appear here."
          />
        </Section>
      )}
      {profile.error && !notLinked && <ErrorState error={profile.error} onRetry={() => void profile.refetch()} />}
      {profile.data && children(profile.data)}
    </Page>
  );
}
