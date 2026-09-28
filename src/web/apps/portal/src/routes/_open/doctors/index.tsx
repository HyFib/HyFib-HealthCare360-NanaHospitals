import { Button, Field, makeStyles, SearchBox, Select, Switch, tokens } from '@fluentui/react-components';
import { DismissRegular, StethoscopeRegular } from '@fluentui/react-icons';
import { useDebouncedValue } from '@hc360/core';
import { EmptyState } from '@hc360/ui';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { useT } from '@/i18n';
import { useNow } from '@/lib/hooks';
import { flagParam, guidParam, textParam } from '@/lib/ids';
import { doctorQueries, type DoctorSearch } from '@/modules/doctors/api';
import { DoctorCard } from '@/modules/doctors/DoctorCard';

export const Route = createFileRoute('/_open/doctors/')({
  validateSearch: (search: Record<string, unknown>): DoctorSearch => ({
    q: textParam(search.q),
    specialtyId: guidParam(search.specialtyId),
    facilityId: guidParam(search.facilityId),
    language: textParam(search.language, 40),
    video: flagParam(search.video),
  }),
  component: FindDoctorPage,
});

const useStyles = makeStyles({
  controls: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  filters: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 200px), 1fr))', gap: '12px' },
  row: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: '8px' },
  results: { display: 'flex', flexDirection: 'column', gap: '12px' },
  count: { margin: 0, color: tokens.colorNeutralForeground2 },
  more: { alignSelf: 'center', minWidth: '200px' },
});

function FindDoctorPage() {
  const styles = useStyles();
  const t = useT();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 350);
  const now = useNow(60_000);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const filters = useQuery(doctorQueries.filters());
  const doctors = useInfiniteQuery(doctorQueries.search(search));
  const rows = doctors.data?.pages.flatMap((page) => page.items) ?? [];
  const total = doctors.data?.pages[0]?.totalCount ?? 0;
  const hasFilters = Boolean(search.q || search.specialtyId || search.facilityId || search.language || search.video);

  const setFilter = (patch: Partial<DoctorSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  return (
    <PortalPage title={t('doctors.title')} subtitle={t('doctors.subtitle')}>
      <section className={styles.controls} aria-label={t('doctors.filters')}>
        <Field label={t('doctors.search.label')}>
          <SearchBox
            size="large"
            value={term}
            onChange={(_, data) => setTerm(data.value)}
            placeholder={t('doctors.search.placeholder')}
            autoComplete="off"
            enterKeyHint="search"
          />
        </Field>
        <div className={styles.filters}>
          <Field label={t('doctors.filter.speciality')}>
            <Select size="large" value={search.specialtyId ?? ''} onChange={(_, data) => setFilter({ specialtyId: data.value || undefined })}>
              <option value="">{t('doctors.filter.anySpeciality')}</option>
              {filters.data?.specialties.map((option) => (
                <option key={option.id} value={option.id}>
                  {option.name} ({option.doctors})
                </option>
              ))}
            </Select>
          </Field>
          <Field label={t('doctors.filter.language')}>
            <Select size="large" value={search.language ?? ''} onChange={(_, data) => setFilter({ language: data.value || undefined })}>
              <option value="">{t('doctors.filter.anyLanguage')}</option>
              {filters.data?.languages.map((language) => (
                <option key={language} value={language}>
                  {language}
                </option>
              ))}
            </Select>
          </Field>
          {(filters.data?.facilities.length ?? 0) > 1 && (
            <Field label={t('doctors.filter.branch')}>
              <Select size="large" value={search.facilityId ?? ''} onChange={(_, data) => setFilter({ facilityId: data.value || undefined })}>
                <option value="">{t('doctors.filter.anyBranch')}</option>
                {filters.data?.facilities.map((facility) => (
                  <option key={facility.id} value={facility.id}>
                    {facility.city ? `${facility.name}, ${facility.city}` : facility.name}
                  </option>
                ))}
              </Select>
            </Field>
          )}
        </div>
        <div className={styles.row}>
          <Switch checked={Boolean(search.video)} label={t('doctors.filter.video')} onChange={(_, data) => setFilter({ video: data.checked || undefined })} />
          {hasFilters && (
            <Button
              appearance="subtle"
              icon={<DismissRegular />}
              onClick={() => {
                setTerm('');
                void navigate({ search: {}, replace: true });
              }}
            >
              {t('doctors.filter.clear')}
            </Button>
          )}
        </div>
      </section>

      <section className={styles.results} aria-busy={doctors.isFetching}>
        <p className={styles.count} role="status" aria-live="polite">
          {doctors.isSuccess ? t('doctors.results', { count: total }) : ''}
        </p>
        {doctors.isPending ? (
          <LoadingCards count={4} />
        ) : doctors.isError ? (
          <ErrorBlock error={doctors.error} onRetry={() => void doctors.refetch()} />
        ) : rows.length === 0 ? (
          <EmptyState icon={<StethoscopeRegular />} title={t('doctors.empty.title')} description={t('doctors.empty.body')} />
        ) : (
          <>
            {rows.map((doctor) => (
              <DoctorCard key={doctor.id} doctor={doctor} video={Boolean(search.video)} now={now} />
            ))}
            {doctors.hasNextPage && (
              <Button className={styles.more} size="large" onClick={() => void doctors.fetchNextPage()} disabled={doctors.isFetchingNextPage}>
                {t('common.showMore')}
              </Button>
            )}
          </>
        )}
      </section>
    </PortalPage>
  );
}
