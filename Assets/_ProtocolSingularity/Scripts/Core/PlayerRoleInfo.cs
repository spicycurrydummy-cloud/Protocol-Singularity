using System;

namespace ProtocolSingularity.Core
{
    [Serializable]
    public struct PlayerRoleInfo
    {
        public int playerId;
        public string displayName;
        public RoleType role;
        public bool isCpu;

        public Faction Faction => role.GetFaction();
    }
}
