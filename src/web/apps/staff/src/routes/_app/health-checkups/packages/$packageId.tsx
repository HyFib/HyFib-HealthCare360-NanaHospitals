import { ErrorState, LoadingState, Page, PageHeader } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { checkupQueries } from '@/modules/health-checkups/api';
import { PackageForm } from '@/modules/health-checkups/PackageForm';

export const Route = createFileRoute('/_app/health-checkups/packages/$packageId')({
  component: PackageEditorPage,
});

/** Create ("new") or edit a health package. */
function PackageEditorPage() {
  const { packageId } = Route.useParams();
  const navigate = useNavigate();
  const isNew = packageId === 'new';
  const pkg = useQuery({ ...checkupQueries.package(packageId), enabled: !isNew });
  const toList = () => void navigate({ to: '/health-checkups/packages' });

  if (!isNew && pkg.isLoading) {
    return <LoadingState />;
  }
  if (!isNew && (pkg.error || !pkg.data)) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={pkg.error} onRetry={() => void pkg.refetch()} />
      </Page>
    );
  }

  return (
    <Page maxWidth={1200}>
      <PageHeader
        title={isNew ? 'New health package' : (pkg.data?.name ?? 'Health package')}
        subtitle={isNew ? 'Code, price, who it is for, and the tests and consultations it includes.' : pkg.data?.code}
        breadcrumbs={[
          { label: 'Health packages', onClick: toList },
          { label: isNew ? 'New' : (pkg.data?.code ?? 'Package') },
        ]}
      />
      <PackageForm
        key={isNew ? 'new' : packageId}
        pkg={isNew ? null : (pkg.data ?? null)}
        onSaved={(saved) => {
          if (isNew) {
            void navigate({
              to: '/health-checkups/packages/$packageId',
              params: { packageId: saved.id },
              replace: true,
            });
          }
        }}
      />
    </Page>
  );
}
