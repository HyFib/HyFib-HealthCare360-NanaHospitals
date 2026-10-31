import {
  FieldContextProvider,
  Label,
  makeStyles,
  mergeClasses,
  tokens,
  typographyStyles,
  useId,
} from '@fluentui/react-components';
import { DiamondDismiss12Filled } from '@fluentui/react-icons';
import type { ReactNode } from 'react';

// A Fluent Field is made for one control: through context it gives that control its id, `required` and aria-*, and it
// gives them to every other control inside it too. Around a set of checkboxes every box had the Field's id, so each
// box's own label pointed at the first box: the other boxes had no name (WCAG 1.3.1, 4.1.2), clicking a label ticked
// the first box, and every box showed the required asterisk. A set of checkboxes is a CheckboxGroup instead.

const ICON_SIZE = '12px';

const useStyles = makeStyles({
  // As a Field lays out its label, its control, its validation message and its hint.
  root: { display: 'grid' },
  label: {
    maxWidth: 'max-content',
    maxHeight: 'max-content',
    paddingTop: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalXXS,
    marginBottom: tokens.spacingVerticalXXS,
  },
  srOnly: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  },
  secondary: {
    marginTop: tokens.spacingVerticalXXS,
    color: tokens.colorNeutralForeground3,
    ...typographyStyles.caption1,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
    paddingLeft: `calc(${ICON_SIZE} + ${tokens.spacingHorizontalXS})`,
  },
  icon: {
    display: 'inline-block',
    fontSize: ICON_SIZE,
    marginLeft: `calc(-${ICON_SIZE} - ${tokens.spacingHorizontalXS})`,
    marginRight: tokens.spacingHorizontalXS,
    lineHeight: '0',
    verticalAlign: '-1px',
  },
});

/**
 * A labelled set of checkboxes (roles, weekdays, meal rounds), shown as a Field shows its control: the label above, the
 * validation message and the hint below. The checkboxes go in as children, in the screen's own layout. Each keeps its
 * own id and is named by its own label; the group is named by `label` and described by the message and the hint.
 *
 * `required` asks for at least one box: the asterisk is on the group's label, and no box is required. A group cannot
 * carry aria-required (axe aria-allowed-attr), so a required group's name ends in "(required)" for screen readers.
 */
export function CheckboxGroup({
  label,
  hint,
  required,
  validationMessage,
  validationState = validationMessage ? 'error' : 'none',
  children,
}: {
  label: string;
  hint?: ReactNode;
  required?: boolean;
  validationMessage?: ReactNode;
  validationState?: 'error' | 'none';
  children: ReactNode;
}) {
  const styles = useStyles();
  const id = useId('checkbox-group-');
  const labelId = `${id}__label`;
  const requiredId = `${id}__required`;
  const messageId = `${id}__validationMessage`;
  const hintId = `${id}__hint`;
  const invalid = validationState === 'error';
  // Two ids, so that the name is "Roles (required)" in every browser: they are joined with a space.
  const labelledBy = required ? `${labelId} ${requiredId}` : labelId;
  const describedBy = [validationMessage ? messageId : null, hint ? hintId : null].filter(Boolean).join(' ');
  return (
    <div role="group" aria-labelledby={labelledBy} aria-describedby={describedBy || undefined} className={styles.root}>
      <Label id={labelId} required={required} className={styles.label}>
        {label}
      </Label>
      {required && (
        <span id={requiredId} className={styles.srOnly}>
          (required)
        </span>
      )}
      {/* No Field's context reaches the boxes, even with the group inside a Field by mistake. */}
      <FieldContextProvider value={undefined}>{children}</FieldContextProvider>
      {validationMessage ? (
        <div
          id={messageId}
          role={invalid ? 'alert' : undefined}
          className={mergeClasses(styles.secondary, invalid && styles.error)}
        >
          {invalid && (
            <span className={styles.icon}>
              <DiamondDismiss12Filled />
            </span>
          )}
          {validationMessage}
        </div>
      ) : null}
      {hint ? (
        <div id={hintId} className={styles.secondary}>
          {hint}
        </div>
      ) : null}
    </div>
  );
}
