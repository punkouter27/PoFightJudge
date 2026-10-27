// Runs before anything paints, in a file rather than inline because the CSP is script-src 'self'.
//
// Two jobs, both about the first frame:
//
//  1. Stamp the stored theme on <html> so the palette does not flash. Dark is the default (no attribute needed);
//     only an explicit "light" choice is stamped. Blazored.LocalStorage writes with SetItemAsStringAsync, so the
//     value is the bare word rather than a JSON string — the quote-strip below tolerates either.
//
//  2. Put Radzen's stylesheet in the critical path. <RadzenTheme> injects it from Blazor, which is roughly 1.4 s
//     in, and until then every Radzen component on the page is unstyled: an icon is its ligature *text*, so the
//     menu icon measured 72 px wide instead of 24 and the whole top bar re-laid out when the sheet landed. That was
//     0.11 of the home page's 0.12 CLS. Appended here, it downloads alongside the framework and is applied before
//     Blazor's first render. <RadzenTheme> still owns the runtime swap; a second link to the same file is inert.
(function () {
  var theme = null;
  try {
    theme = localStorage.getItem('po-theme');
    if (theme) theme = theme.replace(/^"|"$/g, '');
    if (theme === 'light' || theme === 'dark') document.documentElement.setAttribute('data-theme', theme);
  } catch (e) { /* storage blocked: keep the default */ }

  var base = '/_content/Radzen.Blazor/';
  var sheet = document.createElement('link');
  sheet.rel = 'stylesheet';
  sheet.href = base + 'css/' + (theme === 'light' ? 'material-base.css' : 'material-dark-base.css');
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
// this a light page sits under dark chrome. Called at load for the stored choice and again whenever it changes.
window.PoTheme = {
  chrome: function (theme) {
    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) {
      meta.setAttribute('content', theme === 'light' ? '#f4f1ea' : '#0b0d12');
    }
  },
};
window.PoTheme.chrome(document.documentElement.getAttribute('data-theme'));

// Stage effects (the shaders, the particles, the terrain, the stamps and the foil) are off unless somebody turns them
// on in Settings, and off whatever the setting when the system asks for less motion. Read from localStorage, like
// the theme, so the first mount already knows. Every drawn-effect entry point asks this one question.
window.PoMotion = {
  still: function () {
    try {
      if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return true;
    } catch (e) { /* no media queries: fall through to the setting */ }
    try {
      return (localStorage.getItem('po-effects') || '').replace(/^"|"$/g, '') !== 'on';
    } catch (e) {
      return true;
    }
  },
};
