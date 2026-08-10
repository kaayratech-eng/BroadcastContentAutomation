# Lessons

Corrections given during this work, so they don't have to be given twice.

## Background music

- **Instrumental only.** The original `music.mp3` had vocals; anything sung
  competes with the narrator for the same attention. Removed for that reason.
- **Much softer than felt right the first two times.** Two rounds of "still too
  loud" landed on `BackgroundMusicLufs = -36` against ~-19 LUFS narration.
- **Don't publish a re-render just because it finished.** The `counting-to-5` job
  is already live on all four platforms; re-publishing would duplicate it.

## Secrets

- `API_Keys.txt` and the scratch note files at the repo root must never be
  committed. Now in `.gitignore`; verified via `git log --all` that none had ever
  been committed, so no history rewrite was needed.

## Workflow

- Plan to `tasks/todo.md` and check in **before** implementing. Minimal-impact
  changes. Verify before marking anything done.
- Don't fabricate anything that can't actually be retrieved — say what's missing
  instead.
