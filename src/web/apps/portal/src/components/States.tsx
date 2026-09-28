import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Skeleton,
  SkeletonItem,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import type { ReactNode } from 'react';
import { useT, type MessageKey } from '@/i18n';
import { describeError } from '@/lib/errors';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '14px',
    minHeight: '64px',
    boxSizing: 'border-box',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  lines: { display: 'flex', flexDirection: 'column', gap: '8px', flex: 1 },
  spinner: { display: 'flex', justifyContent: 'center', alignItems: 'center', padding: '48px 16px', minHeight: '40vh' },
  hidden: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    margin: '-1px',
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
    border: 0,
    padding: 0,
  },
  message: { borderRadius: tokens.borderRadiusLarge },
});

/** Placeholder cards while a list loads (announced once as "Loading…"). */
export function LoadingCards({ count = 3 }: { count?: number }) {
  const styles = useStyles();
  const t = useT();
  return (
    <div className={styles.list} aria-busy="true">
      <span className={styles.hidden} role="status">
        {t('common.loading')}
      </span>
      <Skeleton aria-hidden className={styles.list}>
        {Array.from({ length: count }, (_, index) => (
          <div key={index} className={styles.row}>
            <SkeletonItem shape="circle" size={40} />
            <div className={styles.lines}>
              <SkeletonItem shape="rectangle" size={16} style={{ width: '60%' }} />
              <SkeletonItem shape="rectangle" size={12} style={{ width: '40%' }} />
            </div>
          </div>
        ))}
      </Skeleton>
    </div>
  );
}

export function PageSpinner({ label }: { label?: string }) {
  const styles = useStyles();
  const t = useT();
  return (
    <div className={styles.spinner}>
      <Spinner size="large" label={label ?? t('common.loading')} labelPosition="below" />
    </div>
  );
}

/** A failure in plain words, with "Try again" when retrying can help and the server's reference for the help desk. */
export function ErrorBlock({ error, onRetry, title }: { error: unknown; onRetry?: () => void; title?: MessageKey }) {
  const styles = useStyles();
  const t = useT();
  const friendly = describeError(error, t, title);
  const intent = isApiError(error) && error.isNetworkError ? 'warning' : 'error';
  return (
    <MessageBar intent={intent} layout="multiline" className={styles.message}>
      <MessageBarBody>
        <MessageBarTitle>{friendly.title}</MessageBarTitle>
        {friendly.message}
        {friendly.reference && <Caption1 block>{t('common.reference', { id: friendly.reference })}</Caption1>}
      </MessageBarBody>
      {onRetry && (
        <MessageBarActions>
          <Button icon={<ArrowClockwiseRegular />} onClick={onRetry}>
            {t('common.retry')}
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}

/** Inline, non-blocking notice (info/success/warning) inside a page. */
export function Notice({
  intent = 'info',
  title,
  children,
  action,
}: {
  intent?: 'info' | 'success' | 'warning' | 'error';
  title?: ReactNode;
  children?: ReactNode;
  action?: ReactNode;
}) {
  const styles = useStyles();
  return (
    <MessageBar intent={intent} layout="multiline" className={styles.message}>
      <MessageBarBody>
        {title && <MessageBarTitle>{title}</MessageBarTitle>}
        {children}
      </MessageBarBody>
      {action && <MessageBarActions>{action}</MessageBarActions>}
    </MessageBar>
  );
}
