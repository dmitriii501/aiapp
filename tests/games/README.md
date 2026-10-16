# Test games for PcPlay

Two small pages with an exact score, so that a run can be judged without anybody's opinion: the page title carries the result
(`Targets — FINISHED score 29 of 29 green, wrong 0`, `Colors — FINISHED correct 15 of 15`).

- `targets.html`: green circles (to click) and red ones (to leave alone) appear for 1.4 s each; 45 s.
- `memory.html`: twelve cards (six symbols, two of each) face down; a click turns one over, two that match stay open; the title carries pairs and moves.
- `colors.html`: a colour is named in words, the four buttons change places every round and carry random words as labels; 15 rounds.

Run one (needs a Jev key and `[pc]` consent; the run takes the mouse for the time it lasts):

```
msedge.exe --app=file:///C:/path/to/tests/games/targets.html --window-size=1100,720 --user-data-dir=%TEMP%\kairo-game-profile
kairo pc play tests/games/targets.policy.json Targets --dry     # look once, show what the rules would do
kairo pc play tests/games/targets.policy.json Targets           # play; the Pause key stops it
```

Measured on 2026-10-16 (Jev 1.14, JPEG pictures of 1024 px): Targets 29 of 29 and no red click, 119 ticks in 62 s (517 ms a tick, 403 ms of it Jev);
Colors 15 of 15, 17 ticks in 12 s (701 ms a tick; five things to find, a choice and a check in every look).
Memory (`memory.policy.json`: notes of what is on which card in a map, the grid worked out from two corner cards): 6 pairs in 9 moves, 22 ticks in 21 s. A single box around the whole grid was not good enough (its edges are inexact, the first live run clicked beside the cards); two corner cards and arithmetic were.

`limits.pipeline` (two looks in flight, half a look apart) was tried on Targets: 21 of 32 against 29 of 29, 93 ticks a minute against 115. Jev takes about 870 ms instead of 400 when two requests run at once, so the decisions do not come faster, and every second one is made from a picture that is one look old. It stays in the code, off, for a faster back end.
