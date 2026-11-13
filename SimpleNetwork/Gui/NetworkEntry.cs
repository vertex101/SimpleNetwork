using Vintagestory.API.Common;

namespace SimpleNetwork.Gui;

/// <summary>
/// One cell in the terminal grid: either an item type stored in the network,
/// or (in recipe mode) a grid recipe, shown as its output.
/// </summary>
public class NetworkEntry
{
    /// <summary>Items mode: the serialized stack as the server sent it; sent back to identify the item when taking.</summary>
    public byte[]? Raw { get; }

    /// <summary>The item shown. In items mode StackSize = total amount in the network; in recipe mode = output amount.</summary>
    public ItemStack Stack { get; }

    /// <summary>A display-only slot used for rendering and tooltips. Not part of any real inventory.</summary>
    public DummySlot Slot { get; }

    public string Name { get; }

    public string NameLower { get; }

    /// <summary>Mod/domain the item comes from ("game" for vanilla), used for sorting by mod.</summary>
    public string Domain { get; }

    /// <summary>Recipe mode: index into the world's grid recipe list.</summary>
    public int RecipeIndex { get; } = -1;

    public GridRecipe? Recipe { get; }

    /// <summary>
    /// Recipe mode: whether the network seems to hold the ingredients. Worked out lazily (only for
    /// entries actually shown) and reset whenever the network contents change.
    /// </summary>
    public bool? Craftable { get; set; }

    public NetworkEntry(byte[] raw, ItemStack stack) : this(stack)
    {
        Raw = raw;
    }

    public NetworkEntry(int recipeIndex, GridRecipe recipe, ItemStack output) : this(output)
    {
        RecipeIndex = recipeIndex;
        Recipe = recipe;
    }

    private NetworkEntry(ItemStack stack)
    {
        Stack = stack;
        Slot = new DummySlot(stack);
        Name = stack.GetName() ?? stack.Collectible.Code.ToString();
        NameLower = Name.ToLowerInvariant();
        Domain = stack.Collectible.Code.Domain ?? "game";
    }
}
