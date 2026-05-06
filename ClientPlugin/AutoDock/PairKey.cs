using System;

namespace ClientPlugin;

internal readonly struct PairKey : IEquatable<PairKey>
{
    private readonly long localEntityId;
    private readonly long targetEntityId;

    public PairKey(long localEntityId, long targetEntityId)
    {
        this.localEntityId = localEntityId;
        this.targetEntityId = targetEntityId;
    }

    public bool Equals(PairKey other)
    {
        return localEntityId == other.localEntityId && targetEntityId == other.targetEntityId;
    }

    public override bool Equals(object obj)
    {
        return obj is PairKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (localEntityId.GetHashCode() * 397) ^ targetEntityId.GetHashCode();
        }
    }
}
