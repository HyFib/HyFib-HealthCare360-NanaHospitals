import { Caption1, makeStyles, Subtitle2, Text, tokens } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import type { DowntimePack, DowntimePackTable } from './continuityApi';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '16px' },
  section: { display: 'flex', flexDirection: 'column', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
  alert: { color: tokens.colorPaletteRedForeground1 },
  scroll: { overflowX: 'auto' },
  table: {
    borderCollapse: 'collapse',
    width: '100%',
    fontSize: tokens.fontSizeBase200,
    '& th, & td': {
      border: `1px solid ${tokens.colorNeutralStroke2}`,
      padding: '4px 6px',
      textAlign: 'left',
      verticalAlign: 'top',
    },
    '& th': { backgroundColor: tokens.colorNeutralBackground3 },
  },
});

/** One table of the pack, as a plain HTML table with a caption, so screen readers read its heading and columns. */
export function PackTable({ table }: { table: DowntimePackTable }) {
  const styles = useStyles();
  return (
    <div className={styles.section}>
      {table.alert && (
        <Text weight="bold" className={styles.alert}>
          {table.alert}
        </Text>
      )}
      <div className={styles.scroll}>
        <table className={styles.table}>
          <caption>
            <Text weight="semibold">{table.heading}</Text>
            {table.note && <Caption1 className={styles.muted}> · {table.note}</Caption1>}
          </caption>
          <thead>
            <tr>
              {table.columns.map((c) => (
                <th key={c} scope="col">
                  {c}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {table.rows.length === 0 ? (
              <tr>
                <td colSpan={table.columns.length}>None.</td>
              </tr>
            ) : (
              table.rows.map((row, i) => (
                // Rows have no id of their own; their order is the pack's.
                <tr key={i}>
                  {row.map((cell, j) => (
                    <td key={j}>{cell}</td>
                  ))}
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/** The pre-downtime pack on screen, read only: the same sections and tables as the print. */
export function DowntimePackView({ pack }: { pack: DowntimePack }) {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      <Caption1 className={styles.muted}>
        {pack.kind === 'Nightly' ? 'Nightly pack' : 'Printed'} {formatDateTime(pack.generatedAt)}
        {pack.generatedByName ? ` by ${pack.generatedByName}` : ''} · covers until {formatDateTime(pack.windowEndsAt)} ·{' '}
        {pack.patients} in-patients
      </Caption1>
      {pack.sections.map((section) => (
        <section key={section.key} className={styles.section} aria-label={section.title}>
          <Subtitle2 as="h2">{section.title}</Subtitle2>
          {section.note && <Caption1 className={styles.muted}>{section.note}</Caption1>}
          {section.tables.map((table) => (
            <PackTable key={`${section.key}:${table.heading}`} table={table} />
          ))}
        </section>
      ))}
    </div>
  );
}
