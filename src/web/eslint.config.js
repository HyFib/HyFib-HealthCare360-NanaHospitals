// Flat ESLint config for all HC360 web packages.
import js from '@eslint/js';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['**/dist/**', '**/node_modules/**', '**/routeTree.gen.ts', '**/coverage/**'] },
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
    },
  },
  {
    // Shared packages are libraries, not hot-reload boundaries: exporting hooks and helpers next to components is fine.
    // Route files export `Route`; the TanStack Router plugin code-splits their components, so HMR still works.
    files: ['packages/**/*.{ts,tsx}', 'apps/*/src/routes/**/*.tsx'],
    rules: { 'react-refresh/only-export-components': 'off' },
  },
  {
    // Build and maintenance scripts run in Node.
    files: ['**/scripts/**/*.{js,mjs}', '*.config.js'],
    languageOptions: { ecmaVersion: 2023, sourceType: 'module', globals: globals.node },
  },
);
