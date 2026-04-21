using System.Collections.Generic;
using ProtocolSingularity.Core;
using UnityEngine;

namespace ProtocolSingularity.Data
{
    [CreateAssetMenu(fileName = "RoleDistributionConfig", menuName = "Protocol Singularity/Role Distribution Config")]
    public class RoleDistributionConfig : ScriptableObject
    {
        [System.Serializable]
        public class Entry
        {
            [Min(1)] public int playerCount;
            public bool includeOracle = true;
            public bool includeAdmin = true;
            public bool includeMotherCore = true;
            [Min(0)] public int operatorCount;
            [Min(0)] public int agentCount;
            public bool includeCipher;
            public bool includeDrone;
            public bool includeRadical;

            public int TotalHumans => (includeOracle ? 1 : 0) + (includeAdmin ? 1 : 0) + operatorCount;
            public int TotalAI => (includeMotherCore ? 1 : 0) + agentCount + (includeCipher ? 1 : 0) + (includeDrone ? 1 : 0) + (includeRadical ? 1 : 0);
            public int TotalPlayers => TotalHumans + TotalAI;

            public IReadOnlyList<RoleType> BuildRoleList()
            {
                var list = new List<RoleType>();
                if (includeOracle) list.Add(RoleType.Oracle);
                if (includeAdmin) list.Add(RoleType.Admin);
                if (includeMotherCore) list.Add(RoleType.MotherCore);
                for (int i = 0; i < operatorCount; i++) list.Add(RoleType.Operator);
                for (int i = 0; i < agentCount; i++) list.Add(RoleType.Agent);
                if (includeCipher) list.Add(RoleType.Cipher);
                if (includeDrone) list.Add(RoleType.Drone);
                if (includeRadical) list.Add(RoleType.Radical);
                return list;
            }
        }

        [SerializeField] private List<Entry> entries = new();

        public Entry GetEntry(int playerCount)
        {
            return entries.Find(e => e.playerCount == playerCount);
        }

        public bool IsSupportedCount(int playerCount) => GetEntry(playerCount) != null;

        public IReadOnlyList<Entry> Entries => entries;

        /// <summary>
        /// CLAUDE.md §2.3 のデフォルト配分表を適用する。既存の entries は上書き。
        /// 1〜3人のデバッグエントリも含まれる（ソロ/少人数テスト用、本番では 6〜10 人を想定）。
        /// </summary>
        [ContextMenu("Reset to CLAUDE.md defaults")]
        public void ResetToDefaults()
        {
            entries.Clear();
            // === Debug: solo test entries (production は 6-10 人) ===
            entries.Add(new Entry { playerCount = 1, includeOracle = true,  includeAdmin = false, includeMotherCore = false });
            entries.Add(new Entry { playerCount = 2, includeOracle = true,  includeAdmin = false, includeMotherCore = true  });
            entries.Add(new Entry { playerCount = 3, includeOracle = true,  includeAdmin = true,  includeMotherCore = true  });
            entries.Add(new Entry { playerCount = 4, includeOracle = true,  includeAdmin = true,  includeMotherCore = true, operatorCount = 1 });
            entries.Add(new Entry { playerCount = 5, includeOracle = true,  includeAdmin = true,  includeMotherCore = true, operatorCount = 1, agentCount = 1 });
            // === CLAUDE.md §2.3 standard ===
            entries.Add(new Entry { playerCount = 6,  operatorCount = 2, agentCount = 1, includeCipher = false, includeDrone = false, includeRadical = false });
            entries.Add(new Entry { playerCount = 7,  operatorCount = 2, agentCount = 1, includeCipher = true,  includeDrone = false, includeRadical = false });
            entries.Add(new Entry { playerCount = 8,  operatorCount = 3, agentCount = 0, includeCipher = true,  includeDrone = true,  includeRadical = false });
            entries.Add(new Entry { playerCount = 9,  operatorCount = 4, agentCount = 0, includeCipher = true,  includeDrone = true,  includeRadical = false });
            entries.Add(new Entry { playerCount = 10, operatorCount = 4, agentCount = 0, includeCipher = true,  includeDrone = true,  includeRadical = true  });
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
    }
}
