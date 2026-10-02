import { Caption1, Caption1Strong, makeStyles, Switch, Text, tokens } from '@fluentui/react-components';
import { Section } from '@hc360/ui';
import { stateLabel, type FeatureDto } from './api';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '16px' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  whenOff: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    paddingLeft: '12px',
    borderLeft: `3px solid ${tokens.colorNeutralStroke2}`,
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * One card per feature switch: what the feature does, what stops while it is off, and its switch (On or Off beside it,
 * as in Windows settings). Turning a switch calls `onChange`; the screen asks before anything is turned off.
 */
export function FeatureList({
  features,
  saving,
  onChange,
}: {
  features: FeatureDto[];
  /** A change is being saved: the switches wait for it. */
  saving: boolean;
  onChange: (feature: FeatureDto, enabled: boolean) => void;
}) {
  const styles = useStyles();
  return (
    <div className={styles.list}>
      {features.map((feature) => (
        <Section
          key={feature.key}
          title={feature.name}
          actions={
            <Switch
              checked={feature.enabled}
              disabled={saving}
              label={stateLabel(feature.enabled)}
              labelPosition="before"
              aria-label={feature.name}
              onChange={(_, data) => onChange(feature, data.checked)}
            />
          }
        >
          <div className={styles.body}>
            <Text block>{feature.description}</Text>
            <div className={styles.whenOff}>
              <Caption1Strong>When it is off</Caption1Strong>
              <Caption1 className={styles.muted}>{feature.whenOff}</Caption1>
            </div>
          </div>
        </Section>
      ))}
    </div>
  );
}
