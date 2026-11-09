/* Puts the stored theme choice on <html> before the first paint, so a user whose choice differs from the OS does
   not see the other theme flash while the app starts. A file, not an inline script: the Content Security Policy
   (script-src 'self') allows this and forbids the inline kind. The key and the values are ThemeService's
   (src/app/services/theme.service.ts); a spec pins them together. A browser that blocks storage throws on the
   access: then the choice is System, which is no attribute. */
try {
  var stored = localStorage.getItem('vsaga.theme');
  if (stored === 'light' || stored === 'dark' || stored === 'high-contrast') {
    document.documentElement.setAttribute('data-theme', stored);
  }
} catch (e) {
  /* System */
}
