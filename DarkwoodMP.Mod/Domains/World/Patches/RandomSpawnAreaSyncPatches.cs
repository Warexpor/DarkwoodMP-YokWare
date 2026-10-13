namespace DWMPHorde.Patches
{
    // RandomSpawnArea (interval prefab around Player.Instance) is not patched: the game has one,
    // the swamp lake's thunder (thunderArea_big_swamp_lake_02 → fx/thunder_01, a flash and its
    // sound 600-1500 away from the player). It is weather around each player, as in single
    // player, so every machine runs its own. Clients used to skip it as "host-authoritative",
    // which left a client in the swamp with no thunder at all: the host's is placed around the
    // host's body and is never sent.
}
