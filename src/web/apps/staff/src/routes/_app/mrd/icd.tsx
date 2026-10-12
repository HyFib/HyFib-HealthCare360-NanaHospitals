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
  Select,
  Spinner,
  Switch,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { AddRegular, ArrowUploadRegular, DismissRegular, GlobeSearchRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, useDebouncedValue } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  DataTable,
  ErrorState,
  FilterBar,
  LoadingState,
  Page,
  PageHeader,
  Section,
  SelectField,
  StatusBadge,
  SwitchField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import {
  ICD10_RELEASE,
  ICD11_RELEASE,
  mrdApi,
  mrdKeys,
  mrdQueries,
  type IcdCodeDto,
  type OnlineIcdResult,
} from '@/modules/mrd/api';
import { CatalogueImportPanel, CatalogueStatusList } from '@/modules/mrd/CatalogueImport';
import { sourceLabel, usageHint, usageMark } from '@/modules/mrd/catalogue';

interface IcdSearch {
  search?: string;
  chapter?: string;
  billable?: boolean;
  /** ICD-11 codes saved from the WHO online search; ICD-10 when absent. */
  release?: 'icd11';
}

export const Route = createFileRoute('/_app/mrd/icd')({
  validateSearch: (s: Record<string, unknown>): IcdSearch => ({
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    chapter: typeof s.chapter === 'string' && /^[IVXLC]+$/.test(s.chapter) ? s.chapter : undefined,
    billable: s.billable === true || s.billable === 'true' ? true : undefined,
    release: s.release === 'icd11' ? 'icd11' : undefined,
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
  single: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr)', gap: '16px' },
  chapters: { maxHeight: 'calc(100vh - 220px)', overflowY: 'auto' },
  block: { display: 'block', width: '100%', textAlign: 'start', justifyContent: 'flex-start' },
  code: { fontFamily: tokens.fontFamilyMonospace, fontWeight: tokens.fontWeightSemibold },
  mark: { fontFamily: tokens.fontFamilyMonospace, color: tokens.colorPaletteMarigoldForeground2, cursor: 'help' },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  status: { display: 'flex', flexDirection: 'column', gap: '12px' },
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
  const closing = useCloseRequests({ onClose });
  return (
    <OverlayDrawer open={code !== null} position="end" size="medium" onOpenChange={closing.onOpenChange}>
      {code !== null && (
        <CloseRequestScope value={closing}>
          <IcdForm key={code === 'new' ? 'new' : code.id} code={code === 'new' ? null : code} onClose={onClose} />
        </CloseRequestScope>
      )}
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
      void queryClient.invalidateQueries({ queryKey: mrdKeys.all });
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
  const fromFile = code?.source === 'Import' || code?.source === 'Seed';
  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
          }
        >
          {code ? code.code : 'Add ICD code'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          {fromFile && (
            <MessageBar intent="info">
              <MessageBarBody>
                This code comes from {code?.source === 'Seed' ? 'the starter list' : 'the WHO file'}. Search words and
                the Active switch can be changed freely. If you change the title or whether it is codable, it becomes
                this hospital&rsquo;s own code and later imports leave it as it is.
              </MessageBarBody>
            </MessageBar>
          )}
          <TextField
            control={form.control}
            name="code"
            label="Code"
            required
            autoFocus
            maxLength={16}
            placeholder="e.g. A90"
            disabled={Boolean(code)}
          />
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
          <TextField
            control={form.control}
            name="keywords"
            label="Search words"
            maxLength={300}
            hint="Shorthand and synonyms doctors type, e.g. T2DM, sugar, DM2"
          />
          {code?.includedTerms && (
            <Field label="Includes (from the WHO file)">
              <Text>{code.includedTerms}</Text>
            </Field>
          )}
          <SwitchField
            control={form.control}
            name="isBillable"
            label="Valid for coding and claims (most specific code)"
          />
          <SwitchField control={form.control} name="isActive" label="Active (offered in searches)" />
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button
          type="submit"
          appearance="primary"
          disabled={save.isPending}
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
        >
          Save
        </Button>
        <Button onClick={guard.requestClose}>Cancel</Button>
      </DrawerFooter>
      {guard.dialogs}
    </form>
  );
}

/** A code with its dagger (†) or asterisk (*) mark, which explains itself on hover. */
function CodeWithMark({ code }: { code: IcdCodeDto }) {
  const styles = useStyles();
  const mark = usageMark(code.usage);
  const hint = usageHint(code.usage);
  return (
    <span>
      <span className={styles.code}>{code.code}</span>
      {mark && hint && (
        <Tooltip content={hint} relationship="description">
          <span className={styles.mark} aria-label={code.usage === 'Dagger' ? 'dagger code' : 'asterisk code'}>
            {mark}
          </span>
        </Tooltip>
      )}
    </span>
  );
}

function OnlineResults({ term, release, canAdd }: { term: string; release: string; canAdd: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const online = useQuery(mrdQueries.icdOnline(term, release));
  const add = useMutation({
    mutationFn: (result: OnlineIcdResult) =>
      mrdApi.saveIcd(null, {
        code: result.code,
        title: result.title,
        release: result.release,
        isBillable: result.isLeaf,
        keywords: null,
        fromOnlineLookup: true,
        isActive: true,
      }),
    onSuccess: (saved) => {
      notify.success(`${saved.code} added to the local catalogue`, 'Doctors and coders can now find it offline.');
      void queryClient.invalidateQueries({ queryKey: mrdKeys.all });
    },
    onError: (error) => notify.error(error, 'Could not add the code'),
  });
  if (online.isLoading) {
    return <LoadingState label="Asking the WHO ICD service…" />;
  }
  if (online.error) {
    return isApiError(online.error) && (online.error.isValidation || online.error.status === 422) ? (
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

/** ICD-10 looks a code up (E11 and its subdivisions); words go to ICD-11, which WHO's service can search. */
function onlineReleaseFor(term: string): string {
  return /^[A-Za-z]\d{2}(\.?\w{0,2})?$/.test(term.trim()) ? ICD10_RELEASE : ICD11_RELEASE;
}

/**
 * The ICD catalogue (MRD-01, INT-ICD): its state, chapters and blocks, ranked search including clinical shorthand, the
 * WHO online search, and for administrators the import of the hospital's WHO ICD-10 file or the CMS ICD-10-PCS file.
 */
function IcdBrowserPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const canCode = usePermission('mrd.coding');
  const canRead = usePermission('clinical.emr.read');
  const canImport = usePermission('mrd.catalogue.import');
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 250);
  const [online, setOnline] = useState<{ term: string; release: string } | null>(null);
  const [editing, setEditing] = useState<IcdCodeDto | 'new' | null>(null);
  const [importing, setImporting] = useState(false);
  const release = search.release === 'icd11' ? ICD11_RELEASE : ICD10_RELEASE;
  const icd10 = release === ICD10_RELEASE;
  const status = useQuery(mrdQueries.catalogue());
  const classification = useQuery({ ...mrdQueries.classification(), enabled: icd10 });
  const results = useQuery(
    mrdQueries.icd(
      search.search ?? '',
      icd10 ? (search.chapter ?? null) : null,
      search.billable ?? false,
      100,
      release,
    ),
  );

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined }), replace: true });
    }
  }, [debounced, search.search, navigate]);

  const chapter = icd10 ? classification.data?.find((c) => c.number === search.chapter) : undefined;

  const table = (
    <Section flush>
      <div style={{ padding: '12px 16px' }}>
        <FilterBar>
          <Field label="Search" style={{ flex: '1 1 280px' }}>
            <SearchBox
              autoFocus
              value={term}
              onChange={(_, d) => setTerm(d.value)}
              placeholder="Code, words or shorthand"
            />
          </Field>
          <Field label="Classification">
            <Select
              value={search.release ?? 'icd10'}
              onChange={(_, d) =>
                void navigate({
                  search: (prev) => ({
                    ...prev,
                    release: d.value === 'icd11' ? 'icd11' : undefined,
                    chapter: d.value === 'icd11' ? undefined : prev.chapter,
                  }),
                  replace: true,
                })
              }
            >
              <option value="icd10">ICD-10 (WHO 2019)</option>
              <option value="icd11">ICD-11 MMS, saved from WHO online</option>
            </Select>
          </Field>
          <Switch
            checked={search.billable ?? false}
            onChange={(_, d) =>
              void navigate({ search: (prev) => ({ ...prev, billable: d.checked || undefined }), replace: true })
            }
            label="Codable only"
          />
          {(canCode || canRead) && (
            <Button
              icon={<GlobeSearchRegular />}
              disabled={term.trim().length < 2}
              onClick={() => setOnline({ term: term.trim(), release: onlineReleaseFor(term) })}
            >
              Search WHO online
            </Button>
          )}
        </FilterBar>
        {chapter && (
          <Caption1 className={styles.muted}>
            Chapter {chapter.number}: {chapter.title}
          </Caption1>
        )}
      </div>
      <DataTable
        ariaLabel={icd10 ? 'ICD-10 codes' : 'ICD-11 codes'}
        rows={results.data}
        loading={results.isLoading}
        error={results.error}
        onRetry={() => void results.refetch()}
        getRowId={(c) => c.id}
        onRowClick={canCode ? (c) => setEditing(c) : undefined}
        size="small"
        columns={[
          { key: 'code', header: 'Code', width: 110, render: (c) => <CodeWithMark code={c} /> },
          {
            key: 'title',
            header: 'Title',
            render: (c) => (
              <span>
                <Text>{c.title}</Text>
                {c.keywords && (
                  <Caption1 block className={styles.muted}>
                    {c.keywords}
                  </Caption1>
                )}
                {c.includedTerms && (
                  <Caption1 block className={styles.muted}>
                    Includes: {c.includedTerms}
                  </Caption1>
                )}
              </span>
            ),
          },
          {
            key: 'block',
            header: 'Block',
            priority: 'low',
            render: (c) => (c.blockTitle ? `${c.block ?? ''} ${c.blockTitle}` : (c.block ?? '—')),
          },
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
          {
            key: 'source',
            header: 'Source',
            width: 110,
            priority: 'low',
            render: (c) => <StatusBadge status={c.source} tone="informative" label={sourceLabel(c.source)} />,
          },
        ]}
        emptyTitle={search.search ? 'No local code matches' : 'Type to search the catalogue'}
        emptyDescription={search.search ? 'Try other words or the code, or search the WHO service online.' : undefined}
      />
    </Section>
  );

  return (
    <Page>
      <PageHeader
        title="ICD catalogue"
        subtitle="Search by code (E11.9 or e119), words or shorthand (T2DM, HTN, AGE). The same ranking is used in the doctor's diagnosis search."
        actions={
          <>
            {canImport && (
              <Button icon={<ArrowUploadRegular />} onClick={() => setImporting(true)}>
                Import catalogue
              </Button>
            )}
            {canCode && (
              <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
                Add code
              </Button>
            )}
          </>
        }
      />
      <Section
        title="Catalogues"
        description="ICD-10 codes, titles and terms: © World Health Organization, from the hospital's own copy of the WHO file. ICD-10-PCS: CMS, public domain."
      >
        <div className={styles.status}>
          {status.isLoading ? (
            <LoadingState />
          ) : status.error ? (
            <ErrorState error={status.error} onRetry={() => void status.refetch()} />
          ) : status.data ? (
            <CatalogueStatusList status={status.data} />
          ) : null}
        </div>
      </Section>
      {icd10 ? (
        <div className={styles.layout}>
          <Section title="Chapters" flush>
            {classification.isLoading ? (
              <LoadingState />
            ) : (
              <div className={styles.chapters}>
                <Accordion
                  collapsible
                  openItems={search.chapter ? [search.chapter] : []}
                  onToggle={(_, d) =>
                    void navigate({
                      search: (prev) => ({ ...prev, chapter: d.openItems.length > 0 ? String(d.value) : undefined }),
                      replace: true,
                    })
                  }
                >
                  {(classification.data ?? []).map((c) => (
                    <AccordionItem key={c.number} value={c.number}>
                      <AccordionHeader size="small">
                        <span>
                          <span className={styles.code}>{c.number}</span> {c.title}{' '}
                          <Caption1 className={styles.muted}>({c.range})</Caption1>
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
          {table}
        </div>
      ) : (
        <div className={styles.single}>{table}</div>
      )}

      <OverlayDrawer
        open={online !== null}
        position="end"
        size="large"
        onOpenChange={(_, d) => !d.open && setOnline(null)}
      >
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button
                appearance="subtle"
                icon={<DismissRegular />}
                aria-label="Close"
                onClick={() => setOnline(null)}
              />
            }
          >
            WHO ICD online: “{online?.term}”
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          {online && (
            <div className={styles.form}>
              <Field label="Search">
                <Select value={online.release} onChange={(_, d) => setOnline({ ...online, release: d.value })}>
                  <option value={ICD11_RELEASE}>ICD-11 MMS by words</option>
                  <option value={ICD10_RELEASE}>ICD-10 by code (for example E11)</option>
                </Select>
              </Field>
              <OnlineResults term={online.term} release={online.release} canAdd={canCode} />
            </div>
          )}
        </DrawerBody>
      </OverlayDrawer>

      <OverlayDrawer
        open={importing}
        position="end"
        size="medium"
        onOpenChange={(_, d) => !d.open && setImporting(false)}
      >
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button
                appearance="subtle"
                icon={<DismissRegular />}
                aria-label="Close"
                onClick={() => setImporting(false)}
              />
            }
          >
            Import catalogue
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          {importing && (
            <CatalogueImportPanel
              run={mrdApi.importCatalogue}
              onImported={() => void queryClient.invalidateQueries({ queryKey: mrdKeys.all })}
            />
          )}
        </DrawerBody>
      </OverlayDrawer>
      <IcdDrawer code={editing} onClose={() => setEditing(null)} />
    </Page>
  );
}
