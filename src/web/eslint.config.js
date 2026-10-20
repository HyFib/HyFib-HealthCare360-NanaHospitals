// Flat ESLint config for all HC360 web packages.
import js from '@eslint/js';
import noUnsanitized from 'eslint-plugin-no-unsanitized';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  {
    // Build output, and what a Playwright run leaves behind (its HTML report holds the trace viewer's own scripts).
    ignores: [
      '**/dist/**',
      '**/node_modules/**',
      '**/routeTree.gen.ts',
      '**/coverage/**',
      '**/playwright-report/**',
      '**/test-results/**',
      '**/blob-report/**',
    ],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: { ecmaVersion: 2023, globals: globals.browser },
    plugins: { 'react-hooks': reactHooks, 'react-refresh': reactRefresh },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true, allowExportNames: ['Route'] }],
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
      '@typescript-eslint/consistent-type-imports': 'error',
      'no-console': ['warn', { allow: ['warn', 'error'] }],
      eqeqeq: ['error', 'smart'],
      // Code built from strings would run with the signed-in user's access to patient records.
      'no-eval': 'error',
      'no-implied-eval': 'error',
      'no-new-func': 'error',
      'no-script-url': 'error',
    },
  },
  {
    // HTML written straight into the page (innerHTML, outerHTML, insertAdjacentHTML, document.write …) is where XSS
    // gets in: React escapes what it renders, these do not. Each use is reviewed and carries an eslint-disable comment
    // saying why its markup is safe.
    ...noUnsanitized.configs.recommended,
    files: ['**/*.{ts,tsx}'],
  },
  {
    // A Fluent Spinner is a progress bar to a screen reader, and one without a name is read only as "progress bar"
    // (axe aria-progressbar-name, SRS UX-10). Name one that stands for something loading (label, or aria-label to keep
    // it small), and hide one beside words that already say so, such as a busy button's icon (aria-hidden).
    files: ['**/*.tsx'],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          selector:
            "JSXOpeningElement[name.name='Spinner']:not(:has(JSXAttribute[name.name=/^(label|aria-label|aria-labelledby|aria-hidden)$/]))",
          message:
            'Give the Spinner a label or aria-label when it stands for something loading, or aria-hidden when the control it is in already says it is busy.',
        },
      ],
    },
  },
  {
    // The tests hand javascript: links to the code that must refuse them.
    files: ['**/*.test.{ts,tsx}'],
    rules: { 'no-script-url': 'off' },
  },
  {
    // Shared packages are libraries, not hot-reload boundaries: exporting hooks and helpers next to components is fine.
    // Route files export `Route`; the TanStack Router plugin code-splits their components, so HMR still works. Test
    // helpers are never hot-reloaded.
    files: ['packages/**/*.{ts,tsx}', 'apps/*/src/routes/**/*.tsx', 'apps/*/src/test/**/*.{ts,tsx}'],
    rules: { 'react-refresh/only-export-components': 'off' },
  },
  {
    // Build and maintenance scripts run in Node.
    files: ['**/scripts/**/*.{js,mjs}', '*.config.js'],
    languageOptions: { ecmaVersion: 2023, sourceType: 'module', globals: globals.node },
  },
);
