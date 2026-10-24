import { Badge, Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import type { TemplateServiceDto } from './api';
import { ORDERABLE_CATEGORIES, serviceCategoryLabel } from './orderCart';
import { ServicePicker } from './ServicePicker';

// The services a template or an order set orders (EMR-02, EMR-05), as both their drawers edit them.

const useStyles = makeStyles({
  list: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '4px' },
  service: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '4px 8px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  serviceText: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  error: { color: tokens.colorPaletteRedForeground1 },
});

/** The services a template or order set orders: picked from the catalogue, removed one at a time, in order picked. */
export function PickedServices({
  label,
  services,
  onChange,
  max,
  error,
}: {
  label: string;
  services: readonly TemplateServiceDto[];
  onChange: (services: TemplateServiceDto[]) => void;
  max: number;
  error?: string;
}) {
  const styles = useStyles();
  const full = services.length >= max;
  return (
    <div>
      <ServicePicker
        label={label}
        categories={ORDERABLE_CATEGORIES}
        showPrice={false}
        disabled={full}
        hint={full ? `At most ${max} services.` : `${services.length} of at most ${max}.`}
        onPick={(picked) => {
          if (!services.some((s) => s.serviceItemId === picked.id)) {
            onChange([
              ...services,
              { serviceItemId: picked.id, code: picked.code, name: picked.name, category: picked.category },
            ]);
          }
        }}
      />
      {error && (
        <Caption1 role="alert" className={styles.error}>
          {error}
        </Caption1>
      )}
      {services.length > 0 && (
        <ul className={styles.list} aria-label={`${label}: chosen`} style={{ marginTop: '8px' }}>
          {services.map((service) => (
            <li key={service.serviceItemId} className={styles.service}>
              <span className={styles.serviceText}>
                <Text weight="semibold">{service.name}</Text>
                <Caption1 className={styles.muted}>{service.code}</Caption1>
              </span>
              <Badge size="small" appearance="tint">
                {serviceCategoryLabel[service.category]}
              </Badge>
              <Button
                size="small"
                appearance="subtle"
                icon={<DismissRegular />}
                aria-label={`Remove ${service.name}`}
                onClick={() => onChange(services.filter((s) => s.serviceItemId !== service.serviceItemId))}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
