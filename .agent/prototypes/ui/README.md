# UI prototype

Backs wayfinder ticket [#13 "Design EMM's UI information architecture"](https://github.com/local-variable/EMM/issues/13).

**Local only, deliberately.** Under `.agent/`, which is gitignored, by the same decision that
keeps the #11 and #12 prototypes local. Do not commit it.

**Read [`DESIGN.md`](DESIGN.md) first** — the five ticket answers as proposals, plus what the
prototype turned up, seven open questions, and what is real versus invented.

## Opening it

`EMM-ui.html` is a single file with the data inlined — open it anywhere, no server. For the
live version, serve the directory:

```powershell
python -m http.server 8791 --bind 127.0.0.1 --directory .agent/prototypes/ui
```

then `http://127.0.0.1:8791/index.html`.

Screenshots time out through the browser pane unless it is displayed. Use headless Edge, which
is what every render here was checked with:

```powershell
& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --headless=new --disable-gpu --screenshot=shot.png --window-size=1380,900 --virtual-time-budget=5000 "http://127.0.0.1:8791/index.html#scene=float&tab=pricing"
```

## Deep links

The view state rides in the hash, so any screen can be linked:

| key | values |
|---|---|
| `scene` | `bell` · `float` · `firstrun` · `space` · `config` |
| `tab` | `tasks` · `pricing` · `space` · `holdings` · `strategies` · `scan` |
| `cfg` | `general` · `window` · `strategies` · `sources` · `mandates` · `space` · `data` · `alerts` |
| `theme` | `dark` · `light` |
| `expanded` | `1` grows the docked window past the retainer list |
| `min` | `1` collapses the window to its title bar |
| `size` | `default` · `large` · `full` |
| `hover` | a Ware key, e.g. `41145-nq` — flips the Pricing card to it |
| `retainer` | a Retainer name |
| `selftest` | `1` drives every control and reports what broke |

For example `#scene=float&tab=pricing&hover=36060-hq&theme=light` or `#scene=config&cfg=data`.

## Self-test

`http://127.0.0.1:8791/index.html#selftest=1` drives every control in the prototype and prints a
pass/fail table over the page. Run it after any change. Currently **89 of 89**.

It also asserts what the eye misses — charts with no pixels, controls rendering dark-on-dark,
blocks overflowing a narrow window, `hidden` panels that are not hidden, and buttons that do
nothing. Any script error is written into the tab title, so a headless run says what broke:

```powershell
& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --headless=new --disable-gpu --dump-dom --window-size=1380,900 --virtual-time-budget=25000 "http://127.0.0.1:8791/index.html#selftest=1" > dom.html
```

## Regenerating the data

`build_data.py` reads **only** the #12 graph prototype's `data.js`. Nothing is fetched and the
corpus is not re-extracted. It takes about a second.

```powershell
python .agent/prototypes/ui/build_data.py
```

## Files

| file | what it is |
|---|---|
| `DESIGN.md` | the proposal, for review |
| `index.html` | the prototype — self-contained, no dependencies |
| `data.js` | ~97 KB: 15 real Wares with their Sales, Listings and Estimates, plus an invented Player |
| `build_data.py` | builds `data.js` from the #12 prototype's extraction |
| `EMM-ui.html` | the same prototype with `data.js` inlined — opens from `file://` |
| `shot-*.png` | the renders shown at the gate |
