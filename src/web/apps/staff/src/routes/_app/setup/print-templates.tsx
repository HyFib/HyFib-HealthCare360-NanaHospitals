import { Badge, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { PrintRegular } from '@fluentui/react-icons';
import { DataTable, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { NoAccess } from '@/modules/admin/NoAccess';
import {
  effectiveTemplate,
  PRINT_TEMPLATES_PERMISSION,
  printTemplateQueries,
  templateSummary,
  type PrintTemplateItem,
} from '@/modules/print-templates/api';
import { PrintTemplateDrawer } from '@/modules/print-templates/PrintTemplateEditor';

export const Route = createFileRoute('/_app/setup/print-templates')({
  component: PrintTemplatesPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  // The name and what it covers wrap rather than being cut off on narrow screens.
  name: { display: 'flex', flexDirection: 'column', minWidth: 0, whiteSpace: 'normal' },
});

/**
 * Print templates (Hospital setup, SRS HMS-04): how each kind of document prints. The letterhead or the hospital's
 * pre-printed stationery, the logo, a closing note and the paper, hospital-wide or for one facility. Documents printed
 * after a change follow it; their layout stays as it is.
 */
function PrintTemplatesPage() {
  const styles = useStyles();
  const allowed = usePermission(PRINT_TEMPLATES_PERMISSION);
  const list = useQuery({ ...printTemplateQueries.list(), enabled: allowed });
  const [editing, setEditing] = useState<string | null>(null);

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Print templates" />
        <NoAccess what="print templates" />
      </Page>
    );
  }

  const several = (list.data?.facilityCount ?? 0) > 1;
  const editingItem = editing ? list.data?.templates.find((t) => t.key === editing) : undefined;

  return (
    <Page>
      <PageHeader
        title="Print templates"
        icon={<PrintRegular />}
        subtitle="How each kind of document prints: the letterhead or your pre-printed stationery, the logo, a closing note and the paper. Documents printed after a change follow it."
      />
      <Section
        flush
        description={
          several
            ? `Templates as ${list.data?.facilityName ?? 'this facility'} prints them. A facility prints with the hospital-wide template unless it has its own.`
            : undefined
        }
      >
        <DataTable
          ariaLabel="Print templates"
          rows={list.data?.templates}
          loading={list.isLoading}
          error={list.error}
          onRetry={() => void list.refetch()}
          getRowId={(t) => t.key}
          onRowClick={(t) => setEditing(t.key)}
          columns={[
            {
              key: 'name',
              header: 'Documents',
              render: (t) => (
                <span className={styles.name}>
                  <Text weight="semibold">{t.name}</Text>
                  <Caption1 className={styles.muted}>{t.covers}</Caption1>
                </span>
              ),
            },
            { key: 'area', header: 'Area', width: 120, priority: 'low', render: (t) => t.area },
            {
              key: 'prints',
              header: 'Prints with',
              width: 280,
              render: (t) => templateSummary(effectiveTemplate(t), t),
            },
            ...(several
              ? [
                  {
                    key: 'scope',
                    header: 'Template',
                    width: 150,
                    render: (t: PrintTemplateItem) =>
                      t.facility ? (
                        <Badge appearance="outline" color="brand" size="small">
                          This facility's own
                        </Badge>
                      ) : (
                        'Hospital-wide'
                      ),
                  },
                ]
              : []),
          ]}
          emptyTitle="No print templates"
          emptyDescription="The server has no documents to set up."
        />
      </Section>
      {editingItem && list.data && (
        <PrintTemplateDrawer item={editingItem} list={list.data} onClose={() => setEditing(null)} />
      )}
    </Page>
  );
}
