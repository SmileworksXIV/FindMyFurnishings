# Find My Furnishings

Don't know where you put that Goobbue Lamp? Need to know how many Troupe Stages you have stored across all of your estates? By default, players cannot search housing items stored or placed in estates. Find My Furnishings fixes that. This one's for the housing mains.

## What it does

Find My Furnishings is your housing item hub. It keeps a searchable index of your furnishing items across all character inventories and estates that you have house editing privileges to. It shows what is placed, what is in a storeroom, and what is in your bags, saddlebag and retainers. Furnishings are logged across all account-wide alt characters regardless of World and Data Center.

Users can also create, organize, and save lists that log the location of your furnishings. Useful for seasonal builds!

Locations where FMF will index furnishings:
- Owned Private Estates
- Indoor and outdoor placed items
- Indoor and outdoor storerooms
- Retainers
- Character inventory
- Chocobo saddlebag
- Apartment
- Private Chambers (Free Company)

If you have housing editing privileges to non-owned estates, it will also (optionally) log:
- Guest estates
- Free Company estates

## Installing - Dalamud Plugin

Find My Furnishings is a plugin compatible with Dalamud.

Dalamud → `/xlsettings` → Experimental tab → Custom Plugin Repositories, add: ``` https://raw.githubusercontent.com/SmileworksXIV/FindMyFurnishings/main/FindMyFurnishings/pluginmaster.json```  → Save

Then `/xlplugins` → search for **Find My Furnishings** → Install.

## Using it

FMF will scan items when the in-game housing menus are opened. Just open the housing menu and flip through your estate placed items list, storage, inventories, interior, exterior, and retainers.

Bring up the plugin with ``/fmf`` in-game for full instructions and a command list.

## What it records, and what it will not

**FMF only records estates you have editing privileges to.** This includes your own plus guest estates and Free Company estates that you can edit. Guest estate recording can be turned off in settings.
A guest estate is recorded by its address, never by its owner's name.

All data is stored locally in a file on your own computer and is never sent anywhere. You can remove any estate or container under the Removal tab anytime.

## License

MIT. See `LICENSE`.
