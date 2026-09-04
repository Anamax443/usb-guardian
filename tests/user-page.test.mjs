// Test vykreslovaci logiky uzivatelske stranky lokalni konzole.
// Cil: overit, ze stavy (blokuje / jen hlasi / docasne vypnuto) a znacky u medii
// odpovidaji datum z /api/muj-stav — bez prohlizece, cista funkce nad DOM shimem.

// Spusteni z korene repa:  node tests/user-page.test.mjs
// Stranka se cte primo ze zdrojaku agenta, takze test nemuze tvrdit nic o kodu,
// ktery uz tam neni.

import { readFileSync } from 'node:fs';

const zdroj = process.argv[2] ?? 'agent/USBGuardian/LocalConsole/LocalConsoleService.cs';
const cs = readFileSync(zdroj, 'utf8');
const m = cs.match(/private const string UzivatelHtml = """\r?\n([\s\S]*?)\r?\n\s*""";/);
if (!m) { console.error('FAIL: UzivatelHtml nenalezen ve zdrojaku'); process.exit(1); }
const html = m[1].split('\n').map(l => l.replace(/^ {8}/, '')).join('\n');

const js = html.match(/<script>\s*([\s\S]*?)\s*<\/script>/)[1];

// ── minimalni DOM shim (jen to, co stranka opravdu pouziva) ──
function mkEl(tag = 'div') {
  const el = {
    tagName: tag, className: '', _text: '', _html: '', style: {}, children: [], onclick: null,
    attrs: {},
    get textContent() { return this._text; },
    set textContent(v) { this._text = String(v); },
    get innerHTML() { return this._html; },
    set innerHTML(v) { this._html = String(v); },
    getAttribute(k) { return this.attrs[k]; },
    setAttribute(k, v) { this.attrs[k] = v; },
    select() {}, appendChild(c) { this.children.push(c); }, removeChild() {},
    querySelectorAll(sel) {
      if (sel !== 'button') return [];
      // vytahnout tlacitka z vygenerovaneho HTML i s data atributy
      return [...this._html.matchAll(/<button data-k="([^"]*)" data-n="([^"]*)">/g)].map(x => {
        const b = mkEl('button');
        b.attrs['data-k'] = x[1]; b.attrs['data-n'] = x[2];
        return b;
      });
    }
  };
  return el;
}

const ids = ['tecka', 'ochrana', 'ochranaD', 'media', 'pata'];
const store = Object.fromEntries(ids.map(i => [i, mkEl()]));
let kopirovano = null;

globalThis.document = {
  getElementById: id => store[id],
  createElement: mkEl,
  body: { appendChild() {}, removeChild() {} },
  execCommand() { kopirovano = true; return true; }
};
Object.defineProperty(globalThis, 'navigator', {
  configurable: true,
  value: { clipboard: { writeText: t => { kopirovano = t; return Promise.resolve(); } } }
});
globalThis.setInterval = () => 0;
globalThis.setTimeout = () => 0;

let data;
globalThis.fetch = () => Promise.resolve({ json: () => Promise.resolve(data) });

// spustit skript stranky (IIFE zavola nacti() → fetch → vykresli)
const spustit = new Function(js);

const nyni = new Date().toISOString();
const pripady = [
  {
    nazev: 'blokuje se, jedno schvalene a jedno zablokovane medium',
    data: {
      hostname: 'TESTPC', agentVerze: 'abc1234',
      ochrana: { blokuje: true, docasneVypnuto: false, docasneDoKdy: null },
      media: [
        { nazev: 'WD Elements', klic: 'WD:ELEMENTS:WX92', velikost: '3726.0 GB', pripojeno: nyni, schvaleno: true,  zablokovano: false, stav: 'schváleno' },
        { nazev: 'Kingston DT', klic: 'KINGSTON:DT30:BE68', velikost: '57.7 GB', pripojeno: nyni, schvaleno: false, zablokovano: true,  stav: 'zablokováno' }
      ]
    },
    ceka: s => [
      [s.ochrana.includes('blokují'), 'hlavni stav rika, ze se blokuje'],
      [s.tecka.includes('zelena'), 'tecka je zelena'],
      [s.media.includes('WD Elements') && s.media.includes('Kingston DT'), 'obe media vypsana'],
      [s.media.includes('znacka ok') && s.media.includes('znacka bad'), 'znacky schvaleno/zablokovano'],
      [s.media.includes('KINGSTON:DT30:BE68'), 'u neschvaleneho je videt identifikace'],
      [!s.media.includes('WD:ELEMENTS:WX92'), 'u schvaleneho se identifikace NEukazuje'],
      [(s.media.match(/<button/g) || []).length === 1, 'tlacitko na kopirovani jen u neschvaleneho'],
      [s.pata.includes('TESTPC') && s.pata.includes('abc1234'), 'paticka ma hostname a verzi']
    ]
  },
  {
    nazev: 'rezim jen varovani',
    data: {
      hostname: 'TESTPC', agentVerze: 'abc1234',
      ochrana: { blokuje: false, docasneVypnuto: false, docasneDoKdy: null },
      media: [{ nazev: 'Kingston DT', klic: 'K:DT:1', velikost: '', pripojeno: nyni, schvaleno: false, zablokovano: false, stav: 'neschválené – zatím jen hlášeno' }]
    },
    ceka: s => [
      [s.ochrana.includes('jen hlásí'), 'hlavni stav rika, ze se jen hlasi'],
      [s.tecka.includes('zluta'), 'tecka je zluta'],
      [s.media.includes('znacka warn'), 'znacka je varovna, ne cervena']
    ]
  },
  {
    nazev: 'break-glass: docasne vypnuto',
    data: {
      hostname: 'TESTPC', agentVerze: 'abc1234',
      ochrana: { blokuje: false, docasneVypnuto: true, docasneDoKdy: nyni },
      media: []
    },
    ceka: s => [
      [s.ochrana.includes('dočasně vypnuté'), 'hlavni stav rika, ze je vypnuto'],
      [s.ochranaD.includes('správce'), 'detail rika, kdo to vypnul'],
      [s.media.includes('Teď není připojené žádné médium'), 'prazdny seznam medii']
    ]
  }
];

let selhalo = 0;
for (const p of pripady) {
  data = p.data;
  ids.forEach(i => { store[i]._text = ''; store[i]._html = ''; store[i].className = ''; });
  spustit();
  await new Promise(r => setImmediate(r));   // dobehnout promise z fetch

  const s = {
    tecka: store.tecka.className, ochrana: store.ochrana._text, ochranaD: store.ochranaD._text,
    media: store.media._html, pata: store.pata._text
  };
  console.log(`\n${p.nazev}`);
  for (const [ok, popis] of p.ceka(s)) {
    console.log(`  ${ok ? 'OK  ' : 'CHYBA'} ${popis}`);
    if (!ok) selhalo++;
  }
}

// kopirovani do schranky
data = pripady[0].data;
spustit();
await new Promise(r => setImmediate(r));
const btn = store.media.querySelectorAll('button')[0];
btn.textContent = 'Zkopírovat pro IT';
// znovu navazat onclick tak, jak to dela stranka
const handlers = [];
store.media.querySelectorAll = () => [btn];
spustit();
await new Promise(r => setImmediate(r));
if (btn.onclick) {
  btn.onclick();
  const ok = typeof kopirovano === 'string' && kopirovano.includes('KINGSTON:DT30:BE68') && kopirovano.includes('TESTPC');
  console.log(`\nkopirovani pro IT\n  ${ok ? 'OK  ' : 'CHYBA'} text pro IT obsahuje identifikaci i nazev pocitace`);
  if (!ok) selhalo++;
} else {
  console.log('\nkopirovani pro IT\n  CHYBA tlacitko nema onclick');
  selhalo++;
}

console.log(`\n${selhalo === 0 ? 'VSE PROSLO' : selhalo + ' KONTROL SELHALO'}`);
process.exit(selhalo === 0 ? 0 : 1);
