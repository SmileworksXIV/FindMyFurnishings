# Find My Furnishings

A Dalamud plugin for FFXIV that keeps a searchable index of your furnishings across every
estate you own — what is placed, what is in a storeroom, and what is sitting in your bags,
retainers or the Free Company chest.

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
| `/fmf bags` | Rescan bags, saddlebag, retainers and the chest |
| `/fmf dump` | Write diagnostics to the log, for reporting a problem |

Full instructions are on the plugin's own Instructions tab.

## What it records, and what it will not

Only estates you own, or a Free Company estate you have editing access to. **Shared
estates are never recorded** — access is not ownership, and what is in somebody else's
house is not this plugin's business. Walking into one also deletes anything recorded about
it before that rule existed.

Only furnishing items are indexed. Everything else in your bags and chests is ignored.

Everything the plugin knows lives in a configuration file on your own computer and is
never sent anywhere.

## Building

```
cd FindMyFurnishings
dotnet build -c Release
```

To produce a distributable zip (unload the plugin in game first — Windows will not let the
zip step replace a folder in use):

```
dotnet build -c Release -p:FmfPackage=true
```

See `RELEASING.md` for publishing a new version.

## Licence

MIT. See `LICENSE`.
