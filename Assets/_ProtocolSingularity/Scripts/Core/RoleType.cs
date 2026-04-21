namespace ProtocolSingularity.Core
{
    public enum RoleType
    {
        Oracle = 0,
        Admin = 1,
        Operator = 2,
        MotherCore = 10,
        Agent = 11,
        Cipher = 12,
        Drone = 13,
        Radical = 14,
        /// <summary>
        /// 実在しない「汎用 AI」表示用ラベル。プレイヤーには割り当てられない。
        /// Oracle 視点では全 AI 陣営 (Cipher 以外) がこの値で見える (役職は区別できず陣営だけ分かる)。
        /// </summary>
        AI = 15
    }

    public static class RoleTypeExtensions
    {
        public static Faction GetFaction(this RoleType role)
        {
            return role switch
            {
                RoleType.Oracle or RoleType.Admin or RoleType.Operator => Faction.Human,
                _ => Faction.AI
            };
        }

        public static bool IsHuman(this RoleType role) => role.GetFaction() == Faction.Human;
        public static bool IsAI(this RoleType role) => role.GetFaction() == Faction.AI;
    }
}
