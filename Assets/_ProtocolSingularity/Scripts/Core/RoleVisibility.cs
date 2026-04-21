namespace ProtocolSingularity.Core
{
    /// <summary>
    /// プレイヤー役職間の視認ルール解決。CLAUDE.md §2.4 に準拠。
    /// </summary>
    public static class RoleVisibility
    {
        /// <summary>
        /// viewer から見た target の表示役職を返す。ゲーム中の可視化情報に使用。
        /// OVERRIDE フェーズの相互開示は別途 <see cref="ResolveAtOverride"/> を使う。
        /// </summary>
        /// <param name="droneAwakened">覚醒済みドローンが AI として扱われる状態なら true</param>
        public static RoleType Resolve(RoleType viewer, RoleType target, bool droneAwakened)
        {
            if (viewer == target) return target;

            switch (viewer)
            {
                case RoleType.Oracle:
                    // Oracle は「陣営」のみ識別できる。個別役職は区別しない (全 AI は RoleType.AI 表示、全 Human は Operator 表示)。
                    // CIPHER は暗号化で Operator として表示される (唯一の盲点)。
                    if (target == RoleType.Cipher) return RoleType.Operator;
                    if (target.IsHuman()) return RoleType.Operator;
                    return RoleType.AI;

                case RoleType.Admin:
                    if (target == RoleType.Oracle || target == RoleType.MotherCore) return RoleType.Oracle;
                    return RoleType.Operator;

                case RoleType.MotherCore:
                case RoleType.Agent:
                case RoleType.Cipher:
                    // AI 同士は陣営のみ識別 (個別役職は分からない)。Drone 覚醒前とラディカルは見えない。
                    // OVERRIDE フェーズでは ResolveAtOverride 側で真の役職が開示される。
                    if (target == RoleType.Drone && !droneAwakened) return RoleType.Operator;
                    if (target == RoleType.Radical) return RoleType.Operator;
                    return target.IsAI() ? RoleType.AI : RoleType.Operator;

                case RoleType.Drone:
                    if (!droneAwakened) return RoleType.Operator;
                    if (target == RoleType.Radical) return RoleType.Operator;
                    return target.IsAI() ? RoleType.AI : RoleType.Operator;

                case RoleType.Radical:
                    return RoleType.Operator;

                case RoleType.Operator:
                default:
                    return RoleType.Operator;
            }
        }

        /// <summary>
        /// OVERRIDE フェーズでの相互開示。AI 陣営は全員（Radical含む）が相互に可視化される。
        /// </summary>
        public static RoleType ResolveAtOverride(RoleType viewer, RoleType target)
        {
            if (viewer.IsAI() && target.IsAI()) return target;
            return Resolve(viewer, target, droneAwakened: true);
        }
    }
}
