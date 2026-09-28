import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Badge,
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  SearchBox,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DismissRegular, GlobeSearchRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, useDebouncedValue } from '@hc360/core';
import { applyServerErrors, DataTable, ErrorState, FilterBar, LoadingState, Page, PageHeader, Section, SelectField, StatusBadge, SwitchField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { ICD10_RELEASE, ICD11_RELEASE, mrdApi, mrdQueries, type IcdCodeDto, type OnlineIcdResult } from '@/modules/mrd/api';

interface IcdSearch {
  search?: string;
  chapter?: string;
  billable?: boolean;
}

export const Route = createFileRoute('/_app/mrd/icd')({
  validateSearch: (s: Record<string, unknown>): IcdSearch => ({
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    chapter: typeof s.chapter === 'string' && /^[IVXLC]+$/.test(s.chapter) ? s.chapter : undefined,
    billable: s.billable === true || s.billable === 'true' ? true : undefined,
  }),
  component: IcdBrowserPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(260px, 340px) minmax(0, 1fr)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1000px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  chapters: { maxHeight: 'calc(100vh - 220px)', overflowY: 'auto' },
  block: { display: 'block', width: '100%', textAlign: 'start', justifyContent: 'flex-start' },
  code: { fontFamily: tokens.fontFamilyMonospace, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

const icdSchema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter the code.')
    .max(16)
    .regex(/^[A-Za-z0-9][A-Za-z0-9.&/]*$/, 'Use letters, digits and dots.'),
  title: z.string().trim().min(1, 'Enter the title.').max(300),
  release: z.string(),
  isBillable: z.boolean(),
  keywords: z.string().max(300),
  isActive: z.boolean(),
});

type IcdValues = z.infer<typeof icdSchema>;

function IcdDrawer({ code, onClose }: { code: IcdCodeDto | 'new' | null; onClose: () => void }) {
  return (
    <OverlayDrawer open={code !== null} position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      {code !== null && <IcdForm key={code === 'new' ? 'new' : code.id} code={code === 'new' ? null : code} onClose={onClose} />}
    </OverlayDrawer>
  );
}

function IcdForm({ code, onClose }: { code: IcdCodeDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<IcdValues>({
    resolver: zodResolver(icdSchema),
    defaultValues: {
      code: code?.code ?? '',
      title: code?.title ?? '',
      release: code?.release ?? ICD10_RELEASE,
      isBillable: code?.isBillable ?? true,
      keywords: code?.keywords ?? '',
      isActive: code?.isActive ?? true,
    },
  });
  const save = useMutation({
    mutationFn: (v: IcdValues) =>
      mrdApi.saveIcd(code?.id ?? null, {
        code: v.code.trim(),
        title: v.title.trim(),
        release: v.release,
        isBillable: v.isBillable,
        keywords: v.keywords.trim() || null,
        fromOnlineLookup: false,
        isActive: v.isActive,
      }),
    onSuccess: (saved) => {
      notify.success(`${saved.code} saved`);
      void queryClient.invalidateQueries({ queryKey: ['mrd', 'icd'] });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'icd.exists') {
        form.setError('code', { message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the code');
      }
    },
  });
  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>{code ? code.code : 'Add ICD code'}</DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          <TextField control={form.control} name="code" label="Code" required autoFocus maxLength={16} placeholder="e.g. A90" disabled={Boolean(code)} />
          <TextField control={form.control} name="title" label="Title" required maxLength={300} />
          <SelectField
            control={form.control}
            name="release"
            label="Classification"
            options={[
              { value: ICD10_RELEASE, label: 'ICD-10 (WHO 2019)' },
              { value: ICD11_RELEASE, label: 'ICD-11 MMS' },
            ]}
          />
          <TextField control={form.control} name="keywords" label="Search words" maxLength={300} hint="Shorthand and synonyms doctors type, e.g. T2DM, sugar, DM2" />
          <SwitchField control={form.control} name="isBillable" label="Valid for coding and claims (most specific code)" />
          <SwitchField control={form.control} name="isActive" label="Active (offered in searches)" />
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button type="submit" appearance="primary" disabled={save.isPending} icon={save.isPending ? <Spinner size="tiny" /> : undefined}>
          Save
        </Button>
        <Button onClick={onClose}>Cancel</Button>
      </DrawerFooter>
    </form>
  );
}

function OnlineResults({ term, canAdd }: { term: string; canAdd: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const online = useQuery(mrdQueries.icdOnline(term));
  const add = useMutation({
    mutationFn: (result: OnlineIcdResult) =>
      mrdApi.saveIcd(null, { code: result.code, title: result.title, release: result.release, isBillable: result.isLeaf, keywords: null, fromOnlineLookup: true, isActive: true }),
    onSuccess: (saved) => {
      notify.success(`${saved.code} added to the local catalogue`, 'Doctors and coders can now find it offline.');
      void queryClient.invalidateQueries({ queryKey: ['mrd'] });
    },
    onError: (error) => notify.error(error, 'Could not add the code'),
  });
  if (online.isLoading) {
    return <LoadingState label="Asking the WHO ICD service…" />;
  }
  if (online.error) {
    return isApiError(online.error) && (online.error.code === 'icd.online_unavailable' || online.error.code === 'icd.online_failed') ? (
      <MessageBar intent="warning">
        <MessageBarBody>{online.error.userMessage}</MessageBarBody>
      </MessageBar>
    ) : (
      <ErrorState error={online.error} onRetry={() => void online.refetch()} />
    );
  }
  return (
    <DataTable
      ariaLabel="WHO ICD results"
      rows={online.data}
      getRowId={(r) => `${r.release}-${r.code}`}
      size="small"
      columns={[
        { key: 'code', header: 'Code', width: 110, render: (r) => <span className={styles.code}>{r.code}</span> },
        { key: 'title', header: 'Title', render: (r) => r.title },
        { key: 'release', header: 'Classification', priority: 'low', render: (r) => r.release },
        { key: 'leaf', header: 'Codable', width: 90, render: (r) => (r.isLeaf ? 'Yes' : 'Category') },
      ]}
      rowActions={(r) =>
        r.isCachedLocally ? (
          <Badge appearance="tint" color="success">
            In catalogue
          </Badge>
        ) : canAdd ? (
          <Button size="small" icon={<AddRegular />} disabled={add.isPending} onClick={() => add.mutate(r)}>
            Add
          </Button>
        ) : null
      }
      emptyTitle="The WHO service found nothing"
    />
  );
}

/** ICD-10 browser (MRD-01): chapters and blocks, ranked search including clinical shorthand, and the local catalogue. */
function IcdBrowserPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canCode = usePermission('mrd.coding');
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 250);
  const [onlineTerm, setOnlineTerm] = useState<string | null>(null);
  const [editing, setEditing] = useState<IcdCodeDto | 'new' | null>(null);
  const classification = useQuery(mrdQueries.classification());
  const results = useQuery(mrdQueries.icd(search.search ?? '', search.chapter ?? null, search.billable ?? false, 100));

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined }), replace: true });
    }
  }, [debounced, search.search, navigate]);

  const chapter = classification.data?.find((c) => c.number === search.chapter);

  return (
    <Page>
      <PageHeader
        title="ICD-10 browser"
        subtitle="Search by code (E11.9 or e119), words or shorthand (T2DM, HTN, AGE). The same ranking is used in the doctor's diagnosis search."
        actions={
          canCode && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add code
            </Button>
          )
        }
      />
      <div className={styles.layout}>
        <Section title="Chapters" flush>
          {classification.isLoading ? (
            <LoadingState />
          ) : (
            <div className={styles.chapters}>
              <Accordion collapsible openItems={search.chapter ? [search.chapter] : []} onToggle={(_, d) => void navigate({ search: (prev) => ({ ...prev, chapter: d.openItems.length > 0 ? String(d.value) : undefined }), replace: true })}>
                {(classification.data ?? []).map((c) => (
                  <AccordionItem key={c.number} value={c.number}>
                    <AccordionHeader size="small">
                      <span>
                        <span className={styles.code}>{c.number}</span> {c.title} <Caption1 className={styles.muted}>({c.range})</Caption1>
                      </span>
                    </AccordionHeader>
                    <AccordionPanel>
                      {c.blocks.map((b) => (
                        <Button
                          key={b.range}
                          size="small"
                          appearance="subtle"
                          className={styles.block}
                          onClick={() => {
                            const start = b.range.split('-')[0] ?? b.range;
                            setTerm(start);
                          }}
                        >
                          <span className={styles.code}>{b.range}</span> {b.title}
                        </Button>
                      ))}
                    </AccordionPanel>
                  </AccordionItem>
                ))}
              </Accordion>
            </div>
          )}
        </Section>

        <Section flush>
          <div style={{ padding: '12px 16px' }}>
            <FilterBar>
              <Field label="Search" style={{ flex: '1 1 280px' }}>
                <SearchBox autoFocus value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Code, words or shorthand" />
              </Field>
              <Switch checked={search.billable ?? false} onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, billable: d.checked || undefined }), replace: true })} label="Codable only" />
              <Button icon={<GlobeSearchRegular />} disabled={term.trim().length < 2} onClick={() => setOnlineTerm(term.trim())}>
                Search WHO online
              </Button>
            </FilterBar>
            {chapter && (
              <Caption1 className={styles.muted}>
                Chapter {chapter.number}: {chapter.title}
              </Caption1>
            )}
          </div>
          <DataTable
            ariaLabel="ICD-10 codes"
            rows={results.data}
            loading={results.isLoading}
            error={results.error}
            onRetry={() => void results.refetch()}
            getRowId={(c) => c.id}
            onRowClick={canCode ? (c) => setEditing(c) : undefined}
            size="small"
            columns={[
              { key: 'code', header: 'Code', width: 100, render: (c) => <span className={styles.code}>{c.code}</span> },
              {
                key: 'title',
                header: 'Title',
                render: (c) => (
                  <span>
                    <Text>{c.title}</Text>
                    {c.keywords && <Caption1 block className={styles.muted}>{c.keywords}</Caption1>}
                  </span>
                ),
              },
              { key: 'block', header: 'Block', priority: 'low', render: (c) => (c.blockTitle ? `${c.block ?? ''} ${c.blockTitle}` : (c.block ?? '—')) },
              {
                key: 'billable',
                header: 'Codable',
                width: 100,
                render: (c) =>
                  c.isBillable ? (
                    'Yes'
                  ) : (
                    <Badge size="small" appearance="outline" color="warning">
                      Category
                    </Badge>
                  ),
              },
              { key: 'source', header: 'Source', width: 100, priority: 'low', render: (c) => <StatusBadge status={c.source} tone="informative" label={c.source === 'Seed' ? 'WHO list' : c.source === 'Online' ? 'WHO online' : 'Added here'} /> },
            ]}
            emptyTitle={search.search ? 'No local code matches' : 'Type to search the catalogue'}
            emptyDescription={search.search ? 'Try other words or the code, or search the WHO service online.' : undefined}
          />
        </Section>
      </div>

      <OverlayDrawer open={onlineTerm !== null} position="end" size="large" onOpenChange={(_, d) => !d.open && setOnlineTerm(null)}>
        <DrawerHeader>
          <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={() => setOnlineTerm(null)} />}>
            WHO ICD online: “{onlineTerm}”
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>{onlineTerm && <OnlineResults term={onlineTerm} canAdd={canCode} />}</DrawerBody>
      </OverlayDrawer>
      <IcdDrawer code={editing} onClose={() => setEditing(null)} />
    </Page>
  );
}
