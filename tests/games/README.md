# Test games for PcPlay

Two small pages with an exact score, so that a run can be judged without anybody's opinion: the page title carries the result
(`Targets — FINISHED score 29 of 29 green, wrong 0`, `Colors — FINISHED correct 15 of 15`).

- `targets.html`: green circles (to click) and red ones (to leave alone) appear for 1.4 s each; 45 s.
- `colors.html`: a colour is named in words, the four buttons change places every round and carry random words as labels; 15 rounds.

Run one (needs a Jev key and `[pc]` consent; the run takes the mouse for the time it lasts):

```
msedge.exe --app=file:///C:/path/to/tests/games/targets.html --window-size=1100,720 --user-data-dir=%TEMP%\kairo-game-profile
kairo pc play tests/games/targets.policy.json Targets --dry     # look once, show what the rules would do
kairo pc play tests/games/targets.policy.json Targets           # play; the Pause key stops it
```

Measured on 2026-10-16 (Jev 1.14, JPEG pictures of 1024 px): Targets 29 of 29 and no red click, 119 ticks in 62 s (517 ms a tick, 403 ms of it Jev);
Colors 15 of 15, 17 ticks in 12 s (701 ms a tick; five things to find, a choice and a check in every look).
