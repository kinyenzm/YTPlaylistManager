# YTPlaylistManager

This repository is ready for an **Angular (v22) frontend + .NET 8 backend** setup with GitHub Pages deployment support for the frontend.

## Can this be hosted on GitHub Pages?

- ✅ **Angular frontend**: yes (static files).
- ❌ **.NET 8 API/backend**: no (GitHub Pages does not run server-side apps).

Recommended deployment split:
- Frontend (Angular): GitHub Pages
- Backend (.NET 8): Azure App Service, Render, Railway, Fly.io, or similar

## Frontend deployment workflow

A GitHub Actions workflow is included at:

`.github/workflows/deploy-angular-gh-pages.yml`

It will:
1. Install Node dependencies in `frontend/`
2. Build Angular in production with `--base-href /YTPlaylistManager/`
3. Publish the generated static output to GitHub Pages

If your Angular app is not in `frontend/`, update `FRONTEND_DIR` in the workflow.

## Angular router fallback on GitHub Pages

For Angular routes to work on page refresh, make sure your build output contains both:
- `index.html`
- `404.html` (same content as `index.html`)

The workflow already creates `404.html` automatically from `index.html` before deploy.