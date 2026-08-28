# Banka UI

Hotová vrstva k vložení do projektu. Katalog v [`../mockup/ui-styly-katalog.html`](../mockup/ui-styly-katalog.html)
slouží k **výběru**, tohle je to, co se pak **použije**.

Bez build kroku, bez závislostí za běhu. Tři soubory CSS a jeden atribut.

## Rychle

```html
<link rel="stylesheet" href="bank/fonts.css">              <!-- nepovinné -->
<link rel="stylesheet" href="bank/tokens/style/ops-steel.css">
<link rel="stylesheet" href="bank/ui.css">

<div class="ui" data-layout="side-nav">
  <div class="p-title">…</div>
  <nav class="p-nav">…</nav>
  <main class="p-main">…</main>
  <footer class="p-status">…</footer>
</div>
```

Funkční kostru se vším všudy má [`example.html`](example.html) — otevři ji
v prohlížeči, přepínej styl a rozvržení a zkopíruj si markup.

**Styl** = který soubor `tokens/style/*.css` připojíš.
**Rozvržení** = `data-layout` na obalu (`side-nav`, `top-tabs`, `rail`,
`master-detail`, `split-console`).

Obal musí mít **výšku** — banka si ji sama nenastaví, kostra je mřížka
a řídí se tím, kolik místa dostane.

## Co je uvnitř

| Cesta | Co to je | Původ |
|---|---|---|
| `ui.css` | Vrstva komponent — skořápka, menu, tabulka, rozvržení, grafy. Vše zanořené pod `.ui`, takže se nemíchá se stylem hostitelské stránky | generováno z katalogu |
| `fonts.css` | `@font-face` na vendorovaná písma | ruční |
| `tokens/style/*.css` | 23 kurátorovaných stylů | generováno z katalogu |
| `tokens/scheme/*.css` | Palety odvozené z base16 / base24 schémat | generováno ze staženého |
| `tokens/index.json` | Přehled všech 533 schémat i s naměřeným verdiktem a důvody | generováno |
| `example.html` | Ukázka a zároveň návod | ruční |

**Generované soubory se needitují** — přepíše je další běh. Styl se mění
v katalogu, palety v generátoru.

## Stažení knihoven do projektu

```
node scripts/fetch-vendor.mjs     # písma + 533 schémat  →  vendor/
node scripts/build-tokens.mjs     # schémata  →  bank/tokens/scheme/
node scripts/build-bank.mjs       # katalog   →  bank/ui.css + tokens/style/
```

Node 18+, žádné `npm install`. `vendor/` a `bank/tokens/scheme/` jsou mimo
repozitář — obnoví je skripty.

### Proč vendorovat a ne linkovat CDN

Aplikace běží ve vnitřní síti, kde se ven nemusí dostat. Písmo, které se
nenačte, navíc není kosmetika: mění metriku, a řádek pak přestane sedět na
výšku, kterou má z tokenu. Stažená kopie je součást projektu.

**Cascadia Mono na běžné stanici není** (chodí s Terminálem a VS Code),
**Inter na Windows taky ne**. Bez `fonts.css` se obojí nahradí (Consolas,
Segoe UI) — použitelné, ale vypadá to jinak. Obojí je pod SIL OFL 1.1, takže
kopie v projektu je v pořádku; licence jsou ve `vendor/fonts/LICENSE.md`.

`latin-ext` podmnožina není volitelná — bez ní chybí ř, ě, š, č, ů a
prohlížeč je dokreslí jiným písmem uprostřed slova.

## Palety ze schémat

`tinted-theming/schemes` má 337 base16 + 196 base24 palet. Nejsou to ale
palety pro tabulku, ale **pro editor**, a to je rozdíl, který stojí za pozornost:

> Z 533 schémat prošlo přímé mapování jen u **32**. Nepadá to na barvách, ale
> na rolích — `base03` je barva komentáře, schválně nevýrazná, a `base02` je
> pozadí výběru v editoru, kde na něm nikdo nečte celý sloupec.

Generátor proto role **opravuje uvnitř téže palety**: co neprojde, posune se po
vlastní rampě schématu (`base03` → `base07`) na první člen, který stačí. Žádná
barva se nedopočítává, jen se sáhne o stupeň vedle. Tím se použitelných schémat
stane **292** — u každého je v `index.json` napsané, které role se posunuly.

Zbylých 241 propadá právem: bývá to stavová barva pod 3:1 proti ploše, a tu
nelze nahradit, aniž by paleta přestala být sama sebou.

```jsonc
// bank/tokens/index.json
{
  "counts": { "total": 533, "pass": 259, "warn": 33, "fail": 241, "written": 292 },
  "schemes": [
    { "slug": "base16-gruvbox-dark-hard", "verdict": "pass",
      "repaired": ["doplňkový text → base04"], "problems": [] }
  ]
}
```

Schéma nese **jednu** variantu, takže ve vygenerovaném souboru jsou světlá
i tmavá sada shodné — při přepnutí režimu se paleta nemá čím vystřídat. Pár
světlá/tmavá si slep ze dvou schémat: `--l-*` z jednoho, `--d-*` z druhého.
23 kurátorovaných stylů v `tokens/style/` pár nese rovnou.

## Než to nasadíš

Projeď **kontrolu zobrazení** v katalogu — tlačítko *Zkontrolovat všechny
styly*, a to v tom rozvržení, které budeš používat. Nálezy se liší podle
kostry, ne jen podle stylu; `top-tabs` například padalo u všech 23 stylů kvůli
posuvníku, který si ukousl výšku pásu záložek.

Podrobnosti v [`../docs/styly.md`](../docs/styly.md#kontrola-zobrazení).
