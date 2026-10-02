import { Caption1, makeStyles, Subtitle2, tokens } from '@fluentui/react-components';
import { StatusBadge } from '@hc360/ui';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '24px', paddingBottom: '8px' },
  section: { display: 'flex', flexDirection: 'column', gap: '12px' },
  heading: { margin: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  toolbar: { padding: '0 16px 12px' },
});

/** An editor's fields, in titled sections. */
export function EditorForm({ children }: { children: ReactNode }) {
  return <div className={useStyles().form}>{children}</div>;
}

export function EditorSection({
  title,
  description,
  children,
}: {
  title: string;
  description?: ReactNode;
  children: ReactNode;
}) {
  const styles = useStyles();
  return (
    <section className={styles.section} aria-label={title}>
      <div>
        <Subtitle2 as="h3" className={styles.heading} block>
          {title}
        </Subtitle2>
        {description && <Caption1 className={styles.muted}>{description}</Caption1>}
      </div>
      {children}
    </section>
  );
}

/** "In use" or "Not in use", as every master shows it. */
export function InUseBadge({ isActive }: { isActive: boolean }) {
  return <StatusBadge status={isActive ? 'Active' : 'Inactive'} label={isActive ? 'In use' : 'Not in use'} />;
}

/** The filters above a master's list. */
export function ListToolbar({ children }: { children: ReactNode }) {
  return <div className={useStyles().toolbar}>{children}</div>;
}
