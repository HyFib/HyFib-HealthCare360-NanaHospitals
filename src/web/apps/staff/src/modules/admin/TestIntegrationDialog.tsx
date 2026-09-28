import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { humanize, isApiError } from '@hc360/core';
import { applyServerErrors, FormGrid, TextAreaField, TextField } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useForm } from 'react-hook-form';
import {
  EMPTY_TEST_FORM,
  integrationKeys,
  runIntegrationTest,
  TEST_DESCRIPTIONS,
  testFormProblems,
  testFormSchema,
  testInputFor,
  toTestRequest,
  type IntegrationName,
  type IntegrationTestResult,
  type TestFormValues,
} from './integrationsApi';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  details: { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '4px 16px' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
  label: {
    fontFamily: tokens.fontFamilyMonospace,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-all',
    backgroundColor: tokens.colorNeutralBackground3,
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusMedium,
    margin: 0,
    maxHeight: '200px',
    overflow: 'auto',
  },
});

const DETAIL_LABELS: Record<string, string> = { providerMessageId: 'Message id at the provider', orderId: 'Order id', provider: 'Provider' };

/**
 * "Send a test" (POST /api/v1/admin/integrations/{name}/test): says what the test will do, asks for a recipient where
 * a real message is sent, then shows the result. Every test is audited on the server.
 */
export function TestIntegrationDialog({ name, displayName, onClose }: { name: IntegrationName; displayName: string; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const input = testInputFor(name);
  const schema = useMemo(
    () =>
      testFormSchema.superRefine((values, ctx) => {
        for (const [path, message] of Object.entries(testFormProblems(name, values))) {
          ctx.addIssue({ code: 'custom', path: [path], message });
        }
      }),
    [name],
  );
  const form = useForm<TestFormValues>({ resolver: zodResolver(schema), defaultValues: EMPTY_TEST_FORM });

  const test = useMutation({
    mutationFn: (values: TestFormValues) => runIntegrationTest(name, toTestRequest(name, values)),
    // The test shows up as the latest success or failure on the dashboard.
    onSettled: () => void queryClient.invalidateQueries({ queryKey: integrationKeys.all }),
    onError: (error) => void applyServerErrors(error, form.setError),
  });

  const submit = form.handleSubmit((values) => test.mutate(values));
  const result = test.data;
  const requestFailed = test.error && !(isApiError(test.error) && test.error.isValidation);

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !test.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form onSubmit={(event) => void submit(event)} noValidate>
          <DialogBody>
            <DialogTitle>Test {displayName}</DialogTitle>
            <DialogContent className={styles.content}>
              {result ? (
                <TestResult result={result} />
              ) : (
                <>
                  <Text>{TEST_DESCRIPTIONS[name]}</Text>
                  {input === 'mobile' && (
                    <TextField control={form.control} name="to" label="Mobile number" type="tel" placeholder="98765 43210" autoFocus autoComplete="off" />
                  )}
                  {input === 'email' && <TextField control={form.control} name="to" label="E-mail address" type="email" autoFocus autoComplete="off" />}
                  {name === 'sms' && (
                    <>
                      <TextField control={form.control} name="dltTemplateId" label="DLT template id" hint="Needed on Indian routes: the approved template the message matches." />
                      <TextAreaField
                        control={form.control}
                        name="message"
                        label="Message"
                        maxLength={500}
                        placeholder="HC360 test message. Please ignore."
                        hint="With a DLT template, type its exact approved text."
                      />
                    </>
                  )}
                  {name === 'whatsapp' && (
                    <FormGrid columns={2}>
                      <TextField control={form.control} name="templateName" label="Template" placeholder="hello_world" hint="Empty: the test template in the settings." />
                      <TextField control={form.control} name="languageCode" label="Language" placeholder="en_US" />
                    </FormGrid>
                  )}
                  {requestFailed && (
                    <MessageBar intent="error" layout="multiline">
                      <MessageBarBody>
                        <MessageBarTitle>The test could not run</MessageBarTitle>
                        {isApiError(test.error) ? test.error.userMessage : 'Please try again.'}
                      </MessageBarBody>
                    </MessageBar>
                  )}
                </>
              )}
            </DialogContent>
            <DialogActions>
              {result ? (
                <>
                  <Button onClick={() => test.reset()}>Test again</Button>
                  <Button appearance="primary" onClick={onClose}>
                    Close
                  </Button>
                </>
              ) : (
                <>
                  <Button onClick={onClose} disabled={test.isPending}>
                    Cancel
                  </Button>
                  <Button type="submit" appearance="primary" disabled={test.isPending} icon={test.isPending ? <Spinner size="tiny" /> : undefined}>
                    {input ? 'Send test' : 'Run test'}
                  </Button>
                </>
              )}
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function TestResult({ result }: { result: IntegrationTestResult }) {
  const styles = useStyles();
  const details = Object.entries(result.details ?? {}).filter(([key, value]) => key !== 'zpl' && value);
  const zpl = result.details?.zpl;
  return (
    <>
      <MessageBar intent={result.success ? 'success' : 'error'} layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>{result.success ? 'Test passed' : 'Test failed'}</MessageBarTitle>
          {result.message}
        </MessageBarBody>
      </MessageBar>
      {details.length > 0 && (
        <div className={styles.details}>
          {details.map(([key, value]) => (
            <span key={key} style={{ display: 'contents' }}>
              <Caption1 className={styles.muted}>{DETAIL_LABELS[key] ?? humanize(key)}</Caption1>
              <Text className={styles.mono}>{value}</Text>
            </span>
          ))}
        </div>
      )}
      {zpl && (
        <div>
          <Caption1 className={styles.muted}>Sample label (ZPL for Zebra printers)</Caption1>
          <pre className={styles.label}>{zpl}</pre>
        </div>
      )}
      {!result.success && <Caption1 className={styles.muted}>Check the settings and the integration log, then test again.</Caption1>}
    </>
  );
}
