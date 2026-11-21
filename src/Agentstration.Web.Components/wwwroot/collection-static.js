// Razor Pages use the same native disclosures as interactive collections.
import { attachActions, attachDisclosure } from './collection-ui.js';
for (const menu of document.querySelectorAll('.collection-actions-menu')) attachActions(menu);
for (const disclosure of document.querySelectorAll('.value-disclosure')) attachDisclosure(disclosure);

if (document.body.classList.contains('auth-page')) {
    const query = matchMedia('(prefers-color-scheme: dark)');
    const applyTheme = () => document.body.classList.toggle('theme-dark', query.matches);
    query.addEventListener('change', applyTheme);
    applyTheme();
}
