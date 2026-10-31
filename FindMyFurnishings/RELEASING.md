# Giving this to people, and updating it

Two ways. The first takes five minutes and has no auto-update; the second is how
third-party Dalamud plugins are normally distributed and updates itself.

---

## 1. Quick: hand them a zip

Unload the plugin in game first (or close the game) - Windows will not let the zip step
replace a folder that is in use.

```
cd D:\Users\Kaydie\Desktop\Twirlenkiller\XIV\Plugins\FindMyFurnishings\FindMyFurnishings
dotnet build -c Release -p:FmfPackage=true
```

That writes `bin\Release\FindMyFurnishings\latest.zip`, built by DalamudPackager in the
layout Dalamud expects. Send them that file.

They unzip it anywhere, then in game:

- `/xlsettings` -> Experimental -> Dev Plugin Locations
- Add the folder they unzipped into
- `/xlplugins` -> Dev Tools -> Installed Dev Plugins -> enable Find My Furnishings

To update them, send a new zip and they replace the files. No notification, no version
check - which is why this is the five-minute option and not the real one.

---

## 2. Proper: a custom plugin repository

They paste one URL into Dalamud once, and from then on your plugin appears in their
plugin installer and updates itself whenever you publish a new version.

### One-time setup

1. Create a public GitHub repository, e.g. `FindMyFurnishings`, and push this folder to
   it. It has to be public - Dalamud fetches the repo URL anonymously and "does not
   support authentication or authorization". The MIT licence is already here, and open
   source is required for the official Dalamud repo later anyway.

2. Edit `pluginmaster.json` and replace every `SmileworksXIV` with your GitHub
   username. Commit it.

3. The URL your friends need is the raw link to that file:

   ```
   https://raw.githubusercontent.com/SmileworksXIV/FindMyFurnishings/main/FindMyFurnishings/pluginmaster.json
   ```

### Publishing a version

1. Bump `<Version>` in `FindMyFurnishings.csproj` (e.g. `0.1.1`).
2. Unload the plugin in game, then:
   ```
   dotnet build -c Release -p:FmfPackage=true
   ```
3. Make a GitHub release tagged with the four-part version (`0.1.1.0`) and attach
   `bin\Release\FindMyFurnishings\latest.zip` to it.
4. In `pluginmaster.json`, update:
   - `AssemblyVersion` -> `0.1.1.0`
   - the `0.1.0.0` in all three `DownloadLink...` URLs -> `0.1.1.0`
   - `LastUpdate` -> the current unix timestamp
5. Commit and push.

Dalamud picks it up on its next refresh and offers your friends the update.

### What your friends do, once

- `/xlsettings` -> Experimental -> Custom Plugin Repositories
- Paste the `pluginmaster.json` URL, press +, Save
- `/xlplugins` -> search for Find My Furnishings -> Install

---

## Notes

- `AssemblyVersion` must match what the build actually produced, or Dalamud will not
  recognise the update. `<Version>0.1.1</Version>` becomes `0.1.1.0`, because
  DalamudPackager writes four version components by default.
- `IconUrl` points at the icon already committed in `images/`. It must be a direct image
  link (`raw.githubusercontent.com/...`), not a `github.com/.../blob/...` page.
- `DownloadLinkTesting` currently points at the same zip as the release. Change it only
  if you want a separate testing channel.
- The repo list is a JSON array even with one plugin in it. Keep the brackets.
