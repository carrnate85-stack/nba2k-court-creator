# Court Creator Workflow

## Sharing Updates

- After each verified app update, commit and push the relevant changes to the
  configured GitHub remote for sharing. This is the user's standing preference.
- Use ordinary commits and pushes. Never force-push, rewrite shared history, or
  revert unrelated local changes. Resolve remote divergence without discarding work.
- Check the staged files before publishing. Do not commit credentials, Python
  runtimes, extracted game assets, personal artwork/projects, caches or build outputs.
- Source pushes do not imply publishing GitHub release assets; that is a separate
  operation when requested. Report the commit and whether the push succeeded.
- If verification or pushing fails, report it clearly; do not claim the update is
  shared or push known-broken changes just to satisfy the routine.

## Local Workflow

- Do not launch the app after changes. Off-screen checks are allowed; do not use
  the smoke runner's `--native-windows` option without fresh permission.
- Keep the desktop launcher working and include its link in the final response:
  `C:/Users/carrn/Desktop/NBA 2K Court Creator.lnk`.
- Preserve existing local changes and personal files.
