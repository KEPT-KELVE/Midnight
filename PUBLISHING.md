# Publishing the midnightOS website on GitHub

The site is plain HTML, CSS and JavaScript, so GitHub Pages can host it as it is. Below,
`NAME` is your GitHub user name and `REPO` is the repository name (for example `midnightos`).

## 1. Put the site on GitHub Pages

1. Create a public repository `REPO` on GitHub.
2. Upload everything in this `website` folder to the root of the repository, including the empty
   `.nojekyll` file. The `downloader` folder is source code and can stay or go.
3. In the repository, open **Settings › Pages**. Under *Build and deployment*, choose
   **Deploy from a branch**, then branch `main` and folder `/ (root)`. Click **Save**.
4. After a minute the site is at `https://NAME.github.io/REPO/`.

## 2. Upload the ISO to a release

GitHub release files can be at most 2 GB and the ISO is about 2.8 GB, so cut it into parts first.
In WSL or Linux, from the project folder:

    python3 tools/split-release.py /path/to/midnightOS-…iso out/release \
        --base-url https://github.com/NAME/REPO/releases/download/dp1 \
        --version "Developer Preview 1"

This writes `midnightOS-….iso.part1`, `.part2` and `release.json` into `out/release`.

1. On GitHub, open **Releases › Draft a new release**. Create the tag `dp1` (it must match the
   end of `--base-url`), give it a title, and attach every `.part` file. Publish it.
2. Copy `out/release/release.json` into this `website` folder, next to `index.html`, and upload
   it to the repository too.

The website now lists the parts with their sizes and the SHA-256, and Setup can download them.

## 3. Point the Windows downloader at your site

Setup reads `release.json` from your site. Tell it where that is, then rebuild it on Windows:

1. Create `website/site-config.txt` containing one line: `https://NAME.github.io/REPO`
2. Double-click `website/downloader/build.cmd` (or run it from a Command Prompt).
3. Upload the new `website/downloads/midnightOS-Setup.exe` to the repository.

You only need to rebuild Setup if the site's address changes. For a new ISO, repeat step 2 with
a new tag (for example `dp2`) and replace `release.json`.

## Notes

- Windows SmartScreen may warn that Setup is from an unknown publisher until it's code-signed.
- Turning off **Secure Boot** is needed to start the preview on a real PC.
- To test the download path without a window:
  `midnightOS-Setup.exe --test-download https://NAME.github.io/REPO/release.json C:\Temp\iso`
  writes `midnight-setup-test.log` in that folder.
