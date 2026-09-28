import { Badge, Caption1, Checkbox, Field, makeStyles, MessageBar, MessageBarBody, MessageBarTitle, Text, Textarea, tokens } from '@fluentui/react-components';
import { ShieldCheckmarkRegular } from '@fluentui/react-icons';
import {
  affectedDrugs,
  alertKindLabel,
  MIN_OVERRIDE_REASON,
  severityTone,
  type GroupedAlerts,
  type SafetyAlert,
  type SignDecision,
  type SignRequirements,
} from './safety';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px' },
  list: { display: 'flex', flexDirection: 'column', gap: '10px', margin: '8px 0 0', padding: 0, listStyle: 'none' },
  alert: { display: 'flex', flexDirection: 'column', gap: '2px' },
  alertHead: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground2 },
  confirm: { marginTop: '8px' },
});

function AlertList({ alerts, items }: { alerts: readonly SafetyAlert[]; items: readonly { id: string; drugName: string }[] }) {
  const styles = useStyles();
  return (
    <ul className={styles.list}>
      {alerts.map((alert) => {
        const drugs = affectedDrugs(alert, items);
        return (
          <li key={alert.key} className={styles.alert}>
            <span className={styles.alertHead}>
              <Badge appearance="filled" color={severityTone(alert.severity)}>
                {alert.severity}
              </Badge>
              <Text weight="semibold">{alertKindLabel[alert.kind]}</Text>
              {drugs.length > 0 && <Caption1>({drugs.join(', ')})</Caption1>}
            </span>
            <Text>{alert.message}</Text>
            {alert.detail && <Caption1 className={styles.muted}>{alert.detail}</Caption1>}
          </li>
        );
      })}
    </ul>
  );
}

/**
 * The result of the prescribing safety check, shown in the prescription itself (never as a toast). Signing unlocks only
 * when every requirement is met: an audited clinical reason for blocking alerts, an acknowledgement of warnings and an
 * explicit confirmation of Schedule H1/X medicines.
 */
export function SafetyReview({
  grouped,
  requirements,
  decision,
  onDecisionChange,
  items,
  controlledItems,
  disabled,
}: {
  grouped: GroupedAlerts;
  requirements: SignRequirements;
  decision: SignDecision;
  onDecisionChange: (decision: SignDecision) => void;
  items: readonly { id: string; drugName: string }[];
  controlledItems: readonly string[];
  disabled?: boolean;
}) {
  const styles = useStyles();
  const reasonLength = decision.overrideReason.trim().length;
  const clean = grouped.blocking.length === 0 && grouped.warnings.length === 0 && !requirements.needsControlledConfirmation;

  return (
    <div className={styles.root} aria-live="polite">
      {clean && (
        <MessageBar intent="success" icon={<ShieldCheckmarkRegular />}>
          <MessageBarBody>
            <MessageBarTitle>Safety check passed</MessageBarTitle>
            No allergy, interaction, duplicate-therapy or dose problems were found.
          </MessageBarBody>
        </MessageBar>
      )}

      {grouped.blocking.length > 0 && (
        <MessageBar intent="error" layout="multiline" role="alert">
          <MessageBarBody>
            <MessageBarTitle>
              Stop: {grouped.blocking.length} {grouped.blocking.length === 1 ? 'problem blocks' : 'problems block'} signing
            </MessageBarTitle>
            <Text block>Change the prescription, or give the clinical reason for prescribing anyway. Overrides are recorded and reviewed.</Text>
            <AlertList alerts={grouped.blocking} items={items} />
            <Field
              className={styles.confirm}
              label="Clinical reason for overriding"
              required
              validationState={reasonLength > 0 && reasonLength < MIN_OVERRIDE_REASON ? 'warning' : 'none'}
              validationMessage={`At least ${MIN_OVERRIDE_REASON} characters (${reasonLength} so far).`}
            >
              <Textarea
                value={decision.overrideReason}
                onChange={(_, d) => onDecisionChange({ ...decision, overrideReason: d.value.slice(0, 1000) })}
                rows={2}
                resize="vertical"
                disabled={disabled}
                placeholder="e.g. Tolerated amoxicillin in 2025 without reaction; benefit outweighs the risk"
              />
            </Field>
          </MessageBarBody>
        </MessageBar>
      )}

      {grouped.warnings.length > 0 && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              {grouped.warnings.length} {grouped.warnings.length === 1 ? 'warning' : 'warnings'} to review
            </MessageBarTitle>
            <AlertList alerts={grouped.warnings} items={items} />
            <Checkbox
              className={styles.confirm}
              checked={decision.acknowledgeWarnings}
              disabled={disabled}
              onChange={(_, d) => onDecisionChange({ ...decision, acknowledgeWarnings: Boolean(d.checked) })}
              label="I have reviewed these warnings and want to continue"
            />
          </MessageBarBody>
        </MessageBar>
      )}

      {requirements.needsControlledConfirmation && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Schedule H1/X medicines</MessageBarTitle>
            <Text block>
              {controlledItems.length > 0 ? controlledItems.join(', ') : 'This prescription contains narcotic or psychotropic medicines.'} Signing enters them in the
              controlled-drug register under your name.
            </Text>
            {grouped.controlled.length > 0 && <AlertList alerts={grouped.controlled} items={items} />}
            <Checkbox
              className={styles.confirm}
              checked={decision.confirmControlledDrugs}
              disabled={disabled}
              onChange={(_, d) => onDecisionChange({ ...decision, confirmControlledDrugs: Boolean(d.checked) })}
              label="I confirm I am prescribing these Schedule H1/X medicines"
            />
          </MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}
