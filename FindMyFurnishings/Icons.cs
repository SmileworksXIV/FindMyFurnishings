using System;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace FindMyFurnishings;

/// <summary>
/// Draws game item icons.
///
/// Every texture call in the plugin goes through here. Dalamud's texture API has moved
/// around between versions, so keeping it to one method means a signature change is one
/// edit rather than a hunt through the UI code.
/// </summary>
public static class Icons
{
    public static ITextureProvider? Provider;

    public static void Draw(uint iconId, float size)
    {
        if (Provider == null || iconId == 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }

        try
        {
            var wrap = Provider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(size, size));
        }
        catch
        {
            // A missing icon is not worth an exception in a draw loop.
            ImGui.Dummy(new Vector2(size, size));
        }
    }
}
