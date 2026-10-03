import { MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { PrintRegular } from '@fluentui/react-icons';
import {
  BridgeError,
  formatDateTime,
  isDesktop,
  PRINTER_ROLE_LABEL,
  useSession,
  type PrinterRole,
  type RolePrinter,
} from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { printerKeys, printerQueries, printersApi } from '@/modules/printers/api';
import { PRINTERS_SETUP, toSetup, type PrinterSetupForm } from '@/modules/printers/form';
import { PrinterSetupForm as PrinterSetupFields } from '@/modules/printers/PrinterSetupForm';

export const Route = createFileRoute('/_app/workstation/printers')({
  component: PrintersPage,
});

const TITLE = 'Printers on this computer';

/**
 * Printers on this computer (INT-BAR): which printer this workstation uses for labels, wristbands, 80 mm slips and A4/A5
 * documents. HC360 Desktop keeps them for the computer; staff allowed to set up printers change them, and anyone may
 * see them and print a test.
 */
function PrintersPage() {
  if (!isDesktop) {
    return (
      <Page maxWidth={900}>
        <PageHeader title={TITLE} />
        <EmptyState
          icon={<PrintRegular />}
          title="Printers are set up in HC360 Desktop"
          description="Open HealthCare360 from HC360 Desktop on the computer the printers are connected to. In a browser, HC360 shows the print dialog for every print."
        />
      </Page>
    );
  }
  return <DesktopPrinters />;
}

function DesktopPrinters() {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const userName = useSession((s) => s.user?.displayName ?? null);
  const mayChange = usePermission(PRINTERS_SETUP);
  const setup = useQuery(printerQueries.setup());
  const installed = useQuery(printerQueries.installed());
  const device = useQuery(printerQueries.device());
  const computer = device.data?.deviceName || device.data?.machineName || setup.data?.computerName || 'this computer';

  const save = useMutation({
    mutationFn: (values: PrinterSetupForm) =>
      printersApi.save(toSetup(values, userName), computer, device.data?.deviceId ?? null),
    onSuccess: () => {
      notify.success('Printers saved', 'Prints from this computer now go to these printers.');
      void queryClient.invalidateQueries({ queryKey: printerKeys.setup });
    },
    onError: (error) => notify.error(error, 'The printers were not saved'),
  });

  const test = useMutation({
    mutationFn: ({ role, printer }: { role: PrinterRole; printer: RolePrinter }) =>
      printersApi.test(role, printer, computer),
    onSuccess: (_, { role, printer }) =>
      notify.success(
        'Test sent',
        `Check the test ${role === 'documents' ? 'page' : 'print'} on ${printer.printer} (${PRINTER_ROLE_LABEL[role].toLowerCase()}).`,
      ),
    onError: (error) => notify.error(error, 'The test did not print'),
  });

  const header = (
    <PageHeader
      title={TITLE}
      subtitle={`Choose the printer for each kind of print on ${computer}. HC360 prints straight to it; a kind with no printer asks with the print dialog.`}
    />
  );

  if (setup.isLoading || installed.isLoading) {
    return (
      <Page maxWidth={900}>
        {header}
        <LoadingState label="Reading the printers of this computer…" />
      </Page>
    );
  }

  if (setup.error instanceof BridgeError && setup.error.code === 'method_not_found') {
    return (
      <Page maxWidth={900}>
        {header}
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>This HC360 Desktop cannot keep printers</MessageBarTitle>
            Install the latest HC360 Desktop on this computer (Install-HC360Desktop.ps1). Until then, prints go where
            they always went.
          </MessageBarBody>
        </MessageBar>
      </Page>
    );
  }

  if (setup.error || installed.error || !setup.data || !installed.data) {
    return (
      <Page maxWidth={900}>
        {header}
        <ErrorState
          error={setup.error ?? installed.error}
          title="Could not read the printers of this computer"
          onRetry={() => void queryClient.invalidateQueries({ queryKey: printerKeys.all })}
        />
      </Page>
    );
  }

  const info = setup.data;
  const canEdit = mayChange && !info.lockedByPolicy;
  return (
    <Page maxWidth={900}>
      {header}
      {info.lockedByPolicy ? (
        <MessageBar intent="info">
          <MessageBarBody>
            IT set the printers of this computer in the HC360 Desktop policy. To change them, ask IT. You can still
            print a test.
          </MessageBarBody>
        </MessageBar>
      ) : (
        !mayChange && (
          <MessageBar intent="info">
            <MessageBarBody>
              You can see the printers and print a test. To change them, ask an administrator: it needs the permission
              to set up printers.
            </MessageBarBody>
          </MessageBar>
        )
      )}
      {info.setup.updatedAt && (
        <MessageBar intent="success" layout="singleline">
          <MessageBarBody>
            Saved {info.setup.updatedBy ? `by ${info.setup.updatedBy} ` : ''}on {formatDateTime(info.setup.updatedAt)}.
          </MessageBarBody>
        </MessageBar>
      )}
      {info.problems && info.problems.length > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Some printers are not used</MessageBarTitle>
            {info.problems.map((problem) => (
              <div key={problem}>{problem}</div>
            ))}
            Those prints show the print dialog until the printer is set again
            {info.lockedByPolicy ? ' in the HC360 Desktop policy (ask IT).' : '.'}
          </MessageBarBody>
        </MessageBar>
      )}
      {installed.data.length === 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Windows has no printers on this computer. Install the printers first (Windows Settings › Printers and
            scanners), then choose them here.
          </MessageBarBody>
        </MessageBar>
      )}
      <PrinterSetupFields
        key={info.setup.updatedAt ?? 'not saved'}
        info={info}
        installed={installed.data}
        canEdit={canEdit}
        saving={save.isPending}
        testing={test.isPending ? (test.variables?.role ?? null) : null}
        onSave={(values) => save.mutate(values)}
        onTest={(role, printer) => test.mutate({ role, printer })}
      />
    </Page>
  );
}
