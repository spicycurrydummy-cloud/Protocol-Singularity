using UnityEngine;

namespace ProtocolSingularity.Data
{
    [CreateAssetMenu(fileName = "GameSettings", menuName = "Protocol Singularity/Game Settings")]
    public class GameSettings : ScriptableObject
    {
        [Header("ラウンド設定")]
        [Min(1)] public int requiredHackSuccess = 3;
        [Min(1)] public int requiredHackFailure = 3;
        [Min(1)] public int maxConsecutiveRejections = 5;
        [Min(1)] public int teamSize = 3;
        [Min(1)] public int droneAwakenRound = 2;

        [Header("制限時間（秒）")]
        [Min(5)] public int teamProposalSeconds = 60;
        [Min(5)] public int approvalVoteSeconds = 30;
        [Min(5)] public int hackingSeconds = 30;
        [Min(5)] public int roundResultSeconds = 10;
        [Min(5)] public int overrideDiscussionSeconds = 90;
        [Min(5)] public int overrideVoteSeconds = 30;

        [Header("CPU")]
        public bool enableCpuFill = true;
        [Min(1)] public int cpuThinkSeconds = 15;
    }
}
