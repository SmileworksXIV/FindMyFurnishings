# Find My Furnishings - build notes

```
cd D:\Users\Kaydie\Desktop\Twirlenkiller\XIV\Plugins\FindMyFurnishings\FindMyFurnishings
dotnet build -c Release
```

Then add `bin\Release` as a dev plugin location in Dalamud, same as the others.

## Commands

| Command | Does |
|---|---|
| `/fmf` | Opens the window |
| `/fmf here` | Records the property you are standing in, now |
| `/fmf bags` | Rescans bags, saddlebag and FC chest |
| `/fmf dump` | Writes diagnostics to `/xllog` |
| `/fmf <text>` | Opens the window to search |

## First run: verify the mapping before trusting any name

Placed furniture does not carry an item id. The plugin maps it:

```
HousingFurniture.Id  ->  sheet row (0x20000 | Id) indoors, (0x30000 | Id) outdoors
                     ->  that row's Item column  ->  Item name
```

That chain is confirmed from the client struct source and from one sheet row, but it has
not been checked against a live room. So before relying on it:

1. Stand **inside** an estate you can edit. Run `/fmf dump`.
2. Open `/xllog`, find the `[FMF] slot` lines, and compare a few names against what is
   actually in the room.
3. Do the same **outdoors** on a plot, which uses the other base value.

If names come out as `UNRESOLVED`, or resolve to the wrong furniture, send me the dump
lines - the raw ids in them are exactly what I need to correct the mapping.

## Two places most likely to need a one-line fix

Everything else is built on APIs already proven in SUNI. These two are not:

1. **`Icons.cs`** - `Provider.GetFromGameIcon(...).GetWrapOrEmpty().ImGuiHandle`.
   Dalamud's texture API has moved between versions. Every icon in the plugin draws
   through this one method, so if it fails to compile it is one edit.

2. **`FurnitureSheet.cs`** - `row.Item.RowId` on the `HousingFurniture` and
   `HousingYardObject` sheets. If Lumina types that column as a plain number rather than
   a row reference, drop the `.RowId`.

3. **`InventoryScanner.ActiveRetainerName()`** - `RetainerManager.Instance()`,
   `GetActiveRetainer()` and `NameString`. Verified working as of the retainer build.

4. **`HousingReader.EstateOf()`** - `Telepo.Instance()`, `UpdateAetheryteList()` and
   walking the returned vector. Verified working in game. Note this is now load-bearing:
   ownership is decided by the teleport list, so if it ever stops working the plugin
   records nothing rather than recording a guess.


## How properties get named

The teleport menu distinguishes Estate Hall (Private), Estate Hall (Free Company), Shared
Estate and Apartment. Nothing on `HousingManager` makes that distinction, but each teleport
destination carries an `EstateType` and the `HouseId` it belongs to - so the plugin matches
the house underfoot against that list and takes the game's own answer.

Once resolved it is cached on the snapshot, so a later visit with an empty teleport list
cannot undo a correct name. If no entry matches, naming falls back to what the address
alone can settle (apartment, chamber) and the section stays editable.

`/fmf dump` prints the whole teleport list with the current house marked, which is how to
check this is working.

## Storerooms and retainers fill themselves in

Neither is readable until the game loads it:

- **Storeroom.** On walking in, a property is recorded without its storeroom and marked
  incomplete. Open the storeroom window while standing there and the plugin notices the
  pages arriving and records it again, this time complete. Nothing to do by hand.
- **Retainers.** Read while a retainer is open at a bell. Each is stored under its own
  name, so working through several adds them all.

## What it does not do

- **Remote properties.** Not a gap that can be closed. The client only holds furnishing
  data for the zone it has loaded, so there is no way to ask about a house you are not
  standing in. Everything the plugin knows is a dated snapshot from a visit, and every
  list says when it was taken.

## Privacy

Two rules decide what may be recorded, and both are checked before anything is read:

- The estate must appear in your own teleport list, which is the game's list of the
  estates you hold. A friend's house you are merely visiting is not in it and indexes
  nothing. (`HasHousePermissions()` is NOT used for this - it returns false even on a
  plot you own, which is what stopped outdoor furnishings being recorded at all.)
- The estate must not be a **shared estate**. Access is not ownership, and what is in
  somebody else's house is not this plugin's to keep. Walking into one also deletes any
  record of it made before this rule existed - standing there is the only moment it can
  be identified.

The index is a local config file and is never sent anywhere.
