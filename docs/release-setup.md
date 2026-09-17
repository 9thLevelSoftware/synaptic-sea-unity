# Release setup: itch.io, CI, macOS, Steam

Everything in the repo is ready. These four steps need an account or a secret, so they can only be done by the project owner. Do them in this order: itch.io is free and takes about 15 minutes, and the other three can wait.

| Step | Cost | Needed for | Do it |
|---|---|---|---|
| 1. itch.io | Free | Sharing builds with testers | Now |
| 2. macOS signing | Apple Developer Program, $99/year, and a Mac | macOS players without a security warning | Before a public macOS release |
| 3. CI (GitHub Actions) | Free for this repo size | Tests and builds on every push | Optional; local `tools/test.ps1` covers it |
| 4. Steam | $100 per game (Steam Direct) | A Steam store page | When you commit to a Steam launch |

## 1. itch.io (start here)

1. **Create the project page.** Sign in at itch.io, open Dashboard, and choose "Create new project".
   - Kind of project: Downloadable.
   - Visibility: Draft. Nobody can see a draft, so uploads are private until you publish.
   - Save. The page URL gives the project name butler needs: `https://<user>.itch.io/<game>` is `<user>/<game>`.
2. **Log butler in once.** butler is already installed. Run this and approve the browser prompt:
   ```
   F:\Tools\butler\butler.exe login
   ```
   The key is stored in your user profile, never in the repo.
3. **Build and check without uploading.**
   ```
   pwsh tools/build.ps1 -Kind release
   pwsh tools/publish-itch.ps1 -Kind release
   ```
   The second command stages the build, runs `butler validate`, and prints the exact push command. Nothing is uploaded.
4. **Upload.**
   ```
   pwsh tools/publish-itch.ps1 -Kind release -Project <user>/<game> -Push
   ```
   The build lands on channel `win-rc`. For Linux use `-Target StandaloneLinux64` (channel `linux-rc`); for macOS use `-Target StandaloneOSX` (channel `mac-rc`). A demo build adds `-demo` to the channel. Later pushes to the same channel upload only what changed.
5. **Share it.** On the project page, set visibility to Restricted and add testers, or Public when ready.

The macOS build is unsigned (step 2). On itch that is acceptable for testers: they right-click the app and choose Open the first time.

## 2. macOS signing and notarisation

Apple blocks unsigned apps downloaded from the internet unless the player overrides the warning.

1. Join the Apple Developer Program ($99/year) and create a "Developer ID Application" certificate.
2. On a Mac with Xcode, sign the `.app` with `codesign --deep --options runtime`, then submit it with `xcrun notarytool submit --wait` and `xcrun stapler staple`.
3. Upload the signed app with `publish-itch.ps1` as above, from Windows or the Mac.

No Mac is available here, so this stays manual. A rented cloud Mac also works.

## 3. CI on GitHub Actions (optional)

The workflows in `.github/workflows` only run when started by hand (Actions tab, "Run workflow"). The dotnet job needs no license.

1. **Push the code.** `main` is ahead of `origin/main` on `github.com/9thLevelSoftware/synaptic-sea-unity`, and CI runs only what is on GitHub.
2. **Create a Unity license file.** In Unity Hub open Preferences, then Licenses, click Add, and choose "Get a free personal license". Hub then writes `C:\ProgramData\Unity\Unity_lic.ulf`. A license that already shows in Hub may not have written that file; clicking Add is what creates it. On this machine the file does not exist yet.
3. **Add three repository secrets** (GitHub repo, Settings, Secrets and variables, Actions). With the GitHub CLI, each command prompts for the value, so nothing lands in your shell history:
   ```
   gh secret set UNITY_EMAIL
   gh secret set UNITY_PASSWORD
   gh secret set UNITY_LICENSE < C:\ProgramData\Unity\Unity_lic.ulf
   ```
4. Start "Unity tests" from the Actions tab. When it passes, add `push:` triggers to the workflows if you want them automatic.

## 4. Steam

1. Sign up for Steamworks (partner.steamgames.com), complete the tax and bank forms, and pay the $100 Steam Direct fee. Steam gives the game an App ID.
2. Download Facepunch.Steamworks from its GitHub releases and copy the DLLs and native libraries into `SynapticSea/Assets/_Project/Platform/Steam/Plugins/` (see the README there; the folder is gitignored).
3. Add `SS_STEAM` to Player Settings, Scripting Define Symbols, and put `steam_appid.txt` with the App ID next to the built exe for local testing.
4. In Steamworks, create the 8 achievements with the same API names as `data/release/achievement_catalog.json` (`first_breath`, `first_repair`, `first_loot`, `objective_complete`, `reactor_stabilized`, `extracted`, `junction_calibrator_used`, `all_systems_restored`).
5. Uploading to Steam uses SteamPipe (steamcmd and a depot build script). That script is not written yet.
