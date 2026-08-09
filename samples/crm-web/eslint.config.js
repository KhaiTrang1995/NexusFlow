import js from '@eslint/js'
import reactHooks from 'eslint-plugin-react-hooks'
import tseslint from 'typescript-eslint'

/**
 * The lint this project needs, and not one rule more.
 *
 * TYPE-AWARE, BECAUSE THE COMPILER IS ALREADY RUNNING. Everything here that fires needs to know
 * what a value is: a promise nobody awaited, a `catch` that swallows, a comparison that can only
 * go one way. The purely syntactic half of a stylistic preset would find nothing this codebase
 * gets wrong, and would find plenty it gets right differently.
 *
 * FORMATTING IS NOT LINT. No stylistic rules: the argument they settle is not one anybody here is
 * having, and `--max-warnings 0` turns each of them into a build break over a comma.
 *
 * `verbatimModuleSyntax` IS ON, so a type imported as a value is a runtime import of a module that
 * may not exist at runtime. `consistent-type-imports` is what keeps that from being found by a
 * broken build instead.
 */
export default tseslint.config(
  { ignores: ['dist/**', 'node_modules/**', 'coverage__/**'] },

  js.configs.recommended,
  tseslint.configs.recommendedTypeChecked,
  reactHooks.configs['recommended-latest'],

  {
    languageOptions: {
      parserOptions: {
        projectService: {
          // `vitest.config.ts` is deliberately outside `tsconfig.json` — its own header says why —
          // so the project service has no program for it and would report a parse error rather
          // than lint it. This gives it an inferred one.
          allowDefaultProject: ['vitest.config.ts'],
        },
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      // The three `console.warn`s in this codebase are development-only assertions and each one
      // already carries a disable comment — written against a rule that was never configured, so
      // they were disabling nothing. A fourth, left in by accident, ships to every reader's
      // console.
      'no-console': 'error',

      '@typescript-eslint/consistent-type-imports': [
        'error',
        { prefer: 'type-imports', fixStyle: 'separate-type-imports' },
      ],

      // A leading underscore is the established way of saying "required by the signature, unused
      // here", and `noUnusedParameters` in tsconfig already honours it.
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrors: 'all', caughtErrorsIgnorePattern: '^_' },
      ],
    },
  },

  {
    // This file, and any other plain JavaScript. It is not in `tsconfig.json` — nothing imports
    // it — so the project service has no types for it and every type-aware rule would report a
    // parsing error rather than a finding.
    files: ['**/*.js'],
    extends: [tseslint.configs.disableTypeChecked],
  },

  {
    // Tests assert against values the compiler has been told nothing about — a fixture cast from
    // a literal, an `any` out of a mock. The unsafe-* family is noise there and silence here.
    files: ['src/**/__tests__/**', 'src/test/**'],
    rules: {
      '@typescript-eslint/no-unsafe-assignment': 'off',
      '@typescript-eslint/no-unsafe-member-access': 'off',
      '@typescript-eslint/no-unsafe-argument': 'off',
      '@typescript-eslint/no-unsafe-call': 'off',
      '@typescript-eslint/no-unsafe-return': 'off',
      '@typescript-eslint/no-non-null-assertion': 'off',
    },
  },
)
