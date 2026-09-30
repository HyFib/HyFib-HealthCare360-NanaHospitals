import {
  Button,
  Caption1,
  Field,
  makeStyles,
  Select,
  Switch,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { DataTable, EmptyState, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { searchFlag, searchOneOf } from '@/modules/lab/common/search';
import {
  MODALITIES,
  RADIOLOGY_PERMISSIONS,
  radiologyQueries,
  type ImagingProcedureDto,
  type Modality,
  type ReportTemplateDto,
} from '@/modules/radiology/api';
import { ModalityBadge } from '@/modules/radiology/badges';
import { modalityLabel } from '@/modules/radiology/labels';
import { ProcedureDrawer } from '@/modules/radiology/setup/ProcedureDrawer';
import { TemplateDrawer } from '@/modules/radiology/setup/TemplateDrawer';

const TABS = ['procedures', 'templates'] as const;
type SetupTab = (typeof TABS)[number];

interface SetupSearch {
  tab?: SetupTab;
  modality?: Modality;
  inactive?: true;
}

export const Route = createFileRoute('/_app/radiology/setup')({
  validateSearch: (s: Record<string, unknown>): SetupSearch => ({
    tab: searchOneOf(s.tab, TABS),
    modality: searchOneOf(s.modality, MODALITIES),
    inactive: searchFlag(s.inactive),
  }),
  component: RadiologySetupPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  row: { display: 'flex', gap: '6px', alignItems: 'center' },
});

/** Imaging masters (SRS IMG-01/03): the procedure catalogue and the report templates radiologists start from. */
function RadiologySetupPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canVerify = usePermission(RADIOLOGY_PERMISSIONS.verify);
  const canEditProcedures = usePermission([RADIOLOGY_PERMISSIONS.verify, RADIOLOGY_PERMISSIONS.mastersManage]);
  const canSeeTemplates = usePermission([RADIOLOGY_PERMISSIONS.write, RADIOLOGY_PERMISSIONS.verify]);
  const tab = search.tab ?? 'procedures';
  const [procedure, setProcedure] = useState<ImagingProcedureDto | 'new' | null>(null);
  const [template, setTemplate] = useState<ReportTemplateDto | 'new' | null>(null);
  const includeInactive = Boolean(search.inactive);
  const procedures = useQuery({
    ...radiologyQueries.procedures(search.modality, includeInactive),
    enabled: tab === 'procedures',
  });
  const templates = useQuery({
    ...radiologyQueries.templates(search.modality, null, includeInactive),
    enabled: tab === 'templates' && canSeeTemplates,
  });
  const set = (patch: Partial<SetupSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }) });

  return (
    <Page>
      <PageHeader
        title="Procedures and templates"
        subtitle="What can be ordered in imaging, and the normal-study wording radiologists start from"
        actions={
          tab === 'procedures'
            ? canEditProcedures && (
                <Button appearance="primary" icon={<AddRegular />} onClick={() => setProcedure('new')}>
                  Add procedure
                </Button>
              )
            : canVerify && (
                <Button appearance="primary" icon={<AddRegular />} onClick={() => setTemplate('new')}>
                  Add template
                </Button>
              )
        }
      />
      <TabList
        selectedValue={tab}
        onTabSelect={(_, data) => set({ tab: data.value === 'procedures' ? undefined : (data.value as SetupTab) })}
      >
        <Tab value="procedures">Procedures</Tab>
        {canSeeTemplates && <Tab value="templates">Report templates</Tab>}
      </TabList>
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Modality">
              <Select
                value={search.modality ?? ''}
                onChange={(_, data) => set({ modality: (data.value || undefined) as Modality | undefined })}
              >
                <option value="">All</option>
                {MODALITIES.map((value) => (
                  <option key={value} value={value}>
                    {modalityLabel[value]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={includeInactive}
              onChange={(_, data) => set({ inactive: data.checked ? true : undefined })}
              label="Show those not in use"
            />
          </FilterBar>
        </div>
        {tab === 'procedures' ? (
          <DataTable
            ariaLabel="Imaging procedures"
            rows={procedures.data}
            loading={procedures.isLoading}
            error={procedures.error}
            onRetry={() => void procedures.refetch()}
            getRowId={(item) => item.id}
            onRowClick={canEditProcedures ? (item) => setProcedure(item) : undefined}
            size="small"
            columns={[
              {
                key: 'name',
                header: 'Procedure',
                render: (item: ImagingProcedureDto) => (
                  <span className={styles.cell}>
                    <span className={styles.row}>
                      <ModalityBadge modality={item.modality} />
                      <Text weight="semibold">{item.name}</Text>
                    </span>
                    <Caption1 className={styles.muted}>
                      {item.code} · {item.bodyPart}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'slot',
                header: 'Slot',
                width: 90,
                render: (item: ImagingProcedureDto) => `${item.durationMinutes} min`,
              },
              {
                key: 'contrast',
                header: 'Contrast',
                width: 100,
                priority: 'low',
                render: (item: ImagingProcedureDto) => (item.requiresContrast ? 'Usually' : 'No'),
              },
              {
                key: 'prep',
                header: 'Preparation',
                priority: 'low',
                render: (item: ImagingProcedureDto) => item.patientPreparation ?? '—',
              },
              {
                key: 'status',
                header: 'Status',
                width: 110,
                render: (item: ImagingProcedureDto) => (
                  <StatusBadge
                    status={item.isActive ? 'Active' : 'Inactive'}
                    label={item.isActive ? 'In use' : 'Not in use'}
                  />
                ),
              },
            ]}
            emptyTitle="No procedures"
            emptyDescription="Add each imaging procedure the hospital offers, linked to its billable service."
          />
        ) : !canSeeTemplates ? (
          <EmptyState
            title="Templates are for radiologists"
            description="You need reporting rights to see report templates."
          />
        ) : (
          <DataTable
            ariaLabel="Report templates"
            rows={templates.data}
            loading={templates.isLoading}
            error={templates.error}
            onRetry={() => void templates.refetch()}
            getRowId={(item) => item.id}
            onRowClick={canVerify ? (item) => setTemplate(item) : undefined}
            size="small"
            columns={[
              {
                key: 'name',
                header: 'Template',
                render: (item: ReportTemplateDto) => (
                  <span className={styles.cell}>
                    <span className={styles.row}>
                      <ModalityBadge modality={item.modality} />
                      <Text weight="semibold">{item.name}</Text>
                    </span>
                    <Caption1 className={styles.muted}>
                      {item.code}
                      {item.bodyPart ? ` · ${item.bodyPart}` : ' · any body part'}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'impression',
                header: 'Impression',
                priority: 'low',
                render: (item: ReportTemplateDto) => item.impression,
              },
              {
                key: 'status',
                header: 'Status',
                width: 110,
                render: (item: ReportTemplateDto) => (
                  <StatusBadge
                    status={item.isActive ? 'Active' : 'Inactive'}
                    label={item.isActive ? 'In use' : 'Not in use'}
                  />
                ),
              },
            ]}
            emptyTitle="No templates"
            emptyDescription="Add templates with the usual wording of a normal study for each procedure."
          />
        )}
      </Section>
      {procedure && (
        <ProcedureDrawer
          key={procedure === 'new' ? 'new' : procedure.id}
          procedure={procedure === 'new' ? null : procedure}
          onClose={() => setProcedure(null)}
        />
      )}
      {template && (
        <TemplateDrawer
          key={template === 'new' ? 'new' : template.id}
          template={template === 'new' ? null : template}
          onClose={() => setTemplate(null)}
        />
      )}
    </Page>
  );
}
