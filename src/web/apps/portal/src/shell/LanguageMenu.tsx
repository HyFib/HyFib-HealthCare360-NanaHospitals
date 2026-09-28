import {
  Button,
  makeStyles,
  Menu,
  MenuItemRadio,
  MenuList,
  MenuPopover,
  MenuTrigger,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular, LocalLanguageRegular } from '@fluentui/react-icons';
import { LANGUAGE_NAMES, LANGUAGES, LOCALES, useLanguage, useT, type Language } from '@/i18n';

const SHORT: Record<Language, string> = { en: 'EN', hi: 'हि' };

const useStyles = makeStyles({
  trigger: { minWidth: '48px', minHeight: '44px' },
  group: {
    display: 'inline-flex',
    padding: '4px',
    gap: '4px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  option: {
    minHeight: '40px',
    minWidth: '96px',
    border: 'none',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground2,
    fontFamily: 'inherit',
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    cursor: 'pointer',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '6px',
    ':hover': { backgroundColor: tokens.colorNeutralBackground3Hover },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '1px' },
  },
  selected: {
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorBrandForeground1,
    boxShadow: tokens.shadow4,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1 },
  },
});

/** Header menu: the current language as a short code; each choice is written in its own language. */
export function LanguageMenu() {
  const styles = useStyles();
  const t = useT();
  const { language, setLanguage } = useLanguage();
  return (
    <Menu
      checkedValues={{ language: [language] }}
      onCheckedValueChange={(_, data) => {
        const next = data.checkedItems[0];
        if (next === 'en' || next === 'hi') {
          setLanguage(next);
        }
      }}
    >
      <MenuTrigger disableButtonEnhancement>
        <Button
          appearance="subtle"
          size="large"
          className={styles.trigger}
          icon={<LocalLanguageRegular />}
          aria-label={t('shell.language.change', { language: LANGUAGE_NAMES[language] })}
        >
          <span aria-hidden>{SHORT[language]}</span>
        </Button>
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          {LANGUAGES.map((option) => (
            <MenuItemRadio key={option} name="language" value={option} lang={LOCALES[option]}>
              {LANGUAGE_NAMES[option]}
            </MenuItemRadio>
          ))}
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}

/** Large side-by-side choice for the sign-in and profile pages. */
export function LanguageChoice() {
  const styles = useStyles();
  const t = useT();
  const { language, setLanguage } = useLanguage();
  return (
    <div role="group" aria-label={t('shell.language.label')} className={styles.group}>
      {LANGUAGES.map((option) => {
        const selected = option === language;
        return (
          <button
            key={option}
            type="button"
            lang={LOCALES[option]}
            aria-pressed={selected}
            className={mergeClasses(styles.option, selected && styles.selected)}
            onClick={() => setLanguage(option)}
          >
            {selected && <CheckmarkRegular aria-hidden />}
            {LANGUAGE_NAMES[option]}
          </button>
        );
      })}
    </div>
  );
}
