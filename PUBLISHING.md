# Publishing the midnightOS website

The site is 9 plain HTML pages plus `assets/`. It needs no build step to host, so GitHub Pages
can serve it as it is. `NAME` is your GitHub user name and `REPO` is the repository name.

## Pages

| Page | What's on it |
|---|---|
| `index.html` | Overview: hero, feature showcase, feature cards, requirements summary, short FAQ |
| `features.html` | Every feature, by area |
| `screenshots.html` | Screenshot gallery |
| `specs.html` | System requirements, hardware support, what's inside |
| `download.html` | ISO and Windows Setup (both "Coming soon" for now) |
| `install.html` | Step-by-step install and troubleshooting |
| `release-notes.html` | What's new, fixed and known issues |
| `help.html` | FAQ and keyboard shortcuts |
| `about.html` | Open-source credits, privacy and trademarks |

All page text lives in `tools/make-site.py`. Edit it there and run `python3 tools/make-site.py`
to rewrite the pages; don't edit the `.html` files by hand or the next run will undo it.

## 1. Put the site on GitHub Pages

1. Create a public repository `REPO` on GitHub.
2. Upload everything in this `website` folder to the root of the repository, including the
   empty `.nojekyll` file. `.gitignore` keeps `downloads/` out, since Setup isn't released yet.
3. Open **Settings › Pages**, choose **Deploy from a branch**, branch `main`, folder `/ (root)`,
   and select **Save**.
4. After a minute the site is at `https://NAME.github.io/REPO/`.

## 2. When the ISO is ready

The ISO is one file of about 3 GB. GitHub releases stop at 2 GB per file, so host the ISO
somewhere that takes a single large file, for example Cloudflare R2 (no download fees) or
SourceForge (free, with mirrors). Then:

1. Upload the ISO and note its address.
2. In `tools/make-site.py`, in `download()`, replace the first
   `<span class="btn btn-disabled" aria-disabled="true">Coming soon</span>` with
   `<a class="btn btn-primary" href="ISO-ADDRESS">Download</a>`, and change the
   `Coming soon` tag next to the version. In `release_notes()`, change the status row too.
3. Add the SHA-256 (`sha256sum file.iso`) to the ISO row's `meta` line so people can check it.
4. Run `python3 tools/make-site.py` and upload the changed pages.

## 3. When Setup for Windows is ready

Setup reads a `release.json` from the site that lists where to get the ISO.

1. Create `website/site-config.txt` with one line: `https://NAME.github.io/REPO`
2. Split the ISO into 3 parts and make `release.json`:
   `python3 tools/split-release.py ISO out --base-url https://github.com/NAME/REPO/releases/download/TAG`
   Upload `out/*.part1..3` to that GitHub release (each part is about 1 GB, under the 2 GB limit) and
   put `out/release.json` next to `index.html`. For an ISO hosted as one file instead:
   `python3 tools/split-release.py ISO out --single-url ISO-ADDRESS`.
3. Run `website/downloader/build.cmd` on Windows. It writes `website/downloads/midnightOS-Setup.exe`.
4. Remove `downloads/` from `.gitignore`, swap Setup's "Coming soon" button for a link to
   `downloads/midnightOS-Setup.exe` in `tools/make-site.py`, rerun it and upload.

## Notes

- Windows SmartScreen may warn about Setup until it's code-signed.
- Secure Boot must be off to start the preview on a real PC; the install page says so.
- To test Setup's download without a window:
  `midnightOS-Setup.exe --test-download https://NAME.github.io/REPO/release.json C:\Temp\iso`
