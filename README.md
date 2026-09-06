# Behind Bars knowledge base

This is the standalone source for the Behind Bars GitHub Pages site. It is intentionally an orphan branch: it publishes documentation only and does not duplicate the mod's source tree.

## Publishing

1. Push the `gh-pages` branch after reviewing its commit.
2. In the repository's **Settings → Pages**, select **Deploy from a branch**.
3. Select `gh-pages` and the `/(root)` folder.

GitHub Pages needs no build step for this site. The `.nojekyll` marker prevents accidental Jekyll processing.

## Public content

The site deliberately separates:

- **Player guide** — installation, what the mod does, and player-facing caveats.
- **Developer guide** — code structure, Mono/IL2CPP rules, build commands, and verification gates.
- **Current status** — a plain-language update on what is available, what is in development, and what is not released yet.

The public pages should speak directly to players and contributors. Do not surface internal release procedure, source/build evidence rules, agent instructions, or page-maintenance policy in the rendered site.

See [PAGES_MAINTENANCE.md](PAGES_MAINTENANCE.md) for repository-only publishing and status-update guidance.
