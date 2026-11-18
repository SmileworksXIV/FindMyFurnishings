using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FindMyFurnishings;

/// <summary>
/// Whether a housing window is open.
///
/// This is the gate on recording a property, and it exists because the housing containers
/// lie. They keep the last estate's contents and still report themselves as loaded, so
/// walking from a Free Company hall into its private chamber and reading them recorded the
/// hall's 777 furnishings as the chamber's. The containers are only trustworthy for the
/// property you are in once the game has opened a housing window for it.
///
/// Several addon names are tried because only the game knows what it calls these windows;
/// a name that does not exist simply never matches, which fails safe by recording nothing
/// rather than recording the wrong thing. /fmf dump prints whichever ones are open.
///
/// Only windows the game opens for somebody who may EDIT the property belong here, because
/// this list is also the access check. It used to include the estate placard, the ward and
/// room pickers and the chocobo saddlebag - windows anyone can open standing in anyone's
/// house - and with one of those up, the stale containers described above were filed under
/// a property that was merely being visited.
/// </summary>
public static class HousingUi
{
    public static readonly string[] Candidates =
    [
        "HousingGoods",
        "HousingFurniture",
        "HousingLayout",
        "HousingEditInterior",
        "HousingEditExterior",
        "HousingStoreroom",
    ];

    /// <summary>
    /// Every housing-related window /fmf dump reports on, including the ones that prove
    /// nothing about editing rights. For diagnosis only; never a reason to record.
    /// </summary>
    public static readonly string[] Diagnostics =
    [
        .. Candidates,
        "HousingSelectBlock",
        "HousingSelectRoom",
        "HousingPortal",
        "HousingConfig",
        "HousingSignBoard",
        "InventoryBuddy",
    ];

    public static unsafe bool IsOpen(IGameGui gui, out string which)
    {
        which = string.Empty;

        foreach (var name in Candidates)
        {
            try
            {
                var ptr = gui.GetAddonByName(name);
                if (ptr == nint.Zero) continue;

                var addon = (AtkUnitBase*)ptr.Address;
                if (addon == null || !addon->IsVisible) continue;

                which = name;
                return true;
            }
            catch
            {
                // An unknown addon name is not an error; it just never matches.
            }
        }

        return false;
    }
}
