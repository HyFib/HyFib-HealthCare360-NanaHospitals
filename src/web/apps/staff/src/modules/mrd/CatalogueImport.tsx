import {
  Body1Strong,
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowUploadRegular, DocumentCheckmarkRegular, DocumentRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { useRef, useState, type ChangeEvent } from 'react';
import type { CatalogueImportResult, CatalogueStatus } from './api';
import {
  CATALOGUE_FILE_TYPES,
  catalogueName,
  checkCatalogueFile,
  formatCount,
  formatSize,
  importSummary,
  withdrawnNote,
} from './catalogue';

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: '16px' },
  file: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  result: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '12px 16px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  hash: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
  list: { margin: 0, paddingLeft: '20px' },
  catalogues: {
    display: 'grid',
    gap: '12px 24px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(min(100%, 260px), 1fr))',
  },
  catalogue: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
});

/** The server's reason for refusing the file (it names the problem), or a general one. */
function reasonOf(error: unknown): string {
  return isApiError(error) ? error.userMessage : 'The file could not be imported. Try again.';
}

/** What an import did, or would do: the file, what it says it is, and the counts. */
export function ImportResultView({ result }: { result: CatalogueImportResult }) {
  const styles = useStyles();
  const note = withdrawnNote(result.withdrawn, result.dryRun);
  return (
    <div className={styles.panel}>
      <div className={styles.result} aria-label={result.dryRun ? 'What the import would do' : 'What the import did'}>
        <Body1Strong>
          {result.dryRun ? 'Checked: ' : 'Imported into '}
          {catalogueName(result.release)}
        </Body1Strong>
        <Caption1 className={styles.muted}>
          {result.fileName}
          {result.sourceTitle ? ` · the file says it is ${result.sourceTitle}` : ''}
          {result.sourceVersion && !(result.sourceTitle ?? '').includes(result.sourceVersion)
            ? `, version ${result.sourceVersion}`
            : ''}
        </Caption1>
        {importSummary(result).map((line) => (
          <Text key={line}>{line}</Text>
        ))}
        <Caption1 className={styles.muted}>
          SHA-256 <span className={styles.hash}>{result.sha256}</span>
        </Caption1>
      </div>
      {note && (
        <MessageBar intent="warning">
          <MessageBarBody>{note}</MessageBarBody>
        </MessageBar>
      )}
      {result.warnings.length > 0 && (
        <MessageBar intent="info">
          <MessageBarBody>
            <MessageBarTitle>Notes on the file</MessageBarTitle>
            <ul className={styles.list}>
              {result.warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          </MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}

/**
 * Importing a catalogue file in two steps. The administrator chooses the WHO ClaML file or the CMS order file and checks
 * it first: the server reads it and says what would change, and changes nothing. Only then can it be imported. `run`
 * sends the file (`dryRun` for the check); `onImported` hears about a finished import.
 */
export function CatalogueImportPanel({
  run,
  onImported,
}: {
  run: (file: File, dryRun: boolean) => Promise<CatalogueImportResult>;
  onImported?: (result: CatalogueImportResult) => void;
}) {
  const styles = useStyles();
  const input = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [checked, setChecked] = useState<CatalogueImportResult | null>(null);
  const [imported, setImported] = useState<CatalogueImportResult | null>(null);
  const [busy, setBusy] = useState<'check' | 'import' | null>(null);

  const choose = (event: ChangeEvent<HTMLInputElement>) => {
    const chosen = event.target.files?.[0] ?? null;
    event.target.value = '';
    setChecked(null);
    setImported(null);
    setFile(chosen);
    setProblem(chosen ? checkCatalogueFile(chosen) : null);
  };

  const send = async (dryRun: boolean) => {
    if (!file) {
      return;
    }
    setBusy(dryRun ? 'check' : 'import');
    setProblem(null);
    try {
      const result = await run(file, dryRun);
      if (dryRun) {
        setChecked(result);
      } else {
        setImported(result);
        setChecked(null);
        onImported?.(result);
      }
    } catch (error) {
      setProblem(reasonOf(error));
    } finally {
      setBusy(null);
    }
  };

  const startAgain = () => {
    setFile(null);
    setChecked(null);
    setImported(null);
    setProblem(null);
    input.current?.click();
  };

  return (
    <div className={styles.panel}>
      <Text>
        Import the hospital&rsquo;s copy of WHO&rsquo;s ICD-10 2019 ClaML file (icd102019en.xml, or the .zip WHO provides), or the
        CMS ICD-10-PCS order file (icd10pcs_order_YYYY.txt). Check the file first: HC360 shows what would change, and
        nothing changes until you choose Import.
      </Text>

      <div className={styles.file}>
        <Button icon={<DocumentRegular />} onClick={() => input.current?.click()} disabled={busy !== null}>
          {file ? 'Choose another file' : 'Choose file'}
        </Button>
        {file && (
          <Text>
            {file.name} <span className={styles.muted}>({formatSize(file.size)})</span>
          </Text>
        )}
        <input
          ref={input}
          type="file"
          accept={CATALOGUE_FILE_TYPES}
          hidden
          tabIndex={-1}
          aria-label="Catalogue file"
          onChange={choose}
        />
      </div>

      {problem && (
        <MessageBar intent="error" role="alert">
          <MessageBarBody>{problem}</MessageBarBody>
        </MessageBar>
      )}

      {busy && (
        <Spinner
          size="small"
          labelPosition="after"
          label={
            busy === 'check'
              ? 'Reading the file. A full WHO file takes up to a minute.'
              : 'Importing. This takes up to a few minutes; keep this window open.'
          }
        />
      )}

      {!imported && (
        <div className={styles.actions}>
          {!checked ? (
            <Button
              appearance="primary"
              icon={<DocumentCheckmarkRegular />}
              disabled={!file || problem !== null || busy !== null}
              onClick={() => void send(true)}
            >
              Check file
            </Button>
          ) : (
            <Button
              appearance="primary"
              icon={<ArrowUploadRegular />}
              disabled={busy !== null}
              onClick={() => void send(false)}
            >
              Import
            </Button>
          )}
        </div>
      )}

      {checked && !imported && <ImportResultView result={checked} />}

      {imported && (
        <>
          <MessageBar intent="success">
            <MessageBarBody>
              <MessageBarTitle>Imported</MessageBarTitle>
              Doctors and coders see the new codes in their next search.
            </MessageBarBody>
          </MessageBar>
          <ImportResultView result={imported} />
          <div className={styles.actions}>
            <Button onClick={startAgain}>Import another file</Button>
          </div>
        </>
      )}
    </div>
  );
}

/** Each catalogue's state: codes in use, where they came from, and its last import. */
export function CatalogueStatusList({ status }: { status: CatalogueStatus }) {
  const styles = useStyles();
  return (
    <div className={styles.catalogues}>
      {status.catalogues.map((c) => (
        <div key={c.release} className={styles.catalogue} aria-label={catalogueName(c.release)}>
          <Body1Strong>{catalogueName(c.release)}</Body1Strong>
          <Text>
            {formatCount(c.activeCodes)} codes in use
            {c.release !== 'ICD-10-PCS' && c.activeCodes > 0 ? `, ${formatCount(c.codableCodes)} codable` : ''}
          </Text>
          <Caption1 className={styles.muted}>
            {c.lastImport
              ? `From ${c.lastImport.fileName}, imported ${formatDateTime(c.lastImport.at)} by ${c.lastImport.importedBy}.`
              : c.release === 'ICD-11 MMS'
                ? 'Codes are added when someone saves them from the WHO online search.'
                : `The starter list only (${formatCount(c.fromStarterList)} codes). Import the ${
                    c.release === 'ICD-10-PCS' ? 'CMS order file' : 'WHO file'
                  } for the full catalogue.`}
            {c.withdrawnCodes > 0 ? ` ${formatCount(c.withdrawnCodes)} withdrawn.` : ''}
          </Caption1>
        </div>
      ))}
    </div>
  );
}
