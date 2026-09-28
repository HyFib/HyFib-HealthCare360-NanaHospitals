import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { ChevronLeftRegular } from '@fluentui/react-icons';
import { createLink, type LinkComponent } from '@tanstack/react-router';
import { forwardRef, type AnchorHTMLAttributes } from 'react';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    minHeight: '64px',
    padding: '12px 14px',
    boxSizing: 'border-box',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground1,
    textDecorationLine: 'none',
    transitionProperty: 'background-color, box-shadow',
    transitionDuration: tokens.durationFaster,
    WebkitTapHighlightColor: 'transparent',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover, boxShadow: tokens.shadow8 },
    ':active': { backgroundColor: tokens.colorNeutralBackground1Pressed },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  back: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    minHeight: '44px',
    paddingRight: '8px',
    color: tokens.colorBrandForegroundLink,
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    textDecorationLine: 'none',
    alignSelf: 'flex-start',
    ':hover': { textDecorationLine: 'underline' },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px', borderRadius: tokens.borderRadiusMedium },
  },
  text: {
    color: tokens.colorBrandForegroundLink,
    fontWeight: tokens.fontWeightSemibold,
    textDecorationLine: 'underline',
    textUnderlineOffset: '3px',
    ':hover': { color: tokens.colorBrandForegroundLinkHover },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px', borderRadius: tokens.borderRadiusSmall },
  },
});

type AnchorProps = AnchorHTMLAttributes<HTMLAnchorElement>;

const CardAnchor = forwardRef<HTMLAnchorElement, AnchorProps>(function CardAnchor({ className, ...props }, ref) {
  const styles = useStyles();
  return <a ref={ref} {...props} className={mergeClasses(styles.card, className)} />;
});

const BackAnchor = forwardRef<HTMLAnchorElement, AnchorProps>(function BackAnchor({ className, children, ...props }, ref) {
  const styles = useStyles();
  return (
    <a ref={ref} {...props} className={mergeClasses(styles.back, className)}>
      <ChevronLeftRegular fontSize={20} aria-hidden />
      {children}
    </a>
  );
});

const TextAnchor = forwardRef<HTMLAnchorElement, AnchorProps>(function TextAnchor({ className, ...props }, ref) {
  const styles = useStyles();
  return <a ref={ref} {...props} className={mergeClasses(styles.text, className)} />;
});

const CreatedCardLink = createLink(CardAnchor);
const CreatedBackLink = createLink(BackAnchor);
const CreatedTextLink = createLink(TextAnchor);

/** A whole card that opens a record: large touch target, keyboard focusable, opens in a new tab with a long press. */
export const CardLink: LinkComponent<typeof CardAnchor> = (props) => <CreatedCardLink {...props} />;

/** "‹ Appointments": the way back up, always in the same place at the top of a detail page. */
export const BackLink: LinkComponent<typeof BackAnchor> = (props) => <CreatedBackLink {...props} />;

/** An inline text link inside sentences. */
export const TextLink: LinkComponent<typeof TextAnchor> = (props) => <CreatedTextLink {...props} />;
