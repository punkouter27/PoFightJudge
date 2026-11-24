// Runs before anything paints, in a file rather than inline because the CSP is script-src 'self'.
//
// The theme follows the device (prefers-color-scheme): there is no switch and no stored choice. Two jobs, both about
// the first frame, and one for when the device flips:
//
//  1. Put Radzen's stylesheet for the device's scheme in the critical path, and BEFORE the app's own CSS: this
//     script sits above every app <link> in index.html, so the sheet is appended ahead of them and the app's rules
//     win at equal specificity. It is the only Radzen sheet: there is no <RadzenTheme> (that injected a second copy
//     into <body>, after app.css, ~1.4 s in). When the device flips, the same link is pointed at the other sheet.
//
//  2. Colour the browser's own chrome (the theme-color meta) for the same scheme.
(function () {
  var dark = false;
  try { dark = window.matchMedia('(prefers-color-scheme: dark)').matches; } catch (e) { /* no media queries: light */ }

  var base = '/_content/Radzen.Blazor/';
  var href = function (isDark) { return base + 'css/' + (isDark ? 'material-dark-base.css' : 'material-base.css'); };
  var sheet = document.createElement('link');
  sheet.rel = 'stylesheet';
  sheet.id = 'radzen-theme';
  sheet.href = href(dark);
  document.head.appendChild(sheet);
  try {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function (e) { sheet.href = href(e.matches); });
  } catch (e) { /* no media queries: the sheet stays as loaded */ }

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

// Stage effects (the shaders, the particles, the terrain, the stamps and the foil) are always on, except when the
// system asks for less motion. Every drawn-effect entry point asks this one question.
window.PoMotion = {
  still: function () {
    try {
      return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    } catch (e) {
      return false; // no media queries: effects on
    }
  },
};
