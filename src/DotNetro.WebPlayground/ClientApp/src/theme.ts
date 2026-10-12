import goldenBase from 'golden-layout/dist/css/goldenlayout-base.css?inline';
import goldenDark from 'golden-layout/dist/css/themes/goldenlayout-dark-theme.css?inline';
import goldenLight from 'golden-layout/dist/css/themes/goldenlayout-light-theme.css?inline';
import { monaco } from './monaco';

// Follows prefers-color-scheme live: swaps the Golden Layout theme stylesheet, the Monaco theme
// and a data attribute the app CSS keys its variables on.
const query = window.matchMedia('(prefers-color-scheme: dark)');

const baseStyle = document.createElement('style');
baseStyle.textContent = goldenBase;
const themeStyle = document.createElement('style');
document.head.append(baseStyle, themeStyle);

export const isDark = () => query.matches;
export const monacoTheme = () => (query.matches ? 'vs-dark' : 'vs');

function apply() {
  themeStyle.textContent = query.matches ? goldenDark : goldenLight;
  document.documentElement.dataset.theme = query.matches ? 'dark' : 'light';
  monaco.editor.setTheme(monacoTheme());
}

export function initTheme() {
  apply();
  query.addEventListener('change', apply);
}
