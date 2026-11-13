using Vintagestory.API.MathTools;

namespace SimpleNetwork.Blocks;

/// <summary>
/// Blocks that carry the network (cables, terminals, ...). The network scanner walks
/// through any block implementing this, but only across sides both blocks allow.
/// </summary>
public interface INetworkBlock
{
    /// <summary>
    /// Whether this block can link to whatever is on the given side of it
    /// (a cable, another network block, or a storage container).
    /// </summary>
    bool ConnectsOnFace(BlockFacing face);
}
