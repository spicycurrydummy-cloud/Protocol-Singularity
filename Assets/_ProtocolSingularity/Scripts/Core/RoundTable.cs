using UnityEngine;

namespace ProtocolSingularity.Core
{
    /// <summary>
    /// ラウンド毎のチームサイズ・必要 NOISE 枚数テーブル。Avalon 準拠を 3 ラウンドへ凝縮。
    /// 人数 6-10 に対応。それ以外は近い人数へフォールバック。
    /// </summary>
    public static class RoundTable
    {
        // [playerCount-6][round-1] = team size
        private static readonly int[,] TeamSizes = new int[5, 3]
        {
            { 2, 3, 4 }, //  6 players
            { 2, 3, 3 }, //  7
            { 3, 4, 4 }, //  8
            { 3, 4, 5 }, //  9
            { 3, 4, 5 }, // 10
        };

        // [playerCount-6][round-1] = NOISE 枚数の閾値 (>=N で FAIL)
        // Avalon の「7+ 人 4 ラウンド目は 2 fail 必要」を最終 R3 に移植 (8+ 人)。
        private static readonly int[,] RequiredNoises = new int[5, 3]
        {
            { 1, 1, 1 }, //  6
            { 1, 1, 1 }, //  7
            { 1, 1, 2 }, //  8
            { 1, 1, 2 }, //  9
            { 1, 1, 2 }, // 10
        };

        public static int GetTeamSize(int playerCount, int round)
        {
            int pIdx = Mathf.Clamp(playerCount, 6, 10) - 6;
            int rIdx = Mathf.Clamp(round, 1, 3) - 1;
            return TeamSizes[pIdx, rIdx];
        }

        public static int GetRequiredNoise(int playerCount, int round)
        {
            int pIdx = Mathf.Clamp(playerCount, 6, 10) - 6;
            int rIdx = Mathf.Clamp(round, 1, 3) - 1;
            return RequiredNoises[pIdx, rIdx];
        }

        /// <summary>LLM プロンプト用のテキスト表現。</summary>
        public static string DescribeForPlayerCount(int playerCount)
        {
            int pIdx = Mathf.Clamp(playerCount, 6, 10) - 6;
            var sb = new System.Text.StringBuilder(64);
            sb.Append("players=").Append(playerCount).Append(" | ");
            for (int r = 0; r < 3; r++)
            {
                if (r > 0) sb.Append(' ');
                sb.Append("R").Append(r + 1).Append(":team=")
                  .Append(TeamSizes[pIdx, r]).Append(",fail>=")
                  .Append(RequiredNoises[pIdx, r]);
            }
            return sb.ToString();
        }
    }
}
