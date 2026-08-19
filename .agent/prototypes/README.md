# Design prototypes

Four prototypes, each backing a closed planning ticket on [the v0.1 map](https://github.com/local-variable/EMM/issues/1):

| Directory | Ticket | What it settled |
| --- | --- | --- |
| `undercut-formula` | [#11](https://github.com/local-variable/EMM/issues/11) | That there is no formula — a fitted, per-ware-type model that recalibrates daily, gates on effective sample size, and never refuses to produce a number |
| `graph` | [#12](https://github.com/local-variable/EMM/issues/12) | Two bands that cannot be confused, a third that must not exist, and no interpolation across gaps |
| `ui` | [#13](https://github.com/local-variable/EMM/issues/13) | The information architecture: six tabs, a docked summary strip, and the sell space as the Retainer's own allocation |
| `tax-incidence` | [#19](https://github.com/local-variable/EMM/issues/19) | That there are two levies, not one, and four ways of getting them wrong silently |

Start with `undercut-formula/REVIEW.md` and `ui/DESIGN.md`. Both are written methods-and-limitations first.

## These will go stale after v0.1, and that is expected

They are **point-in-time records of why a decision was made**, not descriptions of what the plugin does. Nothing here is kept in step with the code, and nothing here should be updated to match it.

Once v0.1 ships:

- **The shipped code is the source of truth for behaviour.** Where a prototype and the code disagree about *what happens*, the code is right and the prototype is simply old.
- **The prototype is still the record of *why*.** The reasoning, the measurements and the rejected alternatives do not expire just because the implementation moved on — several of them are the only place a given number was ever measured.
- **A superseded *decision* is recorded on its ticket, not here.** If a ruling was overturned, the ticket's resolution comment and the map say so. Do not infer from a prototype that a decision still stands.

Read them as evidence and reasoning, never as specification. The specification is [the spec](https://github.com/local-variable/EMM/issues/21) and the build tickets under it.

## The market-data corpus is not in this repository

`undercut-formula/data/`, `data_all/` and `data_adam/` are excluded deliberately — roughly 832 MB of cached aggregator data held on the maintainer's working copy. **Do not re-scrape it**: a full pull is about 1,200 requests and the best part of an hour per World. See [`undercut-formula/README.md`](undercut-formula/README.md) before fetching anything.
