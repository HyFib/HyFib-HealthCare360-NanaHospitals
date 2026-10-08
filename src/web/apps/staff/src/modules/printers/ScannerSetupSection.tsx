import {
  Button,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
} from '@fluentui/react-components';
import { ArrowSyncRegular, BarcodeScannerRegular, SaveRegular } from '@fluentui/react-icons';
import { BridgeError, formatDateTime } from '@hc360/core';
import { FormGrid, LoadingState, Section, SelectField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { PrinterChangeNotRecordedError, printerKeys, printerQueries, scannersApi } from './api';
import {
  BAUD_OPTIONS,
  DATA_BIT_OPTIONS,
  NO_SCANNER,
  PARITY_OPTIONS,
  portOptions,
  STOP_BIT_OPTIONS,
  statusIntent,
  toScanner,
  toScannerForm,
  type ScannerForm,
} from './scanner';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  footer: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
});

const DESCRIPTION =
  'A scanner that types like a keyboard needs nothing here. For a scanner on a COM port (RS-232, or USB set to serial mode), choose the port and set it as the scanner is set: HC360 types each scan into the field you are in and presses Enter.';

/** The serial barcode scanner of this computer (INT-BAR), below its printers. */
export function ScannerSetupSection({
  computer,
  deviceId,
  mayChange,
}: {
  computer: string;
  deviceId: string | null;
  /** The user may set up printers (and so the scanner). */
  mayChange: boolean;
}) {
  const classes = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const scanner = useQuery(printerQueries.scanner());

  const save = useMutation({
    mutationFn: (values: ScannerForm) => scannersApi.save(toScanner(values), computer, deviceId),
    onSuccess: (saved) => {
      notify.success(
        'Scanner saved',
        saved.port ? `HC360 now listens to the scanner on ${saved.port}.` : 'HC360 no longer listens to a COM port.',
      );
      void queryClient.invalidateQueries({ queryKey: printerKeys.scanner });
    },
    onError: (error) => {
      if (error instanceof PrinterChangeNotRecordedError) {
        notify.warning('Scanner saved, change not recorded', error.message);
        void queryClient.invalidateQueries({ queryKey: printerKeys.scanner });
        return;
      }
      notify.error(error, 'The scanner was not saved');
    },
  });

  if (scanner.isLoading) {
    return (
      <Section title="Barcode scanner" description={DESCRIPTION}>
        <LoadingState label="Reading the scanner of this computer…" />
      </Section>
    );
  }

  if (!scanner.data) {
    const outdated = scanner.error instanceof BridgeError && scanner.error.code === 'method_not_found';
    return (
      <Section title="Barcode scanner" description={DESCRIPTION}>
        <MessageBar intent="warning">
          <MessageBarBody>
            {outdated
              ? 'This HC360 Desktop cannot read scanners on a COM port. Install the latest HC360 Desktop on this computer (Install-HC360Desktop.ps1). Scanners that type like a keyboard work as they are.'
              : 'Could not read the scanner of this computer. Scanners that type like a keyboard work as they are.'}
          </MessageBarBody>
        </MessageBar>
      </Section>
    );
  }

  const info = scanner.data;
  return (
    <Section
      title="Barcode scanner"
      description={DESCRIPTION}
      actions={
        <Button
          icon={<ArrowSyncRegular />}
          disabled={scanner.isFetching}
          onClick={() => void queryClient.invalidateQueries({ queryKey: printerKeys.scanner })}
        >
          Check again
        </Button>
      }
    >
      <div className={classes.body}>
        <MessageBar intent={statusIntent(info.status)}>
          <MessageBarBody>
            {info.status.message}
            {info.status.lastScanAt && ` Last scan ${formatDateTime(info.status.lastScanAt)}.`}
            {info.status.lastProblem && <div>{info.status.lastProblem}</div>}
          </MessageBarBody>
        </MessageBar>
        {info.problems.length > 0 && (
          <MessageBar intent="warning">
            <MessageBarBody>
              <MessageBarTitle>The scanner is not used</MessageBarTitle>
              {info.problems.map((problem) => (
                <div key={problem}>{problem}</div>
              ))}
            </MessageBarBody>
          </MessageBar>
        )}
        {info.lockedByPolicy && (
          <MessageBar intent="info">
            <MessageBarBody>
              IT set the scanner of this computer in the HC360 Desktop policy. To change it, ask IT.
            </MessageBarBody>
          </MessageBar>
        )}
        <ScannerFields
          key={info.setup.updatedAt ?? 'not saved'}
          info={info}
          canEdit={mayChange && !info.lockedByPolicy}
          saving={save.isPending}
          onSave={(values) => save.mutate(values)}
        />
        {info.setup.updatedAt && (
          <span>
            Saved {info.setup.updatedBy ? `by ${info.setup.updatedBy} ` : ''}on {formatDateTime(info.setup.updatedAt)}.
          </span>
        )}
        <TryScan />
      </div>
    </Section>
  );
}

function ScannerFields({
  info,
  canEdit,
  saving,
  onSave,
}: {
  info: Parameters<typeof portOptions>[0];
  canEdit: boolean;
  saving: boolean;
  onSave: (values: ScannerForm) => void;
}) {
  const classes = useStyles();
  const form = useForm<ScannerForm>({ defaultValues: toScannerForm(info.setup) });
  const port = useWatch({ control: form.control, name: 'port' });
  const serial = port !== NO_SCANNER;
  return (
    <form className={classes.body} onSubmit={form.handleSubmit(onSave)} noValidate>
      <FormGrid columns={3} minColumnWidth={160}>
        <SelectField
          control={form.control}
          name="port"
          label="COM port"
          hint={info.ports.length === 0 ? 'This computer has no COM ports now.' : undefined}
          options={portOptions(info)}
          disabled={!canEdit}
        />
        {serial && (
          <>
            <SelectField
              control={form.control}
              name="baudRate"
              label="Speed"
              options={BAUD_OPTIONS}
              disabled={!canEdit}
            />
            <SelectField
              control={form.control}
              name="dataBits"
              label="Data bits"
              options={DATA_BIT_OPTIONS}
              disabled={!canEdit}
            />
            <SelectField
              control={form.control}
              name="parity"
              label="Parity"
              options={PARITY_OPTIONS}
              disabled={!canEdit}
            />
            <SelectField
              control={form.control}
              name="stopBits"
              label="Stop bits"
              options={STOP_BIT_OPTIONS}
              disabled={!canEdit}
            />
          </>
        )}
      </FormGrid>
      {canEdit && (
        <div className={classes.footer}>
          <Button
            type="submit"
            appearance="primary"
            icon={<SaveRegular />}
            disabled={saving || !form.formState.isDirty}
          >
            {saving ? 'Saving…' : 'Save scanner'}
          </Button>
        </div>
      )}
    </form>
  );
}

/** A field to scan into, to see that scans arrive (from either kind of scanner) and what they read. */
function TryScan() {
  const [text, setText] = useState('');
  const [last, setLast] = useState<string | null>(null);
  return (
    <Field
      label="Try a scan"
      hint={last ? `Received: ${last}` : 'Click here and scan a barcode: what HC360 receives is shown below the field.'}
    >
      <Input
        contentBefore={<BarcodeScannerRegular aria-hidden />}
        value={text}
        autoComplete="off"
        placeholder="Scan here"
        onChange={(_, d) => setText(d.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault();
            setLast(text.trim() || null);
            setText('');
          }
        }}
      />
    </Field>
  );
}
