import { Button, SearchBox, Switch } from '@fluentui/react-components';
import { ArrowUploadRegular, ShieldTaskRegular } from '@fluentui/react-icons';
import { useDebouncedValue } from '@hc360/core';
import { Page, PageHeader, Section } from '@hc360/ui';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { HbpProcedures, ImportHbpDialog } from '@/modules/billing/HbpMaster';
import { NoAccess } from '@/modules/billing/ui';

export const Route = createFileRoute('/_app/billing/hbp')({
  component: HbpPage,
});

/**
 * AB PM-JAY's Health Benefit Package master (INS-PMJAY): the scheme's procedures, imported from NHA's file, which
 * packages are mapped to so that claims to the scheme name its procedure codes.
 */
function HbpPage() {
  const canRead = usePermission(['billing.tariffs.manage', 'insurance.preauth.manage', 'insurance.claims.manage']);
  const canImport = usePermission('billing.tariffs.manage');
  const [search, setSearch] = useState('');
  const [includeRetired, setIncludeRetired] = useState(false);
  const [importing, setImporting] = useState(false);
  const term = useDebouncedValue(search.trim(), 300);

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="PM-JAY procedures" />
        <NoAccess what="the PM-JAY procedure master" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="PM-JAY procedures"
        subtitle="AB PM-JAY's Health Benefit Package master: the procedures and rates the scheme pays"
        icon={<ShieldTaskRegular />}
        actions={
          canImport && (
            <Button appearance="primary" icon={<ArrowUploadRegular />} onClick={() => setImporting(true)}>
              Import master
            </Button>
          )
        }
      />
      <Section
        flush
        title="Procedures"
        actions={
          <>
            <SearchBox
              aria-label="Search procedures"
              placeholder="Code, procedure, package or specialty"
              value={search}
              onChange={(_, d) => setSearch(d.value)}
            />
            <Switch checked={includeRetired} onChange={(_, d) => setIncludeRetired(d.checked)} label="Show retired" />
          </>
        }
      >
        <HbpProcedures search={term} includeRetired={includeRetired} />
      </Section>
      <ImportHbpDialog open={importing} onClose={() => setImporting(false)} />
    </Page>
  );
}
