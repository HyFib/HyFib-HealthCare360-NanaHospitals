import { makeStyles } from '@fluentui/react-components';
import { CardLink } from '@/components/links';
import { ListRow } from '@/components/ListRow';
import { useT } from '@/i18n';
import { KIND_LABEL, REQUEST_KINDS, type RequestKind } from './labels';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
});

/** "Correct your details", "Ask us to erase your data" and "Raise a privacy concern", each opening its form. */
export function RequestLinks({ kinds = REQUEST_KINDS }: { kinds?: readonly RequestKind[] }) {
  const styles = useStyles();
  const t = useT();
  return (
    <div className={styles.list}>
      {kinds.map((kind) => {
        const label = KIND_LABEL[kind];
        const Icon = label.icon;
        return (
          <CardLink key={kind} to="/my-privacy/requests/new" search={{ type: kind }}>
            <ListRow icon={<Icon />} title={t(label.title)} subtitle={t(label.hint)} />
          </CardLink>
        );
      })}
    </div>
  );
}
