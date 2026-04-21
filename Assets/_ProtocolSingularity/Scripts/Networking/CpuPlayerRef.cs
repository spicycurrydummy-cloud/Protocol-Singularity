using Fusion;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// CPU プレイヤーに割り当てる synthetic PlayerRef を提供する。
    /// 実クライアントが使わない高 index 帯 (200..209) を予約することで、
    /// LeaderOrder / ProposedTeam / Chat など PlayerRef を値として持つ
    /// NetworkArray や RPC に CPU をそのまま混在させられる。
    /// 実クライアントへの RPC は送られないため、CPU 宛ての [RpcTarget] 呼び出しは
    /// 呼び出し側でスキップすること。
    /// </summary>
    public static class CpuPlayerRef
    {
        public const int BaseIndex = 200;
        public const int MaxCpuCount = 10;

        public static PlayerRef FromSlot(int slot) => PlayerRef.FromIndex(BaseIndex + slot);

        public static bool IsCpu(PlayerRef pr)
        {
            int id = pr.PlayerId;
            return id >= BaseIndex && id < BaseIndex + MaxCpuCount;
        }

        public static int ToSlot(PlayerRef pr) => pr.PlayerId - BaseIndex;
    }
}
