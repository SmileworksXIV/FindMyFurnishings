# Find My Furnishings

A Dalamud plugin for FFXIV that keeps a searchable index of your furnishings across all
character inventories and estates that you have house editing privileges to. It shows what
is placed, what is in a storeroom, and what is in your bags, saddlebag or retainers.

The game only holds furnishing data for the property you are standing in, so this is an
index built from visits rather than a live query. Open a property's Housing window and it
records itself. Every list shows when it was recorded.

## Installing

Dalamud → `/xlsettings` → Experimental → Custom Plugin Repositories, add:

```
https://raw.githubusercontent.com/SmileworksXIV/FindMyFurnishings/main/FindMyFurnishings/pluginmaster.json
```

Then `/xlplugins` → search for **Find My Furnishings** → Install.

## Using it

| Command | Does |
|---|---|
| `/fmf` or `/fsearch` | Open the window |
| `/fmf <text>` | Search |
| `/fmf here` | Record this property now |
| `/fmf bags` | Rescan bags, saddlebag and retainers |
| `/fmf dump` | Write diagnostics to the log, for reporting a problem |

Full instructions are on the plugin's own Instructions tab.

## What it records, and what it will not

Records estates you own, plus guest estates and Free Company estates you have housing
privileges for. Guest estates and Free Company estates can each be turned off in Settings.
A guest estate is recorded by its address, never by its owner's name.

Only furnishing items are indexed. Everything else in your bags and retainers is ignored.

Everything the plugin knows lives in a configuration file on your own computer and is
never sent anywhere. You can remove any estate or container under the Removal tab.

## Building

```
cd FindMyFurnishings
dotnet build -c Release
```

To produce a distributable zip, unload the plugin in game first, because Windows will not
let the zip step replace a folder in use:

```
dotnet build -c Release -p:FmfPackage=true
```

See `RELEASING.md` for publishing a new version.

## Licence

MIT. See `LICENSE`.
