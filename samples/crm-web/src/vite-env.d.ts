/// <reference types="vite/client" />

/**
 * CSS modules, typed.
 *
 * `Record<string, string>` rather than a generated per-file type: a typo in a class name is
 * caught by the stylelint pass and by looking at the screen, and the generator that would catch
 * it at compile time costs a build step and a watcher. Named exports are refused so every use
 * goes through the default object, which is the one convention this codebase keeps.
 */
declare module '*.module.css' {
  const classes: Readonly<Record<string, string>>
  export default classes
}
