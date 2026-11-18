// Runs before anything paints, in a file rather than inline because the CSP is script-src 'self'.
//
// The theme follows the device (prefers-color-scheme): there is no switch and no stored choice. Two jobs, both about
// the first frame, and one for when the device flips:
//
//  1. Put Radzen's stylesheet for the device's scheme in the critical path. <RadzenTheme> injects it from Blazor,
//     which is roughly 1.4 s in, and until then every Radzen component on the page is unstyled: an icon is its
//     ligature *text*, so the menu icon measured 72 px wide instead of 24 and the whole top bar re-laid out when the
//     sheet landed. Appended here, it downloads alongside the framework and is applied before Blazor's first render.
//     <RadzenTheme> still owns the runtime swap (ThemeInterop follows the device); a second link to the same file is
//     inert.
//
//  2. Colour the browser's own chrome (the theme-color meta) for the same scheme.
(function () {
  var dark = false;
  try { dark = window.matchMedia('(prefers-color-scheme: dark)').matches; } catch (e) { /* no media queries: light */ }

  var base = '/_content/Radzen.Blazor/';
  var sheet = document.createElement('link');
  sheet.rel = 'stylesheet';
  sheet.href = base + 'css/' + (dark ? 'material-dark-base.css' : 'material-base.css');
  document.head.appendChild(sheet);

  // The icon font is declared inside that sheet, so the browser cannot find it until the sheet parses. Asking for
  // it here means the glyphs are ready when the rules that use them arrive.
  var font = document.createElement('link');
  font.rel = 'preload';
  font.as = 'font';
  font.type = 'font/woff2';
  font.crossOrigin = 'anonymous';
  font.href = base + 'fonts/MaterialSymbolsOutlined.woff2';
  document.head.appendChild(font);
})();

// The address bar and the installed app's title bar take their colour from a meta tag, which is static: without
// this a light page sits under dark chrome. Set at load and again whenever the device flips.
window.PoTheme = {
  prefersDark: function () {
    try { return window.matchMedia('(prefers-color-scheme: dark)').matches; } catch (e) { return false; }
  },
  chrome: function (dark) {
    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) {
      meta.setAttribute('content', dark ? '#0f1117' : '#f6f7fb');
    }
  },
  // Calls dotnet.OnDeviceThemeChanged(isDark) whenever the device's scheme flips (ThemeInterop).
  watch: function (dotnet) {
    try {
      window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function (e) {
        window.PoTheme.chrome(e.matches);
        dotnet.invokeMethodAsync('OnDeviceThemeChanged', e.matches);
      });
    } catch (e) { /* no media queries: the theme stays as loaded */ }
  },
};
window.PoTheme.chrome(window.PoTheme.prefersDark());

// Stage effects (the shaders, the particles, the terrain, the stamps and the foil) are off unless somebody turns them
// on in Settings, and off whatever the setting when the system asks for less motion. Read from localStorage, so the
// first mount already knows. Every drawn-effect entry point asks this one question.
window.PoMotion = {
  still: function () {
    try {
      if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return true;
    } catch (e) { /* no media queries: fall through to the setting */ }
    try {
      return (localStorage.getItem('pofightjudge:effects') || '').replace(/^"|"$/g, '') !== 'on';
    } catch (e) {
      return true;
    }
  },
};
