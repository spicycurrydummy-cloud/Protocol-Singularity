using System;
using System.Collections.Generic;
using System.Linq;
using ProtocolSingularity.Core;
using ProtocolSingularity.Data;

namespace ProtocolSingularity.Gameplay
{
    /// <summary>
    /// プレイヤー数と RoleDistributionConfig から役職一覧をシャッフルして割り当てる。
    /// </summary>
    public static class RoleAssigner
    {
        public static RoleType[] Assign(RoleDistributionConfig config, int playerCount, int randomSeed)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var entry = config.GetEntry(playerCount);
            if (entry == null)
                throw new ArgumentException($"No role distribution defined for {playerCount} players.");
            return Assign(entry, randomSeed);
        }

        /// <summary>指定 Entry (ロビーで動的構築されたものを含む) から役職をシャッフルして返す。</summary>
        public static RoleType[] Assign(RoleDistributionConfig.Entry entry, int randomSeed)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var list = new List<RoleType>(entry.BuildRoleList());
            var rng = new Random(randomSeed);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return list.ToArray();
        }
    }
}
