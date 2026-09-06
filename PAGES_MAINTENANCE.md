# Behind Bars Pages maintenance

This file is for repository maintainers and automation working on the `gh-pages` source. It is intentionally not linked from the rendered site.

## Public-content boundary

- Write rendered pages for players or contributors, not for release management or agent coordination.
- Keep internal source, build, deployment, package, and live-game verification distinctions out of player-facing copy unless a reader needs a specific safety instruction.
- Do not link to `AGENTS.md`, task instructions, internal checklists, worktree paths, or unpublished release preparation from the public pages.
- Keep release dates, supported game versions, and feature availability factual and short.

## Updating current status

1. Verify the active source branch, commit, working-tree state, target game version, and relevant release information before changing a claim.
2. Replace old status text instead of accumulating historical project notes.
3. Keep available features, active work, and unreleased work in separate reader-facing sections.
4. Do not call a feature released, live-verified, or multiplayer-ready from source or build results alone.
5. Record implementation, build, deployed-artifact, and live-game evidence in repository work or release records rather than the public status page.

## Publishing

1. Review the `gh-pages` branch changes and preview the site locally.
2. Commit only the intended Pages source changes.
3. Push the branch, then configure GitHub Pages to deploy `gh-pages` from `/(root)` if it is not already configured.
