using UnityEngine;

namespace ProtocolSingularity.Core
{
    /// <summary>
    /// 役職ごとの表示色。陣営が一目で分かるようにプレイヤー名や役職ラベルに適用する。
    /// オラクルは専用色（黄）で他の人類陣営と区別する。
    /// </summary>
    public static class FactionColors
    {
        public static readonly Color Oracle = new Color(1.00f, 0.85f, 0.30f); // yellow
        public static readonly Color Human = new Color(0.55f, 0.72f, 1.00f);  // blue
        public static readonly Color AI = new Color(1.00f, 0.45f, 0.45f);     // red
        public static readonly Color Unknown = new Color(0.78f, 0.90f, 0.78f); // default terminal green

        public static Color ForRole(RoleType role)
        {
            if (role == RoleType.Oracle) return Oracle;
            if (role.IsAI()) return AI;
            return Human;
        }

        /// <summary>rich-text `<color=#RRGGBB>` 用の 16 進文字列</summary>
        public static string HexForRole(RoleType role)
        {
            var c = ForRole(role);
            return $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}";
        }
    }
}
