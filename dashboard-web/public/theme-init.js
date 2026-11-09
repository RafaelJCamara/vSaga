/* Puts the stored display choices on <html> before the first paint: the theme (data-theme) and the density
   (data-density), so a user whose choice differs from the OS or from the default does not see the other look flash
   while the app starts. A file, not an inline script: the Content Security Policy (script-src 'self') allows this and
   forbids the inline kind. The keys and the values are ThemeService's and DensityService's
   (src/app/services/theme.service.ts, density.service.ts); a spec pins them together. A browser that blocks storage
   throws on the access: then the choice is System and Comfortable, which are no attribute. */
try {
  var theme = localStorage.getItem('vsaga.theme');
  if (theme === 'light' || theme === 'dark' || theme === 'high-contrast') {
    document.documentElement.setAttribute('data-theme', theme);
  }
  if (localStorage.getItem('vsaga.density') === 'compact') {
    document.documentElement.setAttribute('data-density', 'compact');
  }
} catch (e) {
  /* System, Comfortable */
}
